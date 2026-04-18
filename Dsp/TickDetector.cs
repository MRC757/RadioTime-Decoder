namespace WwvDecoder.Dsp;

/// <summary>
/// Detects the 1000 Hz audio pulses broadcast by WWV at the start of each second.
///
/// WWV 1000 Hz channel (WWVH uses 1200 Hz):
///   Seconds 1–59: a 5 ms, 1000 Hz tone burst at the start of each second.
///   Second   0:   an 800 ms, 1000 Hz tone burst — the P0 minute marker.
///
/// The minute pulse is the most reliable frame anchor available. It is on a separate
/// channel from the 100 Hz BCD subcarrier, unaffected by the BCD modulation depth, and
/// louder than individual data pulses. Detecting it eliminates the need for the 9-second
/// P0→P1 inter-marker gap confirmation used when only the 100 Hz channel is available.
///
/// The second ticks provide sample-exact second-epoch timing, enabling the bipolar
/// matched filter from NTP driver 36 (sample I at 15 ms, 200 ms, 500 ms from tick)
/// as a future enhancement.
///
/// Algorithm:
///   Synchronous IQ demodulator at 1000 Hz with 150 Hz lowpass (τ ≈ 1.06 ms).
///   The short time constant resolves the 5 ms tick envelope while rejecting the
///   100 Hz subcarrier (which appears 900 Hz away from DC after down-mixing).
///   Adaptive level tracking: fast attack (2 ms τ) during tone presence, slow decay
///   (3 s τ) between pulses, so the reference holds across the ~1 s inter-tick gap.
///   Hysteresis thresholds: enter at 50% of levelHigh (or 8× noise floor before
///   levelHigh is established), exit at 25% of levelHigh (or 4× noise floor).
///
/// Pulse classification by measured duration at exit-threshold crossing:
///   ≤ 50 ms  → SecondTick  (nominal 5 ms; measured ≈ 6 ms after lowpass smearing)
///   ≥ 500 ms → MinutePulse (nominal 800 ms)
///   Other    → discarded   (no WWV pulse has an intermediate duration)
/// </summary>
public class TickDetector
{
    private readonly int _sampleRate;
    private readonly double _omega;        // 2π × carrierHz / sampleRate
    private readonly BandpassFilter _bandpass; // pre-filter to suppress out-of-band noise before IQ mixing
    private double _phase;
    private double _iFiltered, _qFiltered;
    private readonly double _lpAlpha;     // 150 Hz single-pole IIR lowpass coefficient

    // Noise floor: asymmetric tracker (fast fall, very slow rise) — same algorithm as
    // SynchronousDetector. Provides the floor for the bootstrap thresholds before
    // levelHigh is established on the first detected pulse.
    private double _noiseFloor = 0.001;
    private int _noiseCounter;
    private readonly int _noiseInterval; // samples between noise floor updates (~100 ms)

    // Tone amplitude reference.
    // Attack (during pulse, tone present): 2 ms τ — rises within a few ms of tone onset.
    //   In 5 ms (the tick duration): 1 - exp(-5/2) ≈ 92% of true amplitude. ✓
    // Decay (between pulses, tone absent): 3 s τ — holds reference across the 1 s inter-
    //   tick gap (decays to exp(-1/3) ≈ 72% after 1 s — still well above exit threshold).
    private double _levelHigh;
    private readonly double _highAttack; // per-sample coefficient, applied during pulse
    private readonly double _highDecay;  // per-sample coefficient, applied between pulses

    // Pulse detection state
    private bool _inPulse;
    private int _pulseSamples;
    private readonly int _minPulseSamples; // 4 ms — rejects noise bursts shorter than a real tick
    private readonly int _maxPulseSamples; // 1.1 s — safety cap

    // Per-type cadence guards — WWV has a fixed rate for each pulse type:
    //   SecondTick:  once per second — suppress anything within 500 ms of the last tick.
    //   MinutePulse: once per minute — suppress anything within 50 s of the last minute pulse.
    // The MinutePulse guard is the key defence against SDR voice/noise content whose
    // 1000 Hz envelope wanders above the threshold for 700–900 ms bursts, producing
    // a false MinutePulse every few seconds.  Real WWV fires exactly once per minute.
    private int _samplesSinceLastEmit;
    private int _samplesSinceLastMinutePulse;
    private readonly int _minCadenceSamples;        // 500 ms — SecondTick guard
    private readonly int _minMinuteCadenceSamples;  // 50 s  — MinutePulse guard

    public double CurrentEnvelope { get; private set; }
    public double LevelHigh  => _levelHigh;
    public double NoiseFloor => _noiseFloor;

    public event Action<TickEvent>? TickDetected;

    /// <param name="sampleRate">Audio sample rate in Hz.</param>
    /// <param name="carrierHz">Tone carrier frequency. 1000 Hz for WWV, 1200 Hz for WWVH.</param>
    public TickDetector(int sampleRate, double carrierHz = 1000.0)
    {
        _sampleRate = sampleRate;
        _omega = 2.0 * Math.PI * carrierHz / sampleRate;

        // 80 Hz bandpass pre-filter around the 1000 Hz tick carrier.
        // Rejects the 100 Hz BCD subcarrier leakthrough, power-line harmonics, and SDR
        // audio noise before IQ demodulation.  80 Hz bandwidth passes 960–1040 Hz and
        // survives ±10 Hz SDR LO error with > 3 dB headroom.
        _bandpass = new BandpassFilter(sampleRate, carrierHz, bandwidthHz: 80.0);

        // 150 Hz lowpass: RC = 1/(2π·150) ≈ 1.06 ms
        double rc = 1.0 / (2.0 * Math.PI * 150.0);
        double dt = 1.0 / sampleRate;
        _lpAlpha = dt / (rc + dt);

        _highAttack = 1.0 - Math.Exp(-1.0 / (0.002 * sampleRate)); // 2 ms
        _highDecay  = Math.Exp(-1.0 / (3.000 * sampleRate));        // 3 s

        // 4 ms minimum: genuine WWV tick after 150 Hz lowpass smearing measures ~5–8 ms.
        // 1–2 ms noise bursts from the 100 Hz 10th harmonic and SDR artifacts are rejected.
        _minPulseSamples         = sampleRate * 4    / 1000; // 4 ms
        _maxPulseSamples         = sampleRate * 1100 / 1000; // 1.1 s
        _noiseInterval           = sampleRate / 10;          // 100 ms
        _minCadenceSamples       = sampleRate / 2;           // 500 ms (SecondTick)
        _minMinuteCadenceSamples = sampleRate * 50;          // 50 s   (MinutePulse)
        // Pre-prime so the very first legitimate events are not suppressed.
        _samplesSinceLastEmit         = _minCadenceSamples;
        _samplesSinceLastMinutePulse  = _minMinuteCadenceSamples;
    }

    public void Reset()
    {
        _phase = 0;
        _iFiltered = _qFiltered = 0;
        _bandpass.Reset();
        _noiseFloor = 0.001;
        _noiseCounter = 0;
        _levelHigh = 0;
        _inPulse = false;
        _pulseSamples = 0;
        _samplesSinceLastEmit        = _minCadenceSamples;
        _samplesSinceLastMinutePulse = _minMinuteCadenceSamples;
    }

    public void ProcessBlock(float[] input)
    {
        foreach (float sample in input)
        {
            // Bandpass pre-filter: suppress out-of-band interference before IQ mixing.
            double bpOut = _bandpass.Process(sample);

            // IQ demodulation at carrierHz: mix with local reference, lowpass filter
            double iMixed = bpOut * Math.Cos(_phase);
            double qMixed = bpOut * Math.Sin(_phase);
            _phase += _omega;
            if (_phase >= Math.PI * 2.0) _phase -= Math.PI * 2.0;

            _iFiltered += _lpAlpha * (iMixed - _iFiltered);
            _qFiltered += _lpAlpha * (qMixed - _qFiltered);
            double envelope = 2.0 * Math.Sqrt(_iFiltered * _iFiltered + _qFiltered * _qFiltered);
            CurrentEnvelope = envelope;

            // Noise floor: fast fall (quickly tracks true quiet), very slow rise
            // (pulse amplitude cannot inflate the floor).
            if (++_noiseCounter >= _noiseInterval)
            {
                _noiseCounter = 0;
                if (envelope < _noiseFloor)
                    _noiseFloor = _noiseFloor * 0.90 + envelope * 0.10;
                else
                    _noiseFloor = _noiseFloor * 0.9995 + envelope * 0.0005;
                if (_noiseFloor < 1e-6) _noiseFloor = 1e-6;
            }

            // Level tracker: fast attack while tone is present (in pulse), slow decay
            // while idle. The opposite of PulseDetector (which tracks the 100 Hz carrier
            // HIGH level during non-pulse periods).
            if (_inPulse)
                _levelHigh += _highAttack * (envelope - _levelHigh);
            else
                _levelHigh *= _highDecay;

            // Adaptive hysteresis thresholds, floored at noise-floor multiples so
            // detection can bootstrap before levelHigh is established (cold start).
            //   Enter at 50% of levelHigh (or 8× noise): avoids false triggers up to 18 dB SNR.
            //   Exit  at 25% of levelHigh (or 4× noise): 6 dB dead-band prevents re-entry
            //     noise chattering while the tone is fading after the pulse ends.
            // Advance cadence counters every sample (clamped to avoid overflow).
            if (_samplesSinceLastEmit < _minCadenceSamples)
                _samplesSinceLastEmit++;
            if (_samplesSinceLastMinutePulse < _minMinuteCadenceSamples)
                _samplesSinceLastMinutePulse++;

            double enterThreshold = Math.Max(_levelHigh * 0.50, _noiseFloor * 8.0);
            double exitThreshold  = Math.Max(_levelHigh * 0.25, _noiseFloor * 4.0);

            if (!_inPulse)
            {
                if (envelope > enterThreshold)
                {
                    _inPulse = true;
                    _pulseSamples = 1;
                }
            }
            else
            {
                if (_pulseSamples > _maxPulseSamples)
                {
                    // Stuck in pulse state — discard and reset
                    _inPulse = false;
                    _pulseSamples = 0;
                }
                else if (envelope < exitThreshold)
                {
                    // Pulse ended — classify by duration
                    if (_pulseSamples >= _minPulseSamples)
                    {
                        double widthSeconds = (double)_pulseSamples / _sampleRate;
                        // MinutePulse minimum raised to 700 ms: WWV's minute pulse is
                        // nominal 800 ms. Requiring 700 ms rejects the 500–699 ms SDR audio
                        // bursts that are too short to be a real minute pulse, while still
                        // accepting the genuine 800 ms pulse even if slightly foreshortened
                        // by fading (exit-threshold crossing can occur before the tone ends).
                        TickType? type = widthSeconds switch
                        {
                            <= 0.050 => TickType.SecondTick,
                            >= 0.700 => TickType.MinutePulse,
                            _        => null  // intermediate duration — not a valid WWV tone
                        };
                        // Cadence guards:
                        //   SecondTick:  suppress if within 500 ms of any prior emission.
                        //   MinutePulse: suppress if within 50 s of the last minute pulse.
                        //     Real WWV fires exactly once per minute. Anything within 50 s
                        //     is SDR audio energy (voice, noise) whose 1000 Hz envelope
                        //     wanders above threshold for ~800 ms bursts, mimicking the pulse.
                        bool allowed = type switch
                        {
                            TickType.SecondTick  => _samplesSinceLastEmit >= _minCadenceSamples,
                            TickType.MinutePulse => _samplesSinceLastMinutePulse >= _minMinuteCadenceSamples,
                            _                    => false
                        };
                        if (type.HasValue && allowed)
                        {
                            TickDetected?.Invoke(new TickEvent(type.Value, widthSeconds));
                            _samplesSinceLastEmit = 0;
                            if (type == TickType.MinutePulse)
                                _samplesSinceLastMinutePulse = 0;
                        }
                    }
                    _inPulse = false;
                    _pulseSamples = 0;
                }
                else
                {
                    _pulseSamples++;
                }
            }
        }
    }
}

/// <summary>A detected event on the 1000 Hz WWV channel.</summary>
public record TickEvent(TickType Type, double WidthSeconds);

/// <summary>Classification of a 1000 Hz pulse.</summary>
public enum TickType
{
    /// <summary>5 ms tone burst at the start of each second (seconds 1–59).</summary>
    SecondTick,

    /// <summary>800 ms tone burst at the start of each minute (P0 — second 0).</summary>
    MinutePulse
}
