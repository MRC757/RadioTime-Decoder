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

    public double CurrentEnvelope { get; private set; }
    public double PeakEnvelope => _peakEnvelope;
    public double LevelHigh => _levelHigh;

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
        _highDecay  = Math.Exp(-1.0 / (3.000 * sampleRate)); // 3 s decay (≈0.999985)
        // 100 ms attack, applied only during HIGH periods (not in pulse) — see ProcessBlock.
        // Gating the attack to HIGH-only periods means noise/ticks during LOW cannot inflate
        // _levelHigh. The 100 ms τ recovers 86.5% of the gap to true carrier in a single
        // 200 ms HIGH window (the shortest HIGH period, after an 800 ms Marker):
        //   76% (after LOW decay) + 0.865 × 24% = 96.7% of true carrier.
        //   exitThreshold = 0.62 × 96.7% = 60% of carrier — 29% above LOW (31%). ✓
        // Reduced from 200 ms (which only recovered 63%) because after ionospheric fades the
        // 0.2 s HIGH window must be enough to pull enterThreshold above the LOW carrier (31%).
        // With 200 ms: enterThreshold = 0.47 × 91% = 42.8% — barely above LOW after a pre-fade
        // depressed levelHigh. With 100 ms: 0.47 × 96.7% = 45.4% — more reliable margin.
        // The 3 s decay runs during LOW periods (when the attack branch is skipped),
        // so _levelHigh holds steady during pulses rather than collapsing.
        _highAttack     = 1.0 - Math.Exp(-1.0 / (0.100 * sampleRate));
        // 30 ms fast-recovery attack: activates when the envelope is more than 1.5× above
        // levelHigh — the signature of post-fade carrier recovery.  Under normal conditions
        // (envelope ≈ levelHigh) this branch is never taken, so routine ticks cannot inflate
        // levelHigh.  After a deep fade the tracker snaps back in ~100 ms rather than ~300 ms,
        // preventing exitThreshold from sitting below the carrier for the next two pulses.
        _highFastAttack = 1.0 - Math.Exp(-1.0 / (0.030 * sampleRate));
        _fadeMinSamples         = sampleRate * 200 / 1000; // 200 ms
        _fadeRecoveryMinSamples = sampleRate * 500 / 1000; // 500 ms
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
    }

    public void ProcessBlock(float[] envelopeSamples)
    {
        foreach (var sample in envelopeSamples)
        {
            CurrentEnvelope = sample;
            if (sample > _peakEnvelope) _peakEnvelope = sample;
            _peakEnvelope *= 0.9999;

            // Track carrier HIGH level — attack ONLY during HIGH periods (not in pulse).
            // Gating the attack prevents LOW-period noise and ticks from inflating _levelHigh,
            // which would push exitThreshold above the carrier level and lock the detector.
            // During LOW periods the 3 s decay runs, keeping _levelHigh stable (decays only
            // ~24% over an 800 ms Marker LOW period). The maxPulseSamples cap breaks stuck
            // states so _levelHigh can recover even after a spurious long-LOW lockup.
            if (!_inPulse)
            {
                if (sample > _levelHigh)
                {
                    // Fast-recovery branch: when the envelope significantly exceeds levelHigh
                    // the carrier has returned from a deep fade — snap the tracker back in
                    // ~30 ms instead of the normal 100 ms so exit/enter thresholds recover
                    // before the first returned pulse arrives.
                    double attack = sample > _levelHigh * 1.5 ? _highFastAttack : _highAttack;
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
            // Weak signal guard: if _levelHigh < 3× noise floor, exitThreshold (70% of
            // levelHigh) may fall below the actual carrier HIGH level, making it
            // unreachable. Every pulse then runs to the 1.1 s safety cap and gets
            // classified as Marker. Suppress detection entirely until the carrier is
            // established — garbage output is worse than no output.
            double noise = _detector.NoiseFloor;
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

            // Thresholds as fractions of _levelHigh.
            // WWV LOW ≈ 31% of HIGH. Dead-band (enter↔exit gap) prevents chattering.
            //   Enter: 47% — safely above LOW (31%), starts pulse detection
            //   Exit:  62% — safely below HIGH (100%), ends pulse detection
            // With the gated-HIGH-only attack, noise/ticks during LOW cannot inflate
            // _levelHigh, so exitThreshold stays well below the carrier HIGH level.
            double enterThreshold = _levelHigh * 0.47;
            double exitThreshold  = _levelHigh * 0.62;

            if (!_inPulse)
            {
                // Not in a pulse — enter LOW state when signal drops below enter threshold
                if (sample < enterThreshold)
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
                    _inPulse = false;
                    _pulseSamples = 0;
                    _gapSamples = 0;
                    _pulseBuffer.Clear();
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
                            double referenceLevel = _peakEnvelope > _levelHighAtPulseStart * 1.5
                                ? _peakEnvelope * 0.9
                                : _levelHighAtPulseStart;
                            var (matchedType, confidence, effDuration) = MatchedFilter.ClassifyWithConfidence(
                                _pulseBuffer, _sampleRate, referenceLevel);
                            // Zero confidence during fade recovery: the matched-filter reference
                            // and level thresholds are both unreliable immediately after a deep
                            // fade.  Erasure weight (0.0) lets prior clean frames dominate the vote.
                            double emitConfidence = IsFading ? 0.0 : confidence;
                            PulseDetected?.Invoke(new PulseEvent(pulseWidthSeconds, matchedType, emitConfidence, effDuration));
                        }
                        _inPulse = false;
                        _pulseSamples = 0;
                        _gapSamples = 0;
                        _pulseBuffer.Clear();
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
