namespace WwvDecoder.Dsp;

/// <summary>
/// Synchronous (lock-in) detector for the WWV 100 Hz time-code subcarrier.
///
/// Replaces the bandpass filter + rectifier + envelope lowpass chain with a coherent
/// I/Q demodulator. SNR improvement over simple rectification is typically 15–25 dB
/// because this technique integrates energy at exactly 100 Hz while rejecting all
/// other frequencies proportionally to the integration time.
///
/// How it works:
///   1. Generate a local reference pair: cos(2π·100·t) and sin(2π·100·t)
///   2. Multiply the input by each reference → I (in-phase) and Q (quadrature) products
///   3. Lowpass filter I and Q with a very narrow cutoff (~3 Hz)
///      - After mixing, 100 Hz signal → near-DC (baseband)
///      - All other frequencies shift away from DC and are removed by the lowpass
///      - The narrower the lowpass, the better the SNR, at the cost of slower response
///   4. Envelope = 2 × √(I² + Q²)
///      - The factor of 2 restores amplitude (mixing halves the signal level)
///      - √(I² + Q²) is phase-independent — no need to synchronize to the carrier phase
///
/// Lowpass cutoff of 3 Hz:
///   - Time constant ≈ 53 ms; resolves the 200 ms zero-pulse edges cleanly
///   - Integrates over ~7 cycles of 100 Hz per time constant → 8 dB SNR gain
///     versus the 8 Hz envelope lowpass (which integrates ≈2 cycles)
///
/// Noise floor tracking uses the same asymmetric algorithm as EnvelopeDetector:
///   fast fall (tracks true noise level quickly) / very slow rise (pulse amplitude
///   cannot inflate the floor during the 0.8 s marker LOW period).
/// </summary>
public class SynchronousDetector
{
    private readonly int _sampleRate;
    private readonly double _subcarrierHz;
    private double _omega;       // 2π × (subcarrierHz + FrequencyOffsetHz) / sampleRate
    private double _lpAlpha;    // lowpass filter coefficient for I and Q channels

    private double _phase;               // current reference oscillator phase (radians, 0..2π)
    private double _iFiltered;          // first-pole lowpass output
    private double _qFiltered;
    private double _iFiltered2;         // second-pole lowpass output (coherent integration)
    private double _qFiltered2;

    /// <summary>Frequency offset applied to the local oscillator by the PLL (Hz).</summary>
    public double FrequencyOffsetHz
    {
        get => _freqOffsetHz;
        set
        {
            _freqOffsetHz = value;
            _omega = 2.0 * Math.PI * (_subcarrierHz + value) / _sampleRate;
        }
    }
    private double _freqOffsetHz;

    /// <summary>Lowpass cutoff frequency (Hz). Narrow after PLL locks to improve SNR.</summary>
    public double LowpassHz
    {
        set
        {
            double rc = 1.0 / (2.0 * Math.PI * value);
            double dt = 1.0 / _sampleRate;
            _lpAlpha = dt / (rc + dt);
        }
    }

    /// <summary>Two-pole lowpass in-phase output (read by CarrierPll for frequency estimation).</summary>
    public double IFiltered => _iFiltered2;

    /// <summary>Two-pole lowpass quadrature output (read by CarrierPll for frequency estimation).</summary>
    public double QFiltered => _qFiltered2;

    // Noise floor tracking (identical asymmetric logic to EnvelopeDetector)
    private double _noiseFloor = 0.001;
    private int _noiseCounter;
    private readonly int _noiseInterval; // samples between noise floor updates (~100 ms)

    public double NoiseFloor => _noiseFloor;

    public SynchronousDetector(int sampleRate, double subcarrierHz = 100.0, double lowpassHz = 8.0)
    {
        _sampleRate   = sampleRate;
        _subcarrierHz = subcarrierHz;
        _omega = 2.0 * Math.PI * subcarrierHz / sampleRate;

        // Single-pole IIR lowpass: α = dt / (RC + dt), RC = 1 / (2π·cutoff)
        double rc = 1.0 / (2.0 * Math.PI * lowpassHz);
        double dt = 1.0 / sampleRate;
        _lpAlpha = dt / (rc + dt);

        _noiseInterval = sampleRate / 10; // update every ~100 ms
    }

    public void Reset()
    {
        _phase        = 0;
        _iFiltered    = 0;
        _qFiltered    = 0;
        _iFiltered2   = 0;
        _qFiltered2   = 0;
        _noiseFloor   = 0.001;
        _noiseCounter = 0;
        _freqOffsetHz = 0;
        _omega = 2.0 * Math.PI * _subcarrierHz / _sampleRate;
    }

    public float[] ProcessBlock(float[] input)
    {
        var output = new float[input.Length];

        for (int i = 0; i < input.Length; i++)
        {
            double sample = input[i];

            // Step 1: mix with local reference (coherent demodulation)
            double iMixed = sample * Math.Cos(_phase);
            double qMixed = sample * Math.Sin(_phase);

            // Step 2: advance phase and wrap to [0, 2π) to prevent precision drift
            _phase += _omega;
            if (_phase >= Math.PI * 2.0) _phase -= Math.PI * 2.0;

            // Step 3: two-pole cascaded lowpass for coherent integration across carrier cycles.
            // The second pole gives 12 dB/oct roll-off vs 6 dB/oct for one pole, significantly
            // suppressing noise at frequencies well above the corner (e.g. pulse-rate harmonics).
            // Both poles share _lpAlpha so LowpassHz changes affect them identically.
            _iFiltered  += _lpAlpha * (iMixed    - _iFiltered);
            _qFiltered  += _lpAlpha * (qMixed    - _qFiltered);
            _iFiltered2 += _lpAlpha * (_iFiltered  - _iFiltered2);
            _qFiltered2 += _lpAlpha * (_qFiltered  - _qFiltered2);

            // Step 4: compute envelope from the first pole only (fast transitions needed for
            // PulseDetector threshold crossings).  The second-pole values are exposed via
            // IFiltered/QFiltered so the PLL gets a smoother IQ estimate — the benefit of
            // the extra pole — without slowing the envelope enough to push raw One durations
            // into Marker territory (~60 ms extra exit delay per pole at 2 Hz LP).
            double envelope = 2.0 * Math.Sqrt(_iFiltered * _iFiltered + _qFiltered * _qFiltered);
            output[i] = (float)envelope;

            // Step 5: update noise floor estimate every ~100 ms
            _noiseCounter++;
            if (_noiseCounter >= _noiseInterval)
            {
                _noiseCounter = 0;

                // Asymmetric tracking: fall quickly on quiet, rise very slowly on signal
                // Prevents pulse amplitude from inflating the noise floor baseline.
                if (envelope < _noiseFloor)
                    _noiseFloor = _noiseFloor * 0.90 + envelope * 0.10;  // fast fall
                else
                    _noiseFloor = _noiseFloor * 0.9995 + envelope * 0.0005; // very slow rise

                if (_noiseFloor < 1e-6) _noiseFloor = 1e-6;
            }
        }

        return output;
    }
}
