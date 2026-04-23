using WwvDecoder.Dsp;

namespace WwvDecoder.Tests;

/// <summary>
/// Tests for MatchedFilter.ClassifyWithConfidence.
///
/// The filter uses binary counting: d = count(samples &lt; midThreshold) / sampleRate,
/// where midThreshold = 0.5 × levelHigh. This gives d ≈ actual LOW duration regardless
/// of modulation depth (unlike energy weighting which scales with depth).
///
/// Classification boundaries:
///   d &lt; 0.03 → Tick
///   0.05 ≤ d &lt; 0.22 → Zero
///   0.22 ≤ d &lt; 0.50 → One
///   d ≥ 0.50 → Marker
///
/// NearZeroBuffer (samples at 0.001) has all samples below midThreshold=0.5,
/// so d ≈ buffer_duration — allowing boundary tests to target d directly.
/// </summary>
public class MatchedFilterTests
{
    private const int Sr = 22050;

    // Build a buffer where all samples are near zero so that
    // d ≈ buffer_duration. This lets tests target the classification
    // boundaries directly without depending on envelope LP filter dynamics.
    private static List<double> NearZeroBuffer(double durationSeconds)
    {
        int n = (int)(durationSeconds * Sr);
        return Enumerable.Repeat(0.001, n).ToList();
    }

    [Fact]
    public void VeryShortPulse_20ms_ClassifiedAsTick()
    {
        // d ≈ 0.02 s < tTick (0.03) → Tick
        var buf = NearZeroBuffer(0.020);
        var (type, _, _) = MatchedFilter.ClassifyWithConfidence(buf, Sr, 1.0);
        Assert.Equal(PulseType.Tick, type);
    }

    [Fact]
    public void ZeroPulse_ClassifiedAsZero()
    {
        // d ≈ 0.15 s → in Zero band [0.05, 0.22)
        var buf = NearZeroBuffer(0.15);
        var (type, confidence, d) = MatchedFilter.ClassifyWithConfidence(buf, Sr, 1.0);
        Assert.Equal(PulseType.Zero, type);
        Assert.True(confidence > 0, $"confidence={confidence:F3}");
        Assert.InRange(d, 0.05, 0.22);
    }

    [Fact]
    public void OnePulse_ClassifiedAsOne()
    {
        // d ≈ 0.35 s → in One band [0.22, 0.50)
        var buf = NearZeroBuffer(0.35);
        var (type, confidence, d) = MatchedFilter.ClassifyWithConfidence(buf, Sr, 1.0);
        Assert.Equal(PulseType.One, type);
        Assert.True(confidence > 0, $"confidence={confidence:F3}");
        Assert.InRange(d, 0.22, 0.50);
    }

    [Fact]
    public void MarkerPulse_ClassifiedAsMarker()
    {
        // d ≈ 0.65 s → in Marker band [0.50, ∞)
        var buf = NearZeroBuffer(0.65);
        var (type, confidence, d) = MatchedFilter.ClassifyWithConfidence(buf, Sr, 1.0);
        Assert.Equal(PulseType.Marker, type);
        Assert.True(confidence > 0, $"confidence={confidence:F3}");
        Assert.True(d >= 0.50, $"expected d ≥ 0.50, got {d:F3}");
    }

    [Fact]
    public void EffectiveDuration_IncreasesMonotonically()
    {
        double dShort  = MatchedFilter.ComputeEffectiveDuration(NearZeroBuffer(0.10), Sr, 1.0);
        double dMedium = MatchedFilter.ComputeEffectiveDuration(NearZeroBuffer(0.35), Sr, 1.0);
        double dLong   = MatchedFilter.ComputeEffectiveDuration(NearZeroBuffer(0.65), Sr, 1.0);
        Assert.True(dShort  < dMedium, $"Expected dShort({dShort:F3}) < dMedium({dMedium:F3})");
        Assert.True(dMedium < dLong,   $"Expected dMedium({dMedium:F3}) < dLong({dLong:F3})");
    }

    [Fact]
    public void Confidence_HighForCentralOnePulse()
    {
        // Center of One band: (0.22 + 0.50) / 2 = 0.36 s
        var buf = NearZeroBuffer(0.36);
        var (type, confidence, _) = MatchedFilter.ClassifyWithConfidence(buf, Sr, 1.0);
        Assert.Equal(PulseType.One, type);
        Assert.True(confidence > 0.5, $"Expected high confidence near band center, got {confidence:F3}");
    }

    [Fact]
    public void Confidence_LowNearZeroOneBoundary()
    {
        // Just past the Zero/One boundary at 0.22 s → low confidence One
        var buf = NearZeroBuffer(0.23);
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
    public void HighLevelSamples_ProduceZeroCount_Tick()
    {
        // Samples at or above midThreshold are not counted → d = 0 → Tick.
        // 0.51 > midThreshold (0.50 × levelHigh=1.0) so the binary count is zero.
        var buf = Enumerable.Repeat(0.51, (int)(0.8 * Sr)).ToList();
        var (type, _, d) = MatchedFilter.ClassifyWithConfidence(buf, Sr, 1.0);
        Assert.Equal(PulseType.Tick, type);
        Assert.Equal(0.0, d);
    }
}
