using System.Diagnostics;

namespace WwvDecoder.Dsp;

/// <summary>
/// First-order phase-locked loop for the WWV 1 Hz second-tick cadence.
///
/// The tick source is a cesium oscillator, so the 1-second interval is essentially
/// perfect — only phase (not frequency) needs to be tracked. A first-order phase
/// filter is therefore sufficient.
///
/// Primary benefit — spurious tick rejection:
///   After the 800 ms P0 minute pulse, the TickDetector's BPF/LP ring-down produces
///   a false SecondTick ~20 ms after the pulse ends. Once the PLL is locked it expects
///   the next tick at ~200 ms after P0 end (= 1.000 s from the true minute epoch). The
///   spurious tick arrives ~180 ms early, outside the ±150 ms lock gate, so it is
///   silently discarded. The genuine first-post-minute tick passes normally.
///
/// Secondary benefit — minute-pulse seeding:
///   The P0 end timestamp is a cesium-accurate reference. SeedFromMinutePulse uses the
///   measured pulse width to compute exactly where tick 1 should arrive, giving the PLL
///   an instant warm-start rather than a cold-acquisition period.
///
/// NIST-omitted positions — nextTickOmitted parameter:
///   NIST omits the 1 kHz second tick at positions 29 and 59. Without compensation the
///   PLL advances _pllPhase by 1 s after tick 28, then waits for tick 29 which never
///   arrives. Tick 30 (2 s later) exceeds the ±150 ms gate, causing a "Lost lock" reset
///   every minute. Pass nextTickOmitted = true when submitting tick 28 or 58 to advance
///   _pllPhase by 2 s instead, so the PLL correctly expects the next real tick.
///   Position 59 is additionally rescued by SeedFromMinutePulse when P0 arrives.
///
/// Predictive tick generation during fades:
///   When the 1 kHz signal fades (error > 2 s), instead of immediately resetting to
///   Unlocked, the PLL enters FadingOut state and generates synthetic tick expectations
///   at 1000 ms intervals. If a real tick arrives within ±150 ms of a predicted time,
///   it is accepted and used to recover. This allows quick re-acquisition when the
///   signal returns, eliminating the 5+ second recovery period.
///
/// Gate widths:
///   Unlocked  (cold start)  : ±500 ms — accepts any plausible first tick
///   FadingOut (multipath)   : ±150 ms — predictive tick timing during signal dropout
///   Acquiring / Locked      : ±150 ms — cesium source has no frequency error, so
///                             phase jitter is detection-only (~37 ms biquad group delay);
///                             the tight gate is safe from tick 2 and rejects the ~180 ms
///                             spurious ring-down immediately, not just after 5 ticks.
///
/// Phase-update gains:
///   Acquiring : K = 0.3 — converges quickly to the correct phase
///   Locked    : K = 0.1 — slow, stable tracking
/// </summary>
public class SecondTickPll
{
    private enum PllState { Unlocked, Acquiring, Locked, FadingOut }

    private PllState _state        = PllState.Unlocked;
    private long     _pllPhase;       // expected Stopwatch ticks of the NEXT SecondTick
    private int      _goodTickCount;  // consecutive accepted ticks since last reset/seed
    private long     _fadingOutStart;  // timestamp when FadingOut state was entered
    private long     _lastRealTickTime; // timestamp of the last successfully decoded tick

    private readonly long _freq;      // Stopwatch.Frequency (ticks per second)

    // Gate half-widths in Stopwatch ticks
    private readonly long _gateUnlocked;  // first cold-start tick only
    private readonly long _gateLocked;    // all subsequent ticks (Acquiring and Locked)
    private readonly long _maxFadeOutDuration; // max time to generate synthetic ticks (13 s)

    private const double KAcquiring     = 0.3;
    private const double KLocked        = 0.1;
    private const int    LockThreshold  = 5; // consecutive good ticks required to enter Locked

    private readonly Action<string>? _onLog;

    public bool IsLocked    => _state == PllState.Locked;
    public bool IsAcquiring => _state == PllState.Acquiring;
    public bool IsFadingOut => _state == PllState.FadingOut;

    public SecondTickPll(Action<string>? onLog = null)
    {
        _freq         = Stopwatch.Frequency;
        _gateUnlocked = (long)(_freq * 0.500);
        _gateLocked   = (long)(_freq * 0.150);
        _maxFadeOutDuration = _freq * 13; // 13 seconds max predictive generation
        _onLog        = onLog;
    }

    /// <summary>
    /// Submit a raw SecondTick timestamp from the TickDetector.
    /// Returns the (phase-corrected) timestamp to use downstream, or null if
    /// the tick is rejected as a glitch outside the current acceptance gate.
    /// </summary>
    /// <param name="rawTimestamp">Stopwatch timestamp captured when the tick fired.</param>
    /// <param name="nextTickOmitted">
    /// True when NIST omits the immediately following tick (positions 29 and 59).
    /// Advances _pllPhase by 2 s instead of 1 s so the PLL correctly expects
    /// the next real tick without losing lock.
    /// </param>
    public long? SubmitSecondTick(long rawTimestamp, bool nextTickOmitted = false)
    {
        if (_state == PllState.Unlocked)
        {
            // Cold start: accept unconditionally and seed phase.
            long advance   = nextTickOmitted ? _freq * 2 : _freq;
            _pllPhase      = rawTimestamp + advance;
            _lastRealTickTime = rawTimestamp;
            _goodTickCount = 1;
            _state         = PllState.Acquiring;
            return rawTimestamp;
        }

        long error = rawTimestamp - _pllPhase;
        long gate  = _gateLocked; // cesium source: same tight gate in both Acquiring and Locked

        // Handle FadingOut state: predictive tick generation during signal dropout.
        if (_state == PllState.FadingOut)
        {
            // Check if signal has recovered (tick within gate of predicted time).
            if (Math.Abs(error) <= gate)
            {
                // Real tick arrived near prediction — begin recovery.
                _lastRealTickTime = rawTimestamp;
                _goodTickCount    = 1;
                double K          = KAcquiring; // restart acquisition with fast gain
                long   recoverCorrectedTs = _pllPhase + (long)(K * error);
                long   recoverNextAdvance = nextTickOmitted ? _freq * 2 : _freq;
                _pllPhase                = recoverCorrectedTs + recoverNextAdvance;
                _state                   = PllState.Acquiring;
                _onLog?.Invoke(
                    $"[TickPLL] Signal recovered from FadeOut — error {error * 1000.0 / _freq:+0;-0} ms, resuming Acquiring");
                return recoverCorrectedTs;
            }

            // Still in FadeOut with no tick near prediction — reject and keep generating synthetics.
            if (Math.Abs(error) > _freq * 2)
            {
                // Tick too far off — could be noise burst, discard.
                return null;
            }

            // Tick is moderately off but not within gate. Check duration of FadeOut.
            long fadeOutDuration = Stopwatch.GetTimestamp() - _fadingOutStart;
            if (fadeOutDuration > _maxFadeOutDuration)
            {
                // Too long without signal — give up and reset.
                _state         = PllState.Unlocked;
                _goodTickCount = 0;
                _onLog?.Invoke(
                    $"[TickPLL] FadeOut timeout (>{(_maxFadeOutDuration * 1000.0 / _freq):F1} s) — full reset to Unlocked");
                return null;
            }

            // Continue in FadeOut, discarding this tick.
            return null;
        }

        if (Math.Abs(error) > gate)
        {
            // Outside gate — reject tick as a glitch or spurious event.
            // A very large error (> 2 s) indicates genuine loss of signal: enter FadeOut.
            if (Math.Abs(error) > _freq * 2)
            {
                _state            = PllState.FadingOut;
                _fadingOutStart   = Stopwatch.GetTimestamp();
                _lastRealTickTime = rawTimestamp;
                // _pllPhase continues from last state, providing predictive generation.
                _onLog?.Invoke(
                    $"[TickPLL] Lost lock — error {error * 1000.0 / _freq:+0;-0} ms; entering FadeOut with predictive ticks");
            }
            return null;
        }

        // Within gate: apply proportional phase correction.
        double K_norm       = _state == PllState.Locked ? KLocked : KAcquiring;
        long   correctedTs = _pllPhase + (long)(K_norm * error);
        // Advance by 2 s when the next position is NIST-omitted; 1 s otherwise.
        long   nextAdvance = nextTickOmitted ? _freq * 2 : _freq;
        _pllPhase          = correctedTs + nextAdvance;
        _lastRealTickTime  = rawTimestamp;

        if (nextTickOmitted)
            _onLog?.Invoke($"[TickPLL] Skipping omitted NIST position — next tick expected in 2 s");

        _goodTickCount++;
        if (_state == PllState.Acquiring && _goodTickCount >= LockThreshold)
        {
            _state = PllState.Locked;
            _onLog?.Invoke($"[TickPLL] Locked after {_goodTickCount} ticks");
        }

        return correctedTs;
    }

    /// <summary>
    /// Seed the PLL from the end of a MinutePulse (P0) event.
    ///
    /// The next SecondTick (second 1) arrives at exactly (1.0 − pulseWidthSeconds)
    /// after the pulse end — typically ~200 ms for a nominal 800 ms P0 pulse.
    /// Seeding from the cesium-accurate P0 end gives the PLL a precise phase reference
    /// and pre-builds tick confidence so the first post-minute tick passes the gate.
    /// </summary>
    /// <param name="pulseWidthSeconds">Measured P0 duration (≈ 0.800 s).</param>
    /// <param name="pulseEndTimestamp">Stopwatch timestamp when P0 ended.</param>
    public void SeedFromMinutePulse(double pulseWidthSeconds, long pulseEndTimestamp)
    {
        // tick-1 epoch = minute start + 1.0 s = (pulseEnd - pulseWidth) + 1.0 s
        //              = pulseEnd + (1.0 - pulseWidth)
        double delaySeconds = 1.0 - pulseWidthSeconds;
        // Clamp: a foreshortened or noise-measured pulse should never produce a negative delay.
        if (delaySeconds < 0.050) delaySeconds = 0.200;

        _pllPhase         = pulseEndTimestamp + (long)(delaySeconds * _freq);
        _lastRealTickTime = pulseEndTimestamp;
        _goodTickCount    = Math.Max(_goodTickCount, 2); // pre-build some confidence
        if (_state == PllState.Unlocked || _state == PllState.FadingOut)
            _state = PllState.Acquiring;

        _onLog?.Invoke(
            $"[TickPLL] Seeded from MinutePulse ({pulseWidthSeconds * 1000:F0} ms) — next tick in {delaySeconds * 1000:F0} ms");
    }

    /// <summary>
    /// Generate a synthetic (predicted) SecondTick during fade periods.
    /// Call this every ~50 ms (every ProcessSamples block) while PLL is in FadingOut state.
    /// Returns the corrected timestamp when the current time is within ±250 ms of an expected
    /// tick boundary, or null otherwise. This generates ~1 synthetic tick per second.
    /// </summary>
    /// <param name="currentTimestamp">Current Stopwatch timestamp (used to match tick timing).</param>
    public long? GeneratePredictiveSecondTick(long currentTimestamp)
    {
        // Only generate synthetic ticks while fading out and within max duration.
        if (_state != PllState.FadingOut)
            return null;

        long fadeOutDuration = currentTimestamp - _fadingOutStart;
        if (fadeOutDuration > _maxFadeOutDuration)
        {
            _state         = PllState.Unlocked;
            _goodTickCount = 0;
            _onLog?.Invoke(
                $"[TickPLL] Predictive ticks timeout — max fade duration exceeded, reset to Unlocked");
            return null;
        }

        // Only generate if we're within ±250 ms of the expected tick time.
        // This provides ~1 synthetic tick per second at predictable timing.
        long error = currentTimestamp - _pllPhase;
        long window = (long)(_freq * 0.250); // ±250 ms window
        if (Math.Abs(error) > window)
            return null; // too far from expected time, wait for next second

        // Generate synthetic tick and prepare for next one.
        long syntheticTs = _pllPhase;
        _pllPhase       += _freq; // advance by 1 second for next prediction
        return syntheticTs;
    }

    /// <summary>
    /// Get the expected timestamp of the next SecondTick (used for predictive generation).
    /// </summary>
    public long GetExpectedNextTickTimestamp() => _pllPhase;

    public void Reset()
    {
        _state         = PllState.Unlocked;
        _pllPhase      = 0;
        _goodTickCount = 0;
        _fadingOutStart = 0;
        _lastRealTickTime = 0;
    }
}
