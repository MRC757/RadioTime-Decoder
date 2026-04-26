namespace WwvDecoder.Dsp;

/// <summary>
/// Detects and measures binary pulses from the amplitude envelope.
///
/// WWV signal model:
///   The 100 Hz subcarrier is always present. At the start of each second, its power
///   is REDUCED (by 10 dB) for a duration that encodes the bit value. The encoding
///   is in the LOW period (reduced power), not the high period.
///
/// WWV BCD pulse encoding (each second, starting at t=0):
///   Bit 0   = 0.2 s LOW (reduced), then 0.8 s HIGH (full power)
///   Bit 1   = 0.5 s LOW (reduced), then 0.5 s HIGH (full power)
///   Marker  = 0.8 s LOW (reduced), then 0.2 s HIGH (full power)
///
/// This detector measures the LOW duration (time below threshold) and fires
/// PulseDetected when the signal rises back above threshold.
///
/// Hysteresis thresholds (prevents chattering on slow envelope transitions):
///   Enter pulse (go LOW) : sample &lt; 0.55 × levelHigh
///   Exit  pulse (go HIGH): sample &gt; 0.62 × levelHigh
///   Dead-band = 7% of levelHigh — the synchronous detector's slowly-rising
///   envelope must fully clear this band before the pulse is considered over.
///
/// Pulse classification (based on LOW duration):
///   &lt; 0.10 s    → Tick   (noise/transition glitch, ignored)
///   0.10–0.35 s → Zero   (nominal 0.2 s)
///   0.35–0.65 s → One    (nominal 0.5 s)
///   ≥ 0.65 s    → Marker (nominal 0.8 s)
///
/// levelHigh tracking uses two mechanisms:
///   • Real-time asymmetric IIR (100 ms attack / 3 s decay): drives enterThreshold
///     and exitThreshold for per-sample pulse detection.
///   • 75th-percentile of the last 30 inter-pulse HIGH-period peaks (Option 3):
///     a fade-resistant reference used for MatchedFilter classification.
///     Multipath constructive spikes appear as high outliers (ignored); HF-fade-
///     depressed HIGH periods appear as low outliers (ignored). Falls back to the
///     IIR until ≥ 3 entries are populated.
///
/// IsFading (Option 2):
///   Driven by comparing the current envelope to 15% of the stable carrier reference,
///   measured only during !_inPulse (between-pulse HIGH) periods. WWV LOW carrier
///   ≈ 31% of HIGH — well above 15%. Deep HF fades drop the envelope to &lt; 5%,
///   triggering IsFading. The fade counter is gated to !_inPulse because a pulse LOW
///   period is intentionally reduced amplitude (BCD data), not a propagation fade.
///   The earlier implementation counted pulse LOW samples as fade evidence, causing
///   IsFading to fire on every One/Marker at low SNR and zero-weighing all bits.
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

    // Fade detector (Option 2): IsFading is true when the envelope has been consistently
    // below 15% of the stable carrier reference for > 200 ms and has not yet recovered.
    // Pulses emitted while IsFading carry confidence = 0 (erasure weight) so a wrongly-
    // classified fade pulse cannot outvote clean frames in the multi-frame accumulator.
    private readonly int _fadeMinSamples;         // 200 ms
    private readonly int _fadeRecoveryMinSamples; // 500 ms
    private int _consecutiveLowSamples;
    private int _fadeRecoverySamples;
    public bool IsFading { get; private set; }

    private bool _inPulse;
    private int _pulseSamples;
    private int _gapSamples;
    private double _peakEnvelope;
    private double _levelHigh;     // real-time IIR tracker: drives enter/exit thresholds

    // Snapshot of _levelHighAtPulseStart taken when a pulse begins.
    // With the percentile tracker this equals _levelHighPct (stable carrier estimate).
    // Falls back to the IIR _levelHigh before the percentile window is populated.
    private double _levelHighAtPulseStart;

    // Percentile-based carrier level (Option 3).
    // Each time a pulse starts, the peak envelope accumulated during the preceding
    // HIGH period is pushed into a 30-entry circular window. The 75th percentile of
    // this window gives a fade-resistant carrier reference:
    //   • Multipath spikes inflate the IIR but appear as high outliers here — ignored.
    //   • HF-faded HIGH periods reduce the IIR but appear as low outliers here — ignored.
    // Populated after ≥ 3 inter-pulse peaks; falls back to IIR _levelHigh until then.
    private const int    PctWindowSize = 30;      // ~30 seconds of WWV signal
    private const double PctFraction   = 0.75;    // 75th percentile
    private readonly double[] _highPeakWindow = new double[PctWindowSize];
    private int    _highPeakHead;    // next write slot (circular)
    private int    _highPeakCount;   // filled entries (0..PctWindowSize)
    private double _interPulsePeak;  // running peak during the current inter-pulse HIGH period
    private double _levelHighPct;    // 75th-percentile estimate; 0 until ≥ 3 entries

    // Matched filter: buffer the envelope samples during the LOW period so the
    // classifier can count genuinely-low samples rather than relying on edge timing.
    private readonly List<double> _pulseBuffer = new();

    public event Action<PulseEvent>? PulseDetected;
    public Action<string>? OnLog { get; set; }

    public double CurrentEnvelope { get; private set; }
    public double PeakEnvelope => _peakEnvelope;

    /// <summary>
    /// Stable carrier HIGH level used for pulse classification.
    /// Returns the 75th-percentile estimate when the window is populated (≥ 3 inter-pulse
    /// peaks), otherwise the real-time IIR tracker. The percentile is resistant to both
    /// multipath spikes (high outliers) and HF-fade-depressed HIGH periods (low outliers).
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
    /// to track — the hallmark of HF multipath fading at 1–3 Hz rates.
    /// Engages when the 3 s–smoothed relative deviation exceeds 30 %; clears at 15 %.
    /// </summary>
    public bool IsAmplitudeUnstable => _isAmplitudeUnstable;

    public PulseDetector(int sampleRate, SynchronousDetector detector)
    {
        _sampleRate = sampleRate;
        _detector = detector;
        _dropoutToleranceSamples = sampleRate * 75   / 1000; // 75 ms ignores ionospheric flutter shorter than this
        _minPulseSamples         = sampleRate * 50   / 1000;
        _maxPulseSamples         = sampleRate * 1100 / 1000;
        _highDecay      = Math.Exp(-1.0 / (3.0   * sampleRate));
        _highAttack     = 1.0 - Math.Exp(-1.0 / (0.100 * sampleRate));
        _highFastAttack = 1.0 - Math.Exp(-1.0 / (0.030 * sampleRate));
        _fadeMinSamples         = sampleRate * 200 / 1000;
        _fadeRecoveryMinSamples = sampleRate * 500 / 1000;
        _refractorySamples      = sampleRate * 200 / 1000; // 200 ms covers Marker HIGH gap (200 ms)
        _varAlpha               = 1.0 - Math.Exp(-1.0 / (3.0 * sampleRate));
    }

    /// <summary>
    /// Discard any pulse currently in progress without emitting it.
    /// Called on minute-pulse re-anchor so the P0 Marker can be detected
    /// fresh rather than being consumed by an overflow of the P59 pulse.
    /// Refractory is cleared so the P0 LOW period is entered immediately.
    /// </summary>
    public void AbortCurrentPulse()
    {
        _inPulse        = false;
        _pulseSamples   = 0;
        _gapSamples     = 0;
        _pulseBuffer.Clear();
        _refractory     = 0;
        _interPulsePeak = 0; // start the next inter-pulse HIGH period fresh
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
        _consecutiveLowSamples = 0;
        _fadeRecoverySamples   = 0;
        IsFading               = false;
        _refractory            = 0;
        _amplitudeVariability  = 0;
        _isAmplitudeUnstable   = false;
        _highPeakHead          = 0;
        _highPeakCount         = 0;
        _interPulsePeak        = 0;
        _levelHighPct          = 0;
    }

    public void ProcessBlock(float[] envelopeSamples)
    {
        foreach (var sample in envelopeSamples)
        {
            CurrentEnvelope = sample;
            if (sample > _peakEnvelope) _peakEnvelope = sample;
            _peakEnvelope *= 0.9999;

            // Track carrier HIGH level with asymmetric IIR.
            // Attack is gated to !_inPulse so LOW-period samples cannot inflate
            // _levelHigh and push exitThreshold above the true carrier level.
            // Fast-recovery branch (30 ms τ) snaps the tracker back after a deep
            // fade where _levelHigh has decayed to ~69% of the true carrier.
            if (!_inPulse)
            {
                if (sample > _levelHigh)
                {
                    double attack = sample > _levelHigh * 1.2 ? _highFastAttack : _highAttack;
                    _levelHigh += attack * (sample - _levelHigh);
                }
                else
                    _levelHigh *= _highDecay;

                // Accumulate the inter-pulse peak for the percentile window.
                // Sampled over the full HIGH period (including refractory) so even
                // short 200 ms Marker HIGH periods contribute a representative value.
                if (sample > _interPulsePeak) _interPulsePeak = sample;
            }
            else
            {
                _levelHigh *= _highDecay;
            }

            // Weak-signal guard: suppress detection when no carrier is established.
            // Uses a capped noise reference to prevent the noise estimator drifting
            // toward the carrier level (a known artifact on clean simulation signals
            // where no truly quiet period exists) from incorrectly marking the carrier
            // absent during Marker LOW periods.
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
                continue; // skip threshold detection until carrier is present
            }

            // ── Fade detection (Option 2) ────────────────────────────────────────
            // Compare the current envelope to 15% of the stable carrier reference.
            // WWV LOW carrier ≈ 31% of levelHigh — well above 15%. Deep HF fades
            // drop the envelope to noise level (< 5%), correctly triggering IsFading.
            // The percentile reference (_levelHighPct) is used when populated because
            // it is unaffected by multipath spikes that can transiently inflate the
            // IIR _levelHigh, which would then raise the 15% threshold and miss fades.
            double stableRef = _levelHighPct > 0 ? _levelHighPct : _levelHigh;
            bool envelopeAbsent = stableRef > 1e-6 && sample < stableRef * 0.15;

            if (envelopeAbsent)
            {
                // Only count fade evidence during between-pulse HIGH periods.
                // During a pulse LOW period the envelope is intentionally reduced (that is
                // the BCD data), not a genuine HF fade. Counting those samples caused
                // IsFading to fire on every One/Marker pulse at low SNR, zero-weighting
                // all bits and producing 00:00 decoded time every frame.
                if (!_inPulse) _consecutiveLowSamples++;
                if (_consecutiveLowSamples >= _fadeMinSamples) IsFading = true;
                _fadeRecoverySamples = 0;
            }
            else
            {
                _consecutiveLowSamples = 0;
                if (IsFading)
                {
                    // Require 500 ms of continuous signal AND levelHigh ≥ 60% of
                    // peakEnvelope before trusting pulses again.
                    _fadeRecoverySamples++;
                    if (_fadeRecoverySamples >= _fadeRecoveryMinSamples
                        && _levelHigh >= _peakEnvelope * 0.60)
                        IsFading = false;
                }
                else
                    _fadeRecoverySamples = 0;
            }

            // Track amplitude variability during HIGH periods (not in pulse).
            // relDev = |envelope − levelHigh| / levelHigh; smoothed over ~3 s.
            if (!_inPulse && _levelHigh > 1e-6)
            {
                double relDev = Math.Abs(sample - _levelHigh) / _levelHigh;
                _amplitudeVariability += _varAlpha * (relDev - _amplitudeVariability);
                if      (_amplitudeVariability > 0.30) _isAmplitudeUnstable = true;
                else if (_amplitudeVariability < 0.15) _isAmplitudeUnstable = false;
            }

            // Hysteresis thresholds — fractions of the real-time IIR _levelHigh.
            // The IIR is used here (not the percentile) because thresholds need
            // per-sample responsiveness; the percentile updates only once per pulse.
            //   Enter: 55% — well above LOW carrier (31%); detects pulse even when
            //          _levelHigh is depressed post-Marker (76% of carrier →
            //          enterThreshold = 42% of carrier, still > 31% LOW).
            //   Exit:  62% — safely below HIGH (100%), ends pulse detection.
            double enterThreshold = _levelHigh * 0.55;
            double exitThreshold  = _levelHigh * 0.62;

            if (!_inPulse)
            {
                if (_refractory > 0)
                {
                    _refractory--;
                }
                else if (sample < enterThreshold)
                {
                    // ── Percentile window update (Option 3) ─────────────────────
                    // Push the peak seen during this inter-pulse HIGH period into the
                    // circular window, then recompute the 75th percentile.
                    // Guard against zero peaks (e.g., very first pulse at startup
                    // before any HIGH samples have been observed).
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
                    // Use the stable percentile reference for the MatchedFilter so
                    // multipath spikes and HF fades do not corrupt the midThreshold.
                    // Falls back to the IIR snapshot until the window is populated.
                    _levelHighAtPulseStart = _levelHighPct > 0 ? _levelHighPct : _levelHigh;
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
                else if (sample > exitThreshold)
                {
                    _gapSamples++;
                    if (_gapSamples >= _dropoutToleranceSamples)
                    {
                        if (_pulseSamples > _minPulseSamples)
                        {
                            double pulseWidthSeconds = (double)_pulseSamples / _sampleRate;
                            // MatchedFilter classifies by counting samples below
                            // midThreshold = 0.5 × _levelHighAtPulseStart.
                            // With the percentile reference, midThreshold is stable:
                            // multipath spikes that inflate the IIR cannot raise it,
                            // and HF-faded HIGH periods that depress the IIR cannot
                            // lower it below the true LOW carrier level.
                            double referenceLevel = _levelHighAtPulseStart;
                            var (matchedType, confidence, effDuration) =
                                MatchedFilter.ClassifyWithConfidence(
                                    _pulseBuffer, _sampleRate, referenceLevel);
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

    /// <summary>
    /// Computes the 75th percentile of the active entries in <c>_highPeakWindow</c>.
    /// Called once per pulse (~1 Hz) so the O(N log N) sort over 30 entries is negligible.
    /// </summary>
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
    /// Duration-based fallback: classify by raw edge-to-edge LOW duration.
    /// Includes positive bias from the envelope's rise/fall times.
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
    /// duration-based. The matched filter counts genuinely-LOW samples, removing the
    /// positive bias from envelope transition times.
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

    /// <summary>Zero pulse (0.2 s), encodes a binary 0 in the BCD time code.</summary>
    Zero,

    /// <summary>One pulse (0.5 s), encodes a binary 1 in the BCD time code.</summary>
    One,

    /// <summary>Marker pulse (0.8 s), position markers at P0..P5 and P9 to frame the time frame.</summary>
    Marker
}
