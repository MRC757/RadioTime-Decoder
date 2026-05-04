namespace WwvDecoder.Dsp;

/// <summary>
/// Detects and measures binary pulses from the amplitude envelope.
///
/// WWV signal model (positive-pulse / NIST IRIG-H model):
///   At the start of each second, the 100 Hz subcarrier rises to full power ~30 ms
///   after the 1 kHz second tick. It holds HIGH for a duration that encodes the bit
///   value, then drops to reduced power (−10 dBc) for the remainder of the second.
///   The encoding is in the HIGH period duration.
///
/// WWV BCD pulse encoding (each second, starting at tick):
///   Bit 0   = ~170 ms HIGH, then LOW until next tick
///   Bit 1   = ~470 ms HIGH, then LOW until next tick
///   Marker  = ~770 ms HIGH, then LOW until next tick
///
/// This detector measures the HIGH duration (time above threshold) and fires
/// PulseDetected when the signal drops back below threshold, or at the next tick
/// if the falling edge was missed (backstop via NotifyTick).
///
/// Hysteresis thresholds (prevents chattering on slow envelope transitions):
///   Enter pulse (go HIGH): sample > 0.62 × levelHigh  (exitThreshold)
///   Exit  pulse (go LOW) : sample < 0.55 × levelHigh  (enterThreshold)
///   Dead-band = 7% of levelHigh.
///
/// Rising-edge detection is gated to a 200 ms window opened by NotifyTick().
/// This prevents spurious entry from noise or fading between seconds.
///
/// levelHigh tracking uses two mechanisms:
///   • Real-time asymmetric IIR (100 ms attack / 3 s decay): drives enter/exit thresholds.
///   • 75th-percentile of the last 30 inter-pulse HIGH-period peaks (Option 3):
///     a fade-resistant reference used for MatchedFilter classification.
///     Multipath constructive spikes appear as high outliers (ignored); HF-fade-
///     depressed HIGH periods appear as low outliers (ignored). Falls back to the
///     IIR until ≥ 3 entries are populated.
///
/// IsFading:
///   Driven by comparing the 1 kHz tick amplitude to a 5-tick IIR average.
///   Set when the current tick falls below 30% of the running average (~−10 dB fade).
///   Pulses emitted while IsFading carry confidence = 0 (erasure weight).
///
/// AmplitudeVariability / IsAmplitudeUnstable:
///   Driven by the relative envelope deviation from levelHigh during HIGH periods.
///   Engages at 30% relative deviation; clears at 15%. Triggers the adaptive LP
///   to widen from 2 Hz to 8 Hz when multipath fading is detected.
/// </summary>
public class PulseDetector
{
    private readonly int _sampleRate;
    private readonly SynchronousDetector _detector;

    private readonly int _dropoutToleranceSamples;
    private readonly int _minPulseSamples;
    private readonly double _highDecay;
    private readonly double _highAttack;
    private readonly double _highFastAttack;
    private readonly int _maxPulseSamples;
    private readonly int _refractorySamples;
    private int _refractory;

    // Amplitude variability: exponential average of envelope deviation from levelHigh
    // during HIGH periods. High values indicate rapid HF fading that the 2 Hz LP cannot
    // track. Used to adaptively widen the sync detector lowpass from 2 → 8 Hz.
    private double _amplitudeVariability;
    private readonly double _varAlpha;  // τ ≈ 3 s
    private bool _isAmplitudeUnstable;  // hysteresis state: engage at 0.30, disengage at 0.15

    private bool _inPulse;
    private int _pulseSamples;
    private int _gapSamples;
    private double _peakEnvelope;
    private double _levelHigh;     // real-time IIR tracker: drives enter/exit thresholds

    // Snapshot of levelHigh taken when a pulse begins.
    private double _levelHighAtPulseStart;

    // Percentile-based carrier level (Option 3).
    private const int    PctWindowSize = 30;
    private const double PctFraction   = 0.75;
    private readonly double[] _highPeakWindow = new double[PctWindowSize];
    private int    _highPeakHead;
    private int    _highPeakCount;
    private double _interPulsePeak;
    private double _levelHighPct;

    // Tick-anchored detection fields.
    // _tickWindowSamples: countdown after NotifyTick(); rising edge only accepted while > 0.
    // _tickAmplitudeIir: IIR average of 1 kHz tick peak amplitudes (τ ≈ 5 ticks).
    private int    _tickWindowSamples;
    private double _tickAmplitudeIir;
    private readonly double _tickFadeAlpha; // α = 1 − exp(−1/5) ≈ 0.181

    private readonly List<double> _pulseBuffer = new();

    public event Action<PulseEvent>? PulseDetected;
    public Action<string>? OnLog { get; set; }

    public double CurrentEnvelope { get; private set; }
    public double PeakEnvelope => _peakEnvelope;
    public bool IsFading { get; private set; }

    /// <summary>
    /// Stable carrier HIGH level used for pulse classification.
    /// Returns the 75th-percentile estimate when the window is populated (≥ 3 inter-pulse
    /// peaks), otherwise the real-time IIR tracker.
    /// </summary>
    public double LevelHigh => _levelHighPct > 0 ? _levelHighPct : _levelHigh;

    /// <summary>
    /// Exponentially-smoothed relative envelope deviation from levelHigh (τ ≈ 3 s).
    /// Values approaching 0.30 indicate rapid HF fading; above 0.30 IsAmplitudeUnstable
    /// is set and the adaptive lowpass widens to 8 Hz.
    /// </summary>
    public double AmplitudeVariability => _amplitudeVariability;

    /// <summary>
    /// True when the carrier amplitude is varying too rapidly for the 2 Hz LP filter
    /// to track. Engages at 30% relative deviation; clears at 15%.
    /// </summary>
    public bool IsAmplitudeUnstable => _isAmplitudeUnstable;

    public PulseDetector(int sampleRate, SynchronousDetector detector)
    {
        _sampleRate = sampleRate;
        _detector = detector;
        _dropoutToleranceSamples = sampleRate * 75   / 1000;
        _minPulseSamples         = sampleRate * 50   / 1000;
        _maxPulseSamples         = sampleRate * 1100 / 1000;
        _highDecay      = Math.Exp(-1.0 / (3.0   * sampleRate));
        _highAttack     = 1.0 - Math.Exp(-1.0 / (0.100 * sampleRate));
        _highFastAttack = 1.0 - Math.Exp(-1.0 / (0.030 * sampleRate));
        _refractorySamples      = sampleRate * 200 / 1000;
        _varAlpha               = 1.0 - Math.Exp(-1.0 / (3.0 * sampleRate));
        _tickFadeAlpha          = 1.0 - Math.Exp(-1.0 / 5.0);
    }

    /// <summary>
    /// Discard any pulse currently in progress without emitting it.
    /// Called on minute-pulse re-anchor so the P0 Marker can be detected
    /// fresh rather than being consumed by an overflow of the P59 pulse.
    /// </summary>
    public void AbortCurrentPulse()
    {
        _inPulse        = false;
        _pulseSamples   = 0;
        _gapSamples     = 0;
        _pulseBuffer.Clear();
        _refractory     = 0;
        _interPulsePeak = 0;
    }

    /// <summary>
    /// Clears the inter-pulse peak history used to compute the percentile-based level
    /// reference. Call after a known anchor event (minute pulse) so the reference
    /// relearns from the post-pulse signal level rather than pre-pulse peaks that may
    /// have been recorded before an AGC gain change.
    /// </summary>
    public void ClearLevelReference()
    {
        _highPeakCount  = 0;
        _highPeakHead   = 0;
        _levelHighPct   = 0;
        _interPulsePeak = 0;
    }

    public void Reset()
    {
        _inPulse               = false;
        _pulseSamples          = 0;
        _gapSamples            = 0;
        _peakEnvelope          = 0;
        _levelHigh             = 0;
        _levelHighAtPulseStart = 0;
        _pulseBuffer.Clear();
        IsFading               = false;
        _refractory            = 0;
        _amplitudeVariability  = 0;
        _isAmplitudeUnstable   = false;
        _highPeakHead          = 0;
        _highPeakCount         = 0;
        _interPulsePeak        = 0;
        _levelHighPct          = 0;
        _tickWindowSamples     = 0;
        _tickAmplitudeIir      = 0;
    }

    /// <summary>
    /// Called by DecoderPipeline when a 1 kHz second tick (or minute pulse) fires.
    ///
    /// (1) Backstop: if _inPulse is still true, the previous second's falling edge was
    ///     missed. Fire the accumulated pulse — the matched filter counts only samples
    ///     above midThreshold, so any trailing LOW samples are silently excluded.
    /// (2) Window: arms a rising-edge detection window. The normal window is 200 ms, but
    ///     callers may pass a larger value (e.g. 400 ms for the minute-pulse notification)
    ///     so a subsequent tick within that window cannot shrink it via Math.Max.
    /// (3) Fading: updates IsFading from the tick amplitude IIR.
    ///
    /// IMPORTANT: DecoderPipeline must call TickDetector.ProcessBlock() BEFORE
    /// PulseDetector.ProcessBlock() so the notification arrives before the 100 Hz
    /// rising edge (~30–80 ms after the tick) within the same audio block.
    /// </summary>
    public void NotifyTick(double tickAmplitude, int windowMs = 200)
    {
        if (_inPulse)
        {
            if (_pulseSamples > _minPulseSamples)
            {
                double pulseWidthSeconds = (double)_pulseSamples / _sampleRate;
                var (matchedType, confidence, effDuration) =
                    MatchedFilter.ClassifyWithConfidence(
                        _pulseBuffer, _sampleRate, _levelHighAtPulseStart);
                double emitConfidence = IsFading ? 0.0 : confidence;
                PulseDetected?.Invoke(new PulseEvent(
                    pulseWidthSeconds, matchedType, emitConfidence, effDuration));
            }
            _inPulse      = false;
            _pulseSamples = 0;
            _gapSamples   = 0;
            _pulseBuffer.Clear();
        }

        // Use Math.Max so a large window opened by the minute-pulse notification
        // cannot be shrunk by the false SecondTick that fires milliseconds later
        // from the BPF/LP ring-down (which also calls NotifyTick with the default
        // 200 ms). Once the extended window has naturally elapsed to zero, subsequent
        // normal SecondTicks restore the default 200 ms window as usual.
        _tickWindowSamples = Math.Max(_tickWindowSamples, _sampleRate * windowMs / 1000);
        _refractory        = 0;

        if (_tickAmplitudeIir < 1e-9)
            _tickAmplitudeIir = tickAmplitude > 1e-9 ? tickAmplitude : 1e-9;
        else
            _tickAmplitudeIir += _tickFadeAlpha * (tickAmplitude - _tickAmplitudeIir);

        IsFading = _tickAmplitudeIir > 1e-6 && tickAmplitude < _tickAmplitudeIir * 0.30;
    }

    public void ProcessBlock(float[] envelopeSamples)
    {
        foreach (var sample in envelopeSamples)
        {
            CurrentEnvelope = sample;
            if (sample > _peakEnvelope) _peakEnvelope = sample;
            _peakEnvelope *= 0.9999;

            // Track carrier level unconditionally so _levelHigh bootstraps from the
            // first sample. Fast attack during HIGH pulses, slow decay during LOW gaps.
            if (sample > _levelHigh)
            {
                double attack = sample > _levelHigh * 1.2 ? _highFastAttack : _highAttack;
                _levelHigh += attack * (sample - _levelHigh);
            }
            else
                _levelHigh *= _highDecay;

            // Accumulate inter-pulse peak during the HIGH pulse period for the percentile window.
            if (_inPulse && sample > _interPulsePeak) _interPulsePeak = sample;

            double noise = Math.Min(_detector.NoiseFloor, _levelHigh * 0.10);
            bool hasSignal = _levelHigh > noise * 3.0;

            if (!hasSignal)
            {
                if (_inPulse)
                {
                    _inPulse = false;
                    _pulseSamples = 0;
                    _gapSamples = 0;
                    _pulseBuffer.Clear();
                }
                continue;
            }

            // Amplitude variability tracking during HIGH pulse periods.
            if (_inPulse && _levelHigh > 1e-6)
            {
                double relDev = Math.Abs(sample - _levelHigh) / _levelHigh;
                _amplitudeVariability += _varAlpha * (relDev - _amplitudeVariability);
                if      (_amplitudeVariability > 0.30) _isAmplitudeUnstable = true;
                else if (_amplitudeVariability < 0.15) _isAmplitudeUnstable = false;
            }

            double enterThreshold = _levelHigh * 0.55;
            double exitThreshold  = _levelHigh * 0.62;

            // Count down the tick arm window every sample.
            if (_tickWindowSamples > 0) _tickWindowSamples--;

            if (!_inPulse)
            {
                // Only accept a rising edge while the post-tick window is open.
                if (_tickWindowSamples > 0 && sample > exitThreshold)
                {
                    // Push the inter-pulse LOW period peak into the percentile window.
                    // In positive-pulse mode the LOW period is between pulses — we track
                    // the preceding HIGH pulse peak accumulated in _interPulsePeak.
                    if (_interPulsePeak > 0)
                    {
                        _highPeakWindow[_highPeakHead] = _interPulsePeak;
                        _highPeakHead = (_highPeakHead + 1) % PctWindowSize;
                        if (_highPeakCount < PctWindowSize) _highPeakCount++;
                        if (_highPeakCount >= 3)
                            _levelHighPct = ComputePercentile();
                    }
                    _interPulsePeak = 0;

                    _inPulse      = true;
                    _pulseSamples = 1;
                    _gapSamples   = 0;
                    _tickWindowSamples = 0;
                    double effectiveRef = _levelHighPct > 0 ? _levelHighPct : _levelHigh;
                    _levelHighAtPulseStart = Math.Min(effectiveRef, _levelHigh * 1.5);
                    _pulseBuffer.Clear();
                    _pulseBuffer.Add(sample);
                }
            }
            else
            {
                if (_pulseSamples > _maxPulseSamples)
                {
                    OnLog?.Invoke($"[PulseDetector] OVERFLOW discard: pulseSamples={_pulseSamples} ({(double)_pulseSamples/_sampleRate*1000:F1}ms) > max={_maxPulseSamples}");
                    _inPulse      = false;
                    _pulseSamples = 0;
                    _gapSamples   = 0;
                    _pulseBuffer.Clear();
                    _refractory   = _refractorySamples;
                }
                else if (sample < enterThreshold)  // falling edge → HIGH pulse ending
                {
                    _gapSamples++;
                    if (_gapSamples >= _dropoutToleranceSamples)
                    {
                        if (_pulseSamples > _minPulseSamples)
                        {
                            double pulseWidthSeconds = (double)_pulseSamples / _sampleRate;
                            var (matchedType, confidence, effDuration) =
                                MatchedFilter.ClassifyWithConfidence(
                                    _pulseBuffer, _sampleRate, _levelHighAtPulseStart);
                            double emitConfidence = IsFading ? 0.0 : confidence;
                            PulseDetected?.Invoke(new PulseEvent(
                                pulseWidthSeconds, matchedType, emitConfidence, effDuration));
                        }
                        else
                        {
                            OnLog?.Invoke($"[PulseDetector] SHORT discard: pulseSamples={_pulseSamples} ({(double)_pulseSamples/_sampleRate*1000:F1}ms)");
                        }
                        _inPulse      = false;
                        _pulseSamples = 0;
                        _gapSamples   = 0;
                        _pulseBuffer.Clear();
                        _refractory   = _refractorySamples;
                    }
                }
                else
                {
                    _pulseSamples++;
                    _gapSamples = 0;
                    _pulseBuffer.Add(sample);
                }
            }
        }
    }

    private double ComputePercentile()
    {
        var buf = new double[_highPeakCount];
        for (int i = 0; i < _highPeakCount; i++)
            buf[i] = _highPeakWindow[
                (_highPeakHead - _highPeakCount + i + PctWindowSize) % PctWindowSize];
        Array.Sort(buf);
        int idx = (int)Math.Floor(PctFraction * (_highPeakCount - 1));
        return buf[idx];
    }
}

/// <summary>
/// A detected pulse event with its measured width, matched-filter classification,
/// and soft-decision confidence score.
/// </summary>
public record PulseEvent(double WidthSeconds, PulseType? MatchedType = null, double Confidence = 1.0,
                         double EffectiveDuration = 0.0)
{
    /// <summary>
    /// Duration-based fallback: classify by raw edge-to-edge HIGH duration.
    /// </summary>
    public PulseType DurationType => WidthSeconds switch
    {
        < 0.10  => PulseType.Tick,
        < 0.35  => PulseType.Zero,
        < 0.65  => PulseType.One,
        _       => PulseType.Marker
    };

    /// <summary>
    /// Best classification: matched filter result when available, falling back to
    /// duration-based.
    /// </summary>
    public PulseType Type => MatchedType ?? DurationType;
}

/// <summary>
/// Pulse type classification for WWV/WWVB time-code pulses.
/// </summary>
public enum PulseType
{
    /// <summary>Transition artifact (&lt; 100 ms). Not used for time decoding.</summary>
    Tick,

    /// <summary>Zero pulse (~170 ms HIGH), encodes a binary 0 in the BCD time code.</summary>
    Zero,

    /// <summary>One pulse (~470 ms HIGH), encodes a binary 1 in the BCD time code.</summary>
    One,

    /// <summary>Marker pulse (~770 ms HIGH), position markers at P0..P5 and P9.</summary>
    Marker
}
