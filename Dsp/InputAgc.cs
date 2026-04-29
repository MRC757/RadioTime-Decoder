namespace WwvDecoder.Dsp;

/// <summary>
/// Input Automatic Gain Control (AGC) — normalizes audio level before DSP processing.
///
/// SDR and receiver audio output levels vary widely depending on gain settings, signal
/// strength, and software configuration. The downstream DSP chain (notch filters,
/// synchronous detector) assumes a reasonably consistent input level. Without AGC:
///   - A weak signal may never drive the envelope above the noise floor threshold.
///   - A hot signal may saturate the filter state variables.
///
/// Algorithm: peak-following with asymmetric time constants.
///   - Attack (signal getting louder): ~10 ms — react quickly to prevent overload.
///   - Decay  (signal getting quieter): ~1000 ms — hold gain during fading so the
///     decoder doesn't oscillate between gain levels during pulse LOW periods.
///
/// The AGC targets a normalized peak level of 0.25 (25% of full scale). Gain is
/// clamped to [1, 500] to avoid amplifying pure noise into signal.
/// </summary>
public class InputAgc
{
    private readonly double _attackAlpha;
    private readonly double _decayAlpha;
    private const double TargetLevel = 0.25;
    private const double MinLevel    = 0.0005; // noise floor minimum — prevents gain runaway
    private const double MaxGain     = 500.0;

    private double _level = MinLevel;

    // Snapshot of _level taken at the most recent SecondTick.  Used by BeginFastRecovery()
    // to restore the pre-minute-pulse gain rather than resetting all the way to 1×.
    private double _levelSnapshot = MinLevel;
    private bool   _hasSnapshot;

    /// <summary>Current applied gain (1–500×). At MaxGain the signal is near-inaudible.</summary>
    public double CurrentGain => Math.Min(MaxGain, TargetLevel / _level);

    /// <summary>Tracked peak input level (linear FS). Useful for diagnosing weak/overloaded input.</summary>
    public double CurrentLevel => _level;

    public InputAgc(int sampleRate, double attackSeconds = 0.300, double decaySeconds = 2.000)
    {
        // Time constant τ → per-sample alpha = 1 - exp(-1/(τ × sampleRate))
        _attackAlpha = 1.0 - Math.Exp(-1.0 / (attackSeconds * sampleRate));
        _decayAlpha  = 1.0 - Math.Exp(-1.0 / (decaySeconds  * sampleRate));
    }

    public void Reset()
    {
        _level         = MinLevel;
        _levelSnapshot = MinLevel;
        _hasSnapshot   = false;
    }

    /// <summary>
    /// Saves the current tracked level so BeginFastRecovery() can restore it exactly.
    /// Call once per SecondTick; the snapshot at second 59 (just before the minute pulse)
    /// captures the pre-pulse running gain.
    /// </summary>
    public void SnapshotLevel()
    {
        _levelSnapshot = _level;
        _hasSnapshot   = true;
    }

    /// <summary>
    /// Restores the tracked level to the pre-minute-pulse snapshot so gain returns to its
    /// normal running value immediately after the 800 ms minute pulse ends.
    ///
    /// Without this, the 5 s decay τ holds gain suppressed for 15–20 s post-pulse, making
    /// the first few second ticks too weak for the TickDetector to detect.
    ///
    /// Falls back to TargetLevel (gain = 1×) if no snapshot has been taken yet (cold start).
    /// </summary>
    public void BeginFastRecovery()
    {
        if (_hasSnapshot && _level > _levelSnapshot)
            _level = _levelSnapshot;   // restore pre-pulse gain exactly
        else if (_level > TargetLevel)
            _level = TargetLevel;      // cold-start fallback: at least stop gain suppression
    }

    public float[] ProcessBlock(float[] input)
    {
        var output = new float[input.Length];
        for (int i = 0; i < input.Length; i++)
        {
            double abs = Math.Abs(input[i]);

            // Asymmetric peak follower: attack fast, decay slow
            if (abs > _level)
                _level += _attackAlpha * (abs - _level);
            else
                _level += _decayAlpha  * (abs - _level);

            // Keep level above minimum to avoid divide-by-zero and gain runaway
            if (_level < MinLevel) _level = MinLevel;

            // Apply gain, clamped to MaxGain
            double gain = Math.Min(MaxGain, TargetLevel / _level);
            output[i] = (float)(input[i] * gain);
        }
        return output;
    }
}
