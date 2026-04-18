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
/// Classification boundaries — calibrated against observed effective durations:
///   The sync detector's 2 Hz lowpass (τ ≈ 80 ms) delays the envelope response, so
///   the energy-weighted d value is systematically shorter than the raw LOW duration.
///   Observed d distributions from SDR testing (22050 Hz, 2 Hz LP after PLL lock):
///     Zero   (nominal 200 ms LOW): d ≈ 0.10–0.13 s
///     One    (nominal 500 ms LOW): d ≈ 0.34–0.42 s
///     Marker (nominal 800 ms LOW): d ≈ 0.58–0.71 s
///   Boundaries placed at the midpoints of the observed gaps:
///   &lt; 80 ms   → Tick   (noise glitch)
///   80–220 ms → Zero   (nominal 200 ms LOW)
///   220–500 ms → One   (nominal 500 ms LOW)
///   ≥ 500 ms  → Marker (nominal 800 ms LOW)
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
        double energyLow    = 0.0;
        double energyTotal  = midThreshold * envelopeSamples.Count;
        foreach (double s in envelopeSamples)
            energyLow += Math.Max(0.0, midThreshold - s);
        double lowFraction = energyTotal > 0 ? energyLow / energyTotal : 0.0;
        return lowFraction * envelopeSamples.Count / sampleRate;
    }

    public static (PulseType Type, double Confidence, double EffectiveDuration) ClassifyWithConfidence(
        IReadOnlyList<double> envelopeSamples, int sampleRate, double levelHighAtPulseStart)
    {
        // Integrate-and-dump matched filter: weight each sample by how far it sits
        // below the mid-threshold, not merely whether it crosses it.
        // A sample at 5% of HIGH contributes far more energy than one at 49%, so
        // borderline re-entries from noise (which hover just under the threshold)
        // have little effect on the classification. This is the optimal matched
        // filter for a rectangular pulse template in AWGN.
        double midThreshold = levelHighAtPulseStart * 0.50;
        double energyLow    = 0.0;
        double energyTotal  = midThreshold * envelopeSamples.Count; // max possible
        foreach (double s in envelopeSamples)
            energyLow += Math.Max(0.0, midThreshold - s);

        // Normalise to an effective duration: fraction of max energy × buffer duration.
        double lowFraction = energyTotal > 0 ? energyLow / energyTotal : 0.0;
        double d = lowFraction * envelopeSamples.Count / sampleRate; // effective LOW duration

        // Boundaries derived from observed d-value distributions (see class comment).
        // Scale = 80 ms (half-width of the narrowest class, Zero = 0.08–0.22 s, half-width 0.07 s).
        // A pulse whose d sits ≥ scale from the nearest boundary gets confidence 1.0.
        const double tTick   = 0.08;  // Tick/Zero boundary
        const double tZeroOne = 0.22; // Zero/One boundary
        const double tOneMrk = 0.50; // One/Marker boundary
        const double scale   = 0.08;

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
