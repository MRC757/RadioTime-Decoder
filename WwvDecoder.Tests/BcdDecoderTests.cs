using WwvDecoder.Decoder;

namespace WwvDecoder.Tests;

public class BcdDecoderTests
{
    // Build a minimal valid 60-bit frame with given time and date.
    private static int[] BuildFrame(int minutes, int hours, int doy, int year,
                                    double dut1 = 0.0, bool leapPending = false,
                                    bool dstActive = false)
    {
        var bits = new int[60];

        // Position markers
        foreach (int m in new[] { 0, 9, 19, 29, 39, 49, 59 })
            bits[m] = 2;

        // BCD encode
        EncodeBcd(bits, [1, 2, 3, 4, 6, 7, 8],          minutes);
        EncodeBcd(bits, [12, 13, 14, 15, 17, 18],         hours);
        EncodeBcd(bits, [22, 23, 24, 25, 27, 28, 30, 31, 33, 34], doy);
        EncodeBcd(bits, [45, 46, 47, 48, 50, 51, 52, 53], year % 100);

        // DUT1
        if (dut1 >= 0) bits[36] = 1;
        else            bits[37] = 1;
        int mag = (int)Math.Round(Math.Abs(dut1) * 10);
        EncodeBcd(bits, [40, 41, 42, 43], mag);

        if (leapPending) bits[55] = 1;
        if (dstActive)   bits[56] = 1;

        return bits;
    }

    private static void EncodeBcd(int[] bits, int[] positions, int value)
    {
        int[] weights = [1, 2, 4, 8, 10, 20, 40, 80, 100, 200];
        int remaining = value;
        for (int i = positions.Length - 1; i >= 0 && remaining > 0; i--)
        {
            int w = weights[i];
            if (remaining >= w)
            {
                bits[positions[i]] = 1;
                remaining -= w;
            }
        }
    }

    [Fact]
    public void Decode_ValidFrame_ReturnsCorrectTime()
    {
        var frame = BuildFrame(minutes: 37, hours: 14, doy: 107, year: 26);
        var result = BcdDecoder.Decode(frame);
        Assert.NotNull(result);
        Assert.Equal(14, result.UtcTime.Hour);
        Assert.Equal(37, result.UtcTime.Minute);
        Assert.Equal(107, result.DayOfYear);
        Assert.Equal(2026, result.UtcTime.Year);
    }

    [Fact]
    public void Decode_MidnightEdge_ReturnsZeroHoursZeroMinutes()
    {
        var frame = BuildFrame(minutes: 0, hours: 0, doy: 1, year: 26);
        var result = BcdDecoder.Decode(frame);
        Assert.NotNull(result);
        Assert.Equal(0, result.UtcTime.Hour);
        Assert.Equal(0, result.UtcTime.Minute);
    }

    [Fact]
    public void Decode_LastMinuteOfDay_ReturnsCorrectTime()
    {
        var frame = BuildFrame(minutes: 59, hours: 23, doy: 365, year: 26);
        var result = BcdDecoder.Decode(frame);
        Assert.NotNull(result);
        Assert.Equal(23, result.UtcTime.Hour);
        Assert.Equal(59, result.UtcTime.Minute);
    }

    [Fact]
    public void Decode_InvalidMinutes_ReturnsNull()
    {
        // minutes = 65 → exceeds 59, should fail
        var bits = BuildFrame(minutes: 37, hours: 14, doy: 107, year: 26);
        // Corrupt minutes tens to give tens=6 → minutes=67
        bits[8] = 1; // weight 40 → tens contribution = 40+20 = 60 → minutes = 60+7 = 67
        bits[7] = 1;
        bits[6] = 0;
        bits[1] = 1; bits[2] = 1; bits[3] = 1; bits[4] = 0; // units = 7
        var result = BcdDecoder.Decode(bits);
        Assert.Null(result);
    }

    [Fact]
    public void Decode_MissingMarker_ReturnsNull()
    {
        var bits = BuildFrame(minutes: 37, hours: 14, doy: 107, year: 26);
        bits[9] = 0; // remove P1 marker
        Assert.Null(BcdDecoder.Decode(bits));
    }

    [Fact]
    public void Decode_NonZeroReservedBit_ReturnsNull()
    {
        var bits = BuildFrame(minutes: 37, hours: 14, doy: 107, year: 26);
        bits[5] = 1; // reserved bit 5 must be 0
        Assert.Null(BcdDecoder.Decode(bits));
    }

    [Fact]
    public void Decode_DUT1_PositiveCorrect()
    {
        var frame = BuildFrame(minutes: 0, hours: 0, doy: 1, year: 26, dut1: +0.3);
        var result = BcdDecoder.Decode(frame);
        Assert.NotNull(result);
        Assert.True(result.Dut1Seconds > 0, "Expected positive DUT1");
        Assert.Equal(0.3, result.Dut1Seconds, precision: 1);
    }

    [Fact]
    public void Decode_LeapSecondFlag()
    {
        var frame = BuildFrame(minutes: 0, hours: 0, doy: 1, year: 26, leapPending: true);
        var result = BcdDecoder.Decode(frame);
        Assert.NotNull(result);
        Assert.True(result.LeapSecondPending);
    }

    [Fact]
    public void Decode_TooManyMarkers_ReturnsNull()
    {
        var bits = BuildFrame(minutes: 37, hours: 14, doy: 107, year: 26);
        // Add markers at many non-marker positions
        for (int i = 0; i < 60; i++)
            if (bits[i] != 2) bits[i] = 2;
        Assert.Null(BcdDecoder.Decode(bits));
    }
}
