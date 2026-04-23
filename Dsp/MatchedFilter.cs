namespace WwvDecoder.Dsp;

/// <summary>
/// Matched-filter pulse classifier for WWV LOW-period pulses.
///
/// Problem with simple duration measurement:
///   Duration is measured as the time between the enterThreshold crossing and the
///   exitThreshold crossing. Slow envelope transitions (from the sync detector's
///   lowpass filter) mean the measured duration includes the rise/fall edges, adding
///   a systematic positive bias. Noise on the envelope can also cause spurious re-entry,
///   making a Zero pulse appear as a One or Marker.
///
/// Matched filter approach:
///   Instead of measuring threshold crossings, count how many samples were actually
///   in the LOW state (below the midpoint between HIGH and LOW carrier levels).
///   This "energy counting" is equivalent to a rectangular matched filter against
///   the ideal LOW-period template and is optimal in white Gaussian noise.
///
///   The midpoint threshold sits at 50% of levelHigh. WWV LOW carrier ≈ 31% of HIGH
///   (−10 dB), so the midpoint (50%) cleanly separates LOW from HIGH while being
///   robust to the synchronous detector's ~20 ms envelope transitions.
///
/// Classification boundaries (binary count, modulation-depth-independent):
///   d ≈ time the envelope spent below midThreshold (50 % of levelHigh).
///   For the 2 Hz LP (τ≈80 ms) used after PLL lock, the envelope takes ~120 ms to
///   cross enterThreshold (47 %) downward, so d is shorter than the raw LOW period:
///     Zero   (200 ms LOW): d ≈ 0.10–0.18 s   (2 Hz LP: ~106 ms; 8 Hz LP: ~176 ms)
///     One    (500 ms LOW): d ≈ 0.38–0.48 s   (2 Hz LP: ~406 ms; 8 Hz LP: ~476 ms)
///     Marker (800 ms LOW): d ≈ 0.68–0.78 s   (2 Hz LP: ~706 ms; 8 Hz LP: ~776 ms)
///   Boundaries placed at gaps between the ranges:
///   &lt; 50 ms   → Tick   (noise glitch or post-refractory artifact)
///   50–220 ms → Zero   (nominal 200 ms LOW)
///   220–560 ms → One   (nominal 500 ms LOW)
///   ≥ 560 ms  → Marker (nominal 800 ms LOW)
///
/// Usage: call Classify() at the end of a detected pulse, passing the envelope
/// samples accumulated during the LOW period plus the current levelHigh reference.
/// </summary>
public static class MatchedFilter
{
    /// <summary>
    /// Classify a pulse using the count of samples that were below the midpoint
    /// threshold, rather than raw edge-to-edge duration.
    /// </summary>
    /// <param name="envelopeSamples">Envelope samples collected during the LOW period.</param>
    /// <param name="sampleRate">Audio sample rate (Hz).</param>
    /// <param name="levelHigh">Tracked carrier HIGH level (from PulseDetector).</param>
    /// <returns>Pulse type classification.</returns>
    public static PulseType Classify(IReadOnlyList<double> envelopeSamples,
                                     int sampleRate, double levelHighAtPulseStart)
        => ClassifyWithConfidence(envelopeSamples, sampleRate, levelHighAtPulseStart).Type;

    /// <summary>
    /// Classify and return a confidence score in [0, 1] representing how far the
    /// measured duration is from the nearest class boundary.
    ///
    /// Confidence 1.0 = dead-centre of the class range (unambiguous).
    /// Confidence 0.0 = exactly on a boundary (maximally ambiguous).
    ///
    /// Scale: 125 ms from a boundary → confidence 1.0. Any pulse within 125 ms of a
    /// boundary is penalised proportionally. This is the half-width of the narrowest
    /// class (Zero = 250 ms wide → ±125 ms from centre to boundary).
    ///
    /// In VoteBits(), each frame's votes are weighted by this score so that a marginal
    /// One near the One/Marker boundary (confidence 0.1) cannot outvote a solid Zero
    /// (confidence 0.9) from a prior clean frame.
    /// </summary>
    /// <summary>
    /// Returns the effective LOW duration computed by the energy-weighted matched filter.
    /// Used for boundary calibration logging — exposes the raw d value before classification.
    /// </summary>
    public static double ComputeEffectiveDuration(IReadOnlyList<double> envelopeSamples,
                                                   int sampleRate, double levelHighAtPulseStart)
    {
        double midThreshold = levelHighAtPulseStart * 0.50;
        int belowCount = 0;
        foreach (double s in envelopeSamples)
            if (s < midThreshold) belowCount++;
        return (double)belowCount / sampleRate;
    }

    public static (PulseType Type, double Confidence, double EffectiveDuration) ClassifyWithConfidence(
        IReadOnlyList<double> envelopeSamples, int sampleRate, double levelHighAtPulseStart)
    {
        // Binary count matched filter: count samples that lie below the midpoint
        // threshold (50% of levelHigh). This is modulation-depth-independent — it
        // gives d ≈ actual LOW duration whether the carrier drops to 31% (clean
        // synthetic signal) or near zero (HF propagation fading). The energy-
        // weighted approach (sum of midThreshold − s) scales with modulation depth,
        // causing a 0.316-amplitude LOW to produce d ≈ 0.37 × duration, which
        // misclassifies Markers as Ones for clean signals.
        //
        // Equivalently this is a binary matched filter against a rectangular template:
        // each sample votes "LOW" (1) or "HIGH" (0) with no partial weight.
        double midThreshold = levelHighAtPulseStart * 0.50;
        int belowCount = 0;
        foreach (double s in envelopeSamples)
            if (s < midThreshold) belowCount++;

        double d = (double)belowCount / sampleRate; // effective LOW duration

        // Boundaries calibrated from live SDR measurements of real WWV (Fort Collins) via
        // VB-Audio virtual cable routing, 2 Hz LP after PLL lock.
        //
        // Observed d-value distributions (WWV spec: 0.170 / 0.470 / 0.770 s LOW):
        //   Zero   d ≈ 0.21–0.23 s  → lower bound 50 ms,  upper bound 350 ms
        //   One    d ≈ 0.52–0.54 s  → lower bound 350 ms, upper bound 650 ms
        //   Marker d ≈ 0.77–0.80 s  → lower bound 650 ms
        //
        // The observed d-values are higher than the theoretical (LP-subtracted) predictions
        // because the coherent SynchronousDetector + ALE processing chain produces a faster
        // effective envelope response than a simple RC lowpass model predicts.
        //
        // With these boundaries:
        //   Zero   d=0.22 s → conf = min(0.17, 0.13)/0.08 = 1.0  (was 0.02 at old 0.220 boundary)
        //   One    d=0.53 s → conf = min(0.18, 0.12)/0.08 = 1.0  (was marginal at old 0.560 boundary)
        //   Marker d=0.77 s → conf = (0.77−0.65)/0.08     = 1.0
        //
        // Tick/Zero at 50 ms: post-Marker refractory (150 ms) can truncate the first Zero
        // after a Marker to d ≈ 40–45 ms.  At 50 ms those become Ticks (discarded);
        // gap-fill inserts the correct 0 instead of a low-confidence erasure.
        const double tTick    = 0.050; // Tick/Zero boundary
        const double tZeroOne = 0.350; // Zero/One boundary  (raised from 0.220)
        const double tOneMrk  = 0.650; // One/Marker boundary (raised from 0.560)
        const double scale    = 0.080;

        if (d < tTick)
            return (PulseType.Tick, 0.0, d);

        if (d < tZeroOne)
            return (PulseType.Zero,   Math.Clamp(Math.Min(d - tTick, tZeroOne - d) / scale, 0.0, 1.0), d);

        if (d < tOneMrk)
            return (PulseType.One,    Math.Clamp(Math.Min(d - tZeroOne, tOneMrk - d) / scale, 0.0, 1.0), d);

        // Marker has no upper boundary — confidence grows with distance from tOneMrk,
        // capped at 1.0 once d ≥ tOneMrk + scale (≥ 0.58 s).
        return (PulseType.Marker, Math.Clamp((d - tOneMrk) / scale, 0.0, 1.0), d);
    }
}
