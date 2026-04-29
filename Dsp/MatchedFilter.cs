namespace WwvDecoder.Dsp;

/// <summary>
/// Matched-filter pulse classifier for WWV HIGH-period pulses.
///
/// Positive-pulse model:
///   The 100 Hz subcarrier is HIGH at the start of each second and drops after
///   170/470/770 ms (Zero/One/Marker). The matched filter counts how many samples
///   were above the midpoint threshold (50% of levelHigh) — this "energy counting"
///   is equivalent to a rectangular matched filter against the ideal HIGH-period
///   template and is optimal in white Gaussian noise.
///
///   The midpoint threshold at 50% of levelHigh cleanly separates HIGH from LOW while
///   being robust to the synchronous detector's ~20 ms envelope transitions.
///
/// Classification boundaries (binary count, modulation-depth-independent):
///   d ≈ time the envelope spent above midThreshold (50% of levelHigh).
///     Zero   (~170 ms HIGH): d ≈ 0.21–0.23 s
///     One    (~470 ms HIGH): d ≈ 0.52–0.54 s
///     Marker (~770 ms HIGH): d ≈ 0.77–0.80 s
///   Boundaries:
///   &lt; 50 ms    → Tick   (noise glitch or post-refractory artifact)
///   50–350 ms  → Zero
///   350–650 ms → One
///   ≥ 650 ms   → Marker
///
/// Usage: call Classify() at the end of a detected pulse, passing the envelope
/// samples accumulated during the HIGH period plus the current levelHigh reference.
/// </summary>
public static class MatchedFilter
{
    /// <summary>
    /// Classify a pulse using the count of samples above the midpoint threshold.
    /// </summary>
    public static PulseType Classify(IReadOnlyList<double> envelopeSamples,
                                     int sampleRate, double levelHighAtPulseStart)
        => ClassifyWithConfidence(envelopeSamples, sampleRate, levelHighAtPulseStart).Type;

    /// <summary>
    /// Returns the effective HIGH duration computed by the energy-weighted matched filter.
    /// Used for boundary calibration logging.
    /// </summary>
    public static double ComputeEffectiveDuration(IReadOnlyList<double> envelopeSamples,
                                                   int sampleRate, double levelHighAtPulseStart)
    {
        double midThreshold = levelHighAtPulseStart * 0.50;
        int count = 0;
        foreach (double s in envelopeSamples)
            if (s > midThreshold) count++;
        return (double)count / sampleRate;
    }

    /// <summary>
    /// Classify and return a confidence score in [0, 1] representing how far the
    /// measured duration is from the nearest class boundary.
    ///
    /// Confidence 1.0 = dead-centre of the class range (unambiguous).
    /// Confidence 0.0 = exactly on a boundary (maximally ambiguous).
    /// </summary>
    public static (PulseType Type, double Confidence, double EffectiveDuration) ClassifyWithConfidence(
        IReadOnlyList<double> envelopeSamples, int sampleRate, double levelHighAtPulseStart)
    {
        // Binary count matched filter: count samples above the midpoint threshold (50% of
        // levelHigh). Modulation-depth-independent — correct whether the carrier is at full
        // amplitude (clean signal) or partially faded (HF propagation).
        double midThreshold = levelHighAtPulseStart * 0.50;
        int aboveCount = 0;
        foreach (double s in envelopeSamples)
            if (s > midThreshold) aboveCount++;

        double d = (double)aboveCount / sampleRate;

        // Boundaries calibrated from live SDR measurements of real WWV (Fort Collins):
        //   Zero   d ≈ 0.21–0.23 s  → lower bound 50 ms,  upper bound 350 ms
        //   One    d ≈ 0.52–0.54 s  → lower bound 350 ms, upper bound 650 ms
        //   Marker d ≈ 0.77–0.80 s  → lower bound 650 ms
        const double tTick    = 0.050;
        const double tZeroOne = 0.350;
        const double tOneMrk  = 0.650;
        const double scale    = 0.080;

        if (d < tTick)
            return (PulseType.Tick, 0.0, d);

        if (d < tZeroOne)
            return (PulseType.Zero,   Math.Clamp(Math.Min(d - tTick, tZeroOne - d) / scale, 0.0, 1.0), d);

        if (d < tOneMrk)
            return (PulseType.One,    Math.Clamp(Math.Min(d - tZeroOne, tOneMrk - d) / scale, 0.0, 1.0), d);

        return (PulseType.Marker, Math.Clamp((d - tOneMrk) / scale, 0.0, 1.0), d);
    }
}
