using WwvDecoder.Dsp;

namespace WwvDecoder.Tests;

/// <summary>
/// Tests for MatchedFilter.ClassifyWithConfidence.
///
/// The filter uses binary counting: d = count(samples &gt; midThreshold) / sampleRate,
/// where midThreshold = 0.5 × levelHigh. This gives d ≈ actual HIGH duration regardless
/// of modulation depth (positive-pulse model — the HIGH period encodes the bit value).
///
/// Classification boundaries (calibrated from real WWV):
///   d &lt; 0.050            → Tick  (noise glitch)
///   0.050 ≤ d &lt; 0.350   → Zero  (~170 ms HIGH)
///   0.350 ≤ d &lt; 0.650   → One   (~470 ms HIGH)
///   d ≥ 0.650            → Marker (~770 ms HIGH)
///
/// HighBuffer (samples at 0.75, levelHigh = 1.0) has all samples above midThreshold=0.5,
/// so d ≈ buffer_duration — allowing boundary tests to target d directly.
/// </summary>
public class MatchedFilterTests
{
    private const int Sr = 22050;

    // Build a buffer where all samples are above midThreshold (0.5 × levelHigh=1.0)
    // so that d ≈ buffer_duration. This lets tests target the classification
    // boundaries directly without depending on envelope LP filter dynamics.
    private static List<double> HighBuffer(double durationSeconds)
    {
        int n = (int)(durationSeconds * Sr);
        return Enumerable.Repeat(0.75, n).ToList();
    }

    [Fact]
    public void VeryShortPulse_20ms_ClassifiedAsTick()
    {
        // d ≈ 0.02 s < tTick (0.050) → Tick
        var buf = HighBuffer(0.020);
        var (type, _, _) = MatchedFilter.ClassifyWithConfidence(buf, Sr, 1.0);
        Assert.Equal(PulseType.Tick, type);
    }

    [Fact]
    public void ZeroPulse_ClassifiedAsZero()
    {
        // d ≈ 0.15 s → in Zero band [0.050, 0.350)
        var buf = HighBuffer(0.15);
        var (type, confidence, d) = MatchedFilter.ClassifyWithConfidence(buf, Sr, 1.0);
        Assert.Equal(PulseType.Zero, type);
        Assert.True(confidence > 0, $"confidence={confidence:F3}");
        Assert.InRange(d, 0.050, 0.350);
    }

    [Fact]
    public void OnePulse_ClassifiedAsOne()
    {
        // d ≈ 0.50 s → in One band [0.350, 0.650)
        var buf = HighBuffer(0.50);
        var (type, confidence, d) = MatchedFilter.ClassifyWithConfidence(buf, Sr, 1.0);
        Assert.Equal(PulseType.One, type);
        Assert.True(confidence > 0, $"confidence={confidence:F3}");
        Assert.InRange(d, 0.350, 0.650);
    }

    [Fact]
    public void MarkerPulse_ClassifiedAsMarker()
    {
        // d ≈ 0.75 s → in Marker band [0.650, ∞)
        var buf = HighBuffer(0.75);
        var (type, confidence, d) = MatchedFilter.ClassifyWithConfidence(buf, Sr, 1.0);
        Assert.Equal(PulseType.Marker, type);
        Assert.True(confidence > 0, $"confidence={confidence:F3}");
        Assert.True(d >= 0.650, $"expected d ≥ 0.650, got {d:F3}");
    }

    [Fact]
    public void EffectiveDuration_IncreasesMonotonically()
    {
        double dShort  = MatchedFilter.ComputeEffectiveDuration(HighBuffer(0.10), Sr, 1.0);
        double dMedium = MatchedFilter.ComputeEffectiveDuration(HighBuffer(0.50), Sr, 1.0);
        double dLong   = MatchedFilter.ComputeEffectiveDuration(HighBuffer(0.75), Sr, 1.0);
        Assert.True(dShort  < dMedium, $"Expected dShort({dShort:F3}) < dMedium({dMedium:F3})");
        Assert.True(dMedium < dLong,   $"Expected dMedium({dMedium:F3}) < dLong({dLong:F3})");
    }

    [Fact]
    public void Confidence_HighForCentralOnePulse()
    {
        // Center of One band: (0.350 + 0.650) / 2 = 0.500 s
        var buf = HighBuffer(0.500);
        var (type, confidence, _) = MatchedFilter.ClassifyWithConfidence(buf, Sr, 1.0);
        Assert.Equal(PulseType.One, type);
        Assert.True(confidence > 0.5, $"Expected high confidence near band center, got {confidence:F3}");
    }

    [Fact]
    public void Confidence_LowNearZeroOneBoundary()
    {
        // Just past the Zero/One boundary at 0.350 s → low confidence One
        var buf = HighBuffer(0.36);
        var (type, confidence, _) = MatchedFilter.ClassifyWithConfidence(buf, Sr, 1.0);
        Assert.Equal(PulseType.One, type);
        Assert.True(confidence < 0.5, $"Expected low confidence near boundary, got {confidence:F3}");
    }

    [Fact]
    public void EmptyBuffer_ReturnsTick()
    {
        var (type, _, _) = MatchedFilter.ClassifyWithConfidence([], Sr, 1.0);
        Assert.Equal(PulseType.Tick, type);
    }

    [Fact]
    public void NearZeroSamples_ProduceZeroHighCount_Tick()
    {
        // Samples below midThreshold are not counted as HIGH → d = 0 → Tick.
        // 0.001 < midThreshold (0.50 × levelHigh=1.0) so the HIGH count is zero.
        var buf = Enumerable.Repeat(0.001, (int)(0.8 * Sr)).ToList();
        var (type, _, d) = MatchedFilter.ClassifyWithConfidence(buf, Sr, 1.0);
        Assert.Equal(PulseType.Tick, type);
        Assert.Equal(0.0, d);
    }
}
