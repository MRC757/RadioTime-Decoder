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
///   Enter pulse (go LOW) : sample &lt; 0.65 × levelHigh  (lower bar to start)
///   Exit  pulse (go HIGH): sample &gt; 0.85 × levelHigh  (higher bar to end)
///   Dead-band = 20% of levelHigh — the synchronous detector's slowly-rising
///   envelope must fully clear this band before the pulse is considered over,
///   preventing multiple false pulses from a single slow transition.
///
/// Pulse classification (based on LOW duration):
///   &lt; 0.10 s    → Tick   (noise/transition glitch, ignored)
///   0.10–0.35 s → Zero   (nominal 0.2 s)
///   0.35–0.65 s → One    (nominal 0.5 s)
///   ≥ 0.65 s    → Marker (nominal 0.8 s)
/// </summary>
public class PulseDetector
{
    private readonly int _sampleRate;
    private readonly SynchronousDetector _detector;

    private readonly int _dropoutToleranceSamples; // 50 ms in samples
    private readonly int _minPulseSamples;          // 50 ms in samples
    private readonly double _highDecay;      // per-sample decay for _levelHigh (~3 s)
    private readonly double _highAttack;     // per-sample rise for _levelHigh (100 ms — filters ~5 ms tick spikes)
    private readonly double _highFastAttack; // per-sample rise for _levelHigh (30 ms) — post-fade snap-back
    private readonly int _maxPulseSamples;   // 1.1 s — force-end any impossible-length pulse
    // After an 800 ms Marker the LP filter starts the next second at ~0.944 (post-AGC
    // equivalent), taking ~107 ms to fall to enterThreshold.  Without a refractory
    // window the detector enters the first data pulse at 107 ms, but the LP hasn't fallen
    // far enough below midThreshold to accumulate d ≥ 50 ms → Tick classification.
    //
    // With a 150 ms refractory (from pulse end): the Marker pulse fires at ~857 ms into
    // its second, so 150 ms later = 1007 ms = 7 ms into the next second.  The LP is
    // still above enterThreshold at 7 ms (~0.938 vs threshold ~0.229), so the detector
    // waits until the LP falls to enterThreshold at ~107 ms.  The pulse then runs to
    // the end of the LOW period, accumulating d well above the Tick/Zero boundary.
    //
    // Minimum gap to next legitimate crossing for non-Marker pulses:
    //   After Zero (200 ms LOW): pulse fires at ~257 ms → gap to next enter ≈ 850 ms >> 150 ms. ✓
    //   After One  (500 ms LOW): pulse fires at ~557 ms → gap to next enter ≈ 550 ms >> 150 ms. ✓
    private readonly int _refractorySamples; // 150 ms
    private int _refractory;

    // Amplitude variability: exponential average of envelope deviation from levelHigh
    // during HIGH periods. High values indicate rapid HF fading that the 2 Hz LP cannot
    // track. Used to adaptively widen the sync detector lowpass from 2 → 8 Hz.
    private double _amplitudeVariability;
    private readonly double _varAlpha;  // τ ≈ 3 s
    private bool _isAmplitudeUnstable;  // hysteresis state: engage at 0.30, disengage at 0.15

    // Fade detector: flag when the signal has been absent for > 200 ms and hasn't
    // yet recovered.  Pulses emitted while IsFading get confidence=0 (erasure weight)
    // so a wrongly-classified pulse during a fade can't outvote clean frames.
    // Recovery requires 500 ms of continuous signal AND levelHigh ≥ 60 % of peakEnvelope.
    private readonly int _fadeMinSamples;         // 200 ms
    private readonly int _fadeRecoveryMinSamples; // 500 ms
    private int _consecutiveLowSamples;
    private int _fadeRecoverySamples;
    public bool IsFading { get; private set; }

    private bool _inPulse;        // true while in LOW period (reduced power = pulse encoding)
    private int _pulseSamples;    // count of samples in current LOW period
    private int _gapSamples;      // count of above-exit-threshold samples during dropout window
    private double _peakEnvelope;
    private double _levelHigh;    // tracks peak carrier amplitude (H) with soft attack

    // Snapshot of _levelHigh taken at the moment a pulse starts (before decay during the
    // LOW period skews it). With τ=3 s decay, a 0.8 s marker would reduce _levelHigh to
    // 76% of its true value by pulse end — making the MatchedFilter midThreshold (50%)
    // fall to 38% of true HIGH, dangerously close to the WWV LOW carrier level (31%).
    // Using the pre-pulse snapshot keeps a clean reference throughout the LOW period.
    private double _levelHighAtPulseStart;

    // Matched filter: buffer the envelope samples during the LOW period so the
    // classifier can count genuinely-low samples rather than relying on edge timing.
    private readonly List<double> _pulseBuffer = new();

    public event Action<PulseEvent>? PulseDetected;
    public Action<string>? OnLog { get; set; }

    public double CurrentEnvelope { get; private set; }
    public double PeakEnvelope => _peakEnvelope;
    public double LevelHigh => _levelHigh;

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
        _dropoutToleranceSamples = sampleRate * 30   / 1000; // 30 ms — shorter than envelope rise time (20 ms) plus margin
        _minPulseSamples         = sampleRate * 50   / 1000;
        _maxPulseSamples         = sampleRate * 1100 / 1000;
        // Per-sample multiplier for exponential decay: e^{-1/(τ·sr)} ≈ 0.99999.
        // NOTE: this is Math.Exp(...), NOT 1-Math.Exp(...).  Using 1-exp would give
        // ~1.5e-5 per sample, collapsing _levelHigh to zero in a single step.
        //
        // 3 s decay: applied when the envelope dips below levelHigh (signal fade or
        // pulse LOW period). Fast enough to track ionospheric fading without causing
        // exitThreshold to lag so far behind the carrier that pulses fail to close.
        // The MatchedFilter uses _levelHighAtPulseStart (snapshot taken before any
        // in-pulse decay) so the classification reference is always clean.
        _highDecay  = Math.Exp(-1.0 / (3.0 * sampleRate));
        // 100 ms attack, applied only during HIGH periods (not in pulse) — see ProcessBlock.
        _highAttack     = 1.0 - Math.Exp(-1.0 / (0.100 * sampleRate));
        // 30 ms fast-recovery attack: activates when the envelope is more than 1.5× above
        // levelHigh — the signature of post-fade carrier recovery.  Under normal conditions
        // (envelope ≈ levelHigh) this branch is never taken, so routine ticks cannot inflate
        // levelHigh.  After a deep fade the tracker snaps back in ~100 ms rather than ~300 ms,
        // preventing exitThreshold from sitting below the carrier for the next two pulses.
        _highFastAttack = 1.0 - Math.Exp(-1.0 / (0.030 * sampleRate));
        _fadeMinSamples         = sampleRate * 200 / 1000; // 200 ms
        _fadeRecoveryMinSamples = sampleRate * 500 / 1000; // 500 ms
        _refractorySamples      = sampleRate * 150 / 1000; // 150 ms
        _varAlpha               = 1.0 - Math.Exp(-1.0 / (3.0 * sampleRate));
    }

    /// <summary>
    /// Discard any pulse currently in progress without emitting it.
    /// Called on minute-pulse re-anchor so the P0 Marker can be detected
    /// fresh rather than being consumed by an OVERFLOW of the P59 pulse.
    /// Refractory is cleared so the P0 LOW period is entered immediately.
    /// </summary>
    public void AbortCurrentPulse()
    {
        _inPulse      = false;
        _pulseSamples = 0;
        _gapSamples   = 0;
        _pulseBuffer.Clear();
        _refractory   = 0; // don't gate P0 detection
    }

    public void Reset()
    {
        _inPulse = false;
        _pulseSamples = 0;
        _gapSamples = 0;
        _peakEnvelope = 0;
        _levelHigh = 0;
        _levelHighAtPulseStart = 0;
        _pulseBuffer.Clear();
        _consecutiveLowSamples = 0;
        _fadeRecoverySamples   = 0;
        IsFading               = false;
        _refractory            = 0;
        _amplitudeVariability  = 0;
        _isAmplitudeUnstable   = false;
    }

    public void ProcessBlock(float[] envelopeSamples)
    {
        foreach (var sample in envelopeSamples)
        {
            CurrentEnvelope = sample;
            if (sample > _peakEnvelope) _peakEnvelope = sample;
            _peakEnvelope *= 0.9999;

            // Track carrier HIGH level. Attack is gated to !_inPulse so LOW-period noise
            // and ticks cannot inflate _levelHigh and push exitThreshold above the carrier.
            // The 3 s decay runs in both HIGH-period dips and pulse LOW periods; _levelHighAtPulseStart
            // (snapshot at pulse entry) is what the MatchedFilter uses, so in-pulse decay
            // does not bias classification. The maxPulseSamples cap breaks any stuck-LOW state.
            if (!_inPulse)
            {
                if (sample > _levelHigh)
                {
                    // Fast-recovery branch: when the envelope significantly exceeds levelHigh
                    // the carrier has returned from a deep fade — snap the tracker back in
                    // ~30 ms instead of the normal 100 ms so exit/enter thresholds recover
                    // before the first returned pulse arrives.
                    // Threshold lowered 1.5 → 1.2: after a 1.1s OVERFLOW levelHigh decays to
                    // ~69% of true carrier. The returning carrier (1.0 × true) gives ratio
                    // 1/0.69 ≈ 1.45, which was just below the old 1.5 trigger, causing slow
                    // attack (100ms τ) when fast attack (30ms τ) is needed.
                    double attack = sample > _levelHigh * 1.2 ? _highFastAttack : _highAttack;
                    _levelHigh += attack * (sample - _levelHigh);
                }
                else
                    _levelHigh *= _highDecay;
            }
            else
            {
                _levelHigh *= _highDecay;
            }

            // Hysteresis thresholds as percentages of the tracked peak level.
            // WWV LOW carrier ≈ 31% of HIGH (10 dB reduction). Thresholds at 55% and 70%
            // sit well between the two states with a 15% dead-band to prevent chattering
            // on the synchronous detector's ~20 ms envelope transitions.
            //
            // Weak signal guard: if _levelHigh < 3× noise floor, exitThreshold (62% of
            // levelHigh) may fall below the actual carrier HIGH level, making it
            // unreachable. Every pulse then runs to the 1.1 s safety cap and gets
            // classified as Marker. Suppress detection entirely until the carrier is
            // established — garbage output is worse than no output.
            //
            // Cap the effective noise reference at 10 % of _levelHigh. In real HF the
            // true noise floor is always well below 10 % of the carrier (20 dB+ SNR
            // for any decodable signal). The SynchronousDetector's noise estimator can
            // erroneously drift toward the carrier level when no truly quiet period
            // exists (e.g. clean simulation signals where the LOW carrier is 0.316 —
            // still above the initial noise floor), which would then suppress detection
            // during Marker LOW periods as _levelHigh decays to 0.765 of its peak.
            double noise = Math.Min(_detector.NoiseFloor, _levelHigh * 0.10);
            bool hasSignal = _levelHigh > noise * 3.0;

            if (!hasSignal)
            {
                // Abort any in-progress pulse so we don't carry stale state into
                // the next (hopefully stronger) signal period.
                if (_inPulse)
                {
                    _inPulse = false;
                    _pulseSamples = 0;
                    _gapSamples = 0;
                    _pulseBuffer.Clear();
                }
                // Fade detector: accumulate consecutive no-signal samples.
                _consecutiveLowSamples++;
                if (_consecutiveLowSamples >= _fadeMinSamples) IsFading = true;
                _fadeRecoverySamples = 0;
                continue;
            }
            // Signal present — reset no-signal counter and manage fade recovery.
            _consecutiveLowSamples = 0;
            if (IsFading)
            {
                // Require 500 ms of continuous signal AND levelHigh ≥ 60 % of peakEnvelope
                // (the fast-recovery attack will have pulled levelHigh up by then) before
                // trusting pulses again.
                _fadeRecoverySamples++;
                if (_fadeRecoverySamples >= _fadeRecoveryMinSamples
                    && _levelHigh >= _peakEnvelope * 0.60)
                    IsFading = false;
            }
            else
            {
                _fadeRecoverySamples = 0;
            }

            // Track amplitude variability during HIGH periods (not in pulse).
            // relDev = |envelope − levelHigh| / levelHigh; smoothed over ~3 s.
            // Large values mean the carrier is fading faster than the 2 Hz LP can follow.
            // Hysteresis: engage at 0.30, disengage at 0.15 — prevents chattering.
            if (!_inPulse && _levelHigh > 1e-6)
            {
                double relDev = Math.Abs(sample - _levelHigh) / _levelHigh;
                _amplitudeVariability += _varAlpha * (relDev - _amplitudeVariability);
                if      (_amplitudeVariability > 0.30) _isAmplitudeUnstable = true;
                else if (_amplitudeVariability < 0.15) _isAmplitudeUnstable = false;
            }

            // Thresholds as fractions of _levelHigh.
            // WWV LOW ≈ 31% of HIGH. Dead-band (enter↔exit gap) prevents chattering.
            //   Enter: 55% — well above LOW (31%); detects pulse even when LH is depressed
            //           post-Marker (LH≈76% of carrier → enterThr=42% of carrier, still > 31%)
            //   Exit:  62% — safely below HIGH (100%), ends pulse detection
            // With the gated-HIGH-only attack, noise/ticks during LOW cannot inflate
            // _levelHigh, so exitThreshold stays well below the carrier HIGH level.
            double enterThreshold = _levelHigh * 0.55;
            double exitThreshold  = _levelHigh * 0.62;

            if (!_inPulse)
            {
                // Refractory period: suppress new pulse entry immediately after a pulse ends
                // to prevent ALE adaptation transients from generating spurious Ticks.
                if (_refractory > 0)
                {
                    _refractory--;
                }
                // Not in a pulse — enter LOW state when signal drops below enter threshold
                else if (sample < enterThreshold)
                {
                    _inPulse = true;
                    _pulseSamples = 1;
                    _gapSamples = 0;
                    _levelHighAtPulseStart = _levelHigh; // snapshot before decay during LOW period
                    _pulseBuffer.Clear();
                    _pulseBuffer.Add(sample);
                }
            }
            else
            {
                // Safety: no valid WWV pulse exceeds 0.8 s. If we have been in LOW state
                // for more than 1.1 s the detector is stuck — discard and reset.
                if (_pulseSamples > _maxPulseSamples)
                {
                    OnLog?.Invoke($"[PulseDetector] OVERFLOW discard: pulseSamples={_pulseSamples} ({(double)_pulseSamples/_sampleRate*1000:F1}ms) > max={_maxPulseSamples}");
                    _inPulse = false;
                    _pulseSamples = 0;
                    _gapSamples = 0;
                    _pulseBuffer.Clear();
                    _refractory = _refractorySamples;
                }
                // In a pulse — only exit when signal clearly rises above exit threshold
                else if (sample > exitThreshold)
                {
                    _gapSamples++;
                    if (_gapSamples >= _dropoutToleranceSamples)
                    {
                        // Pulse has ended — emit if long enough to be real data
                        if (_pulseSamples > _minPulseSamples)
                        {
                            double pulseWidthSeconds = (double)_pulseSamples / _sampleRate;
                            // Use matched filter to classify by counting genuinely-LOW samples
                            // rather than relying on edge timing. This removes the positive bias
                            // introduced by the synchronous detector's envelope rise/fall time.
                            //
                            // When levelHigh is significantly undertracking the true carrier
                            // (detectable via peakEnvelope >> levelHighAtPulseStart), the
                            // midThreshold 0.5×levelHigh can fall below the actual LOW carrier
                            // (0.316×true_HIGH), causing LOW samples to be above the threshold
                            // and not counted → effective duration shrinks → Marker classified
                            // as One.  This happens when env/H > 1.58 (derivation: the LOW
                            // carrier 0.316×true_HIGH < midThreshold 0.5×levelHigh only when
                            // true_HIGH/levelHigh < 1.58).
                            //
                            // Fix: when peakEnvelope > 1.5×levelHigh, use peakEnvelope×0.9 as
                            // the reference. peakEnvelope is a running maximum across the full
                            // signal (not just the pulse buffer, so the spike-inflation concern
                            // in MatchedFilter's comment does not apply).  Factor 0.9 gives
                            // midThreshold = 0.45×true_HIGH — above LOW (0.316) and below HIGH.
                            // Always use the level snapshot taken at pulse start.
                            // midThreshold = 0.5×levelHighAtPulseStart correctly separates
                            // the LOW carrier (0.316×LH) from the HIGH carrier (≈LH) in all
                            // normal conditions.  The fast-attack branch (30 ms when envelope
                            // > 1.5×LH) snaps levelHigh back within one HIGH window after a
                            // fade, so levelHighAtPulseStart is reliable by the next pulse.
                            // A peakEnvelope-based correction was previously used here but
                            // raised midThreshold to 0.75×LH when peakEnvelope was stale
                            // (AGC lag after a signal drop), causing One pulses to misclassify
                            // as Marker (d ≈ 0.53 s > tOneMrk 0.50 s).
                            double referenceLevel = _levelHighAtPulseStart;
                            var (matchedType, confidence, effDuration) = MatchedFilter.ClassifyWithConfidence(
                                _pulseBuffer, _sampleRate, referenceLevel);
                            // Zero confidence during fade recovery: the matched-filter reference
                            // and level thresholds are both unreliable immediately after a deep
                            // fade.  Erasure weight (0.0) lets prior clean frames dominate the vote.
                            double emitConfidence = IsFading ? 0.0 : confidence;
                            PulseDetected?.Invoke(new PulseEvent(pulseWidthSeconds, matchedType, emitConfidence, effDuration));
                        }
                        else
                        {
                            OnLog?.Invoke($"[PulseDetector] SHORT discard: pulseSamples={_pulseSamples} ({(double)_pulseSamples/_sampleRate*1000:F1}ms)");
                        }
                        _inPulse = false;
                        _pulseSamples = 0;
                        _gapSamples = 0;
                        _pulseBuffer.Clear();
                        _refractory = _refractorySamples;
                    }
                }
                else
                {
                    // Still below exit threshold — keep counting the pulse, reset gap
                    _pulseSamples++;
                    _gapSamples = 0;
                    _pulseBuffer.Add(sample);
                }
            }
        }
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
    /// Includes positive bias from the envelope's ~20 ms rise/fall times.
    /// </summary>
    public PulseType DurationType => WidthSeconds switch
    {
        < 0.10  => PulseType.Tick,
        < 0.35  => PulseType.Zero,
        < 0.65  => PulseType.One,
        _       => PulseType.Marker
    };

    /// <summary>
    /// Best classification: matched filter result when available (always for live pulses),
    /// falling back to duration-based. The matched filter counts genuinely-LOW samples,
    /// removing the positive bias from envelope transition times and sporadic noise re-entry.
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
