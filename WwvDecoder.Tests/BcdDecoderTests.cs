using WwvDecoder.Decoder;

namespace WwvDecoder.Tests;

public class BcdDecoderTests
{
    // Build a valid 60-bit frame at the correct NIST IRIG-H bit positions.
    // Bit 0 is the frame-reference hole (Pr) — left at 0, NOT a marker.
    // Six position markers: P1–P5 at [9,19,29,39,49] and P0 at [59].
    // Positions match BcdDecoder.cs exactly:
    //   Minutes units [10-13], tens [15-17]
    //   Hours units   [20-23], tens [25-26]
    //   DOY units     [30-33], tens [35-38], hundreds [40-41]
    //   Year units    [ 4- 7], tens [51-54]
    //   DUT1 sign     [50], magnitude [56-58]
    //   Leap          [3], DST1 [2], DST2 [55]
    private static int[] BuildFrame(int minutes, int hours, int doy, int year,
                                    double dut1 = 0.0, bool leapPending = false,
                                    bool dstActive = false)
    {
        var bits = new int[60];

        foreach (int m in new[] { 9, 19, 29, 39, 49, 59 })
            bits[m] = 2;

        EncodeBcd(bits, [10, 11, 12, 13, 15, 16, 17],              minutes);
        EncodeBcd(bits, [20, 21, 22, 23, 25, 26],                   hours);
        EncodeBcd(bits, [30, 31, 32, 33, 35, 36, 37, 38, 40, 41],   doy);
        EncodeBcd(bits, [4,  5,  6,  7,  51, 52, 53, 54],           year % 100);

        // DUT1: sign at bit 50 (1=positive), magnitude at bits 56,57,58 (0.1, 0.2, 0.4)
        bits[50] = dut1 >= 0 ? 1 : 0;
        int mag = (int)Math.Round(Math.Abs(dut1) * 10);
        if ((mag & 1) != 0) bits[56] = 1;
        if ((mag & 2) != 0) bits[57] = 1;
        if ((mag & 4) != 0) bits[58] = 1;

        if (leapPending) bits[3]  = 1;
        if (dstActive)   bits[2]  = 1;

        return bits;
    }

    private static void EncodeBcd(int[] bits, int[] positions, int value)
    {
        int[] weights = [1, 2, 4, 8, 10, 20, 40, 80, 100, 200];
        int remaining = value;
        for (int i = positions.Length - 1; i >= 0; i--)
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
        // Build a valid frame then corrupt minutes to 60 (tens=6 → invalid)
        var bits = BuildFrame(minutes: 0, hours: 14, doy: 107, year: 26);
        // Set minutes tens bits to encode tens=6 (bits 15,16,17 with weights 10,20,40)
        // 6*10=60 → only encodable as 40+20=60, so set bits 16 and 17
        bits[16] = 1; // weight 20
        bits[17] = 1; // weight 40 → tens = 60, total minutes >= 60
        var result = BcdDecoder.Decode(bits);
        Assert.Null(result);
    }

    [Fact]
    public void Decode_MissingMarker_ReturnsNull()
    {
        // Removing any of the six required position markers must reject the frame.
        var bits = BuildFrame(minutes: 37, hours: 14, doy: 107, year: 26);
        bits[19] = 0; // remove P2 marker
        Assert.Null(BcdDecoder.Decode(bits));
    }

    [Fact]
    public void Decode_NonZeroReservedBit_ReturnsNull()
    {
        var bits = BuildFrame(minutes: 37, hours: 14, doy: 107, year: 26);
        bits[8] = 1; // reserved bit 8 must be 0
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
    public void Decode_DUT1_Negative()
    {
        var frame = BuildFrame(minutes: 0, hours: 0, doy: 1, year: 26, dut1: -0.2);
        var result = BcdDecoder.Decode(frame);
        Assert.NotNull(result);
        Assert.True(result.Dut1Seconds < 0, "Expected negative DUT1");
        Assert.Equal(-0.2, result.Dut1Seconds, precision: 1);
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
    public void Decode_DstFlag()
    {
        var frame = BuildFrame(minutes: 0, hours: 0, doy: 1, year: 26, dstActive: true);
        var result = BcdDecoder.Decode(frame);
        Assert.NotNull(result);
        Assert.True(result.DstActive);
    }

    [Fact]
    public void Decode_TooManyMarkers_ReturnsNull()
    {
        // 60 markers far exceeds the 11-marker sanity limit.
        var bits = BuildFrame(minutes: 37, hours: 14, doy: 107, year: 26);
        for (int i = 0; i < 60; i++)
            bits[i] = 2;
        Assert.Null(BcdDecoder.Decode(bits));
    }

    [Fact]
    public void Decode_AllSixMarkersRequired()
    {
        // Removing any one of the six position markers must return null.
        int[] markerPositions = [9, 19, 29, 39, 49, 59];
        foreach (int pos in markerPositions)
        {
            var bits = BuildFrame(minutes: 30, hours: 12, doy: 100, year: 26);
            bits[pos] = 0; // remove one marker
            Assert.Null(BcdDecoder.Decode(bits));
        }
    }

    [Fact]
    public void Decode_MaxValidDoy366_ReturnsFrame()
    {
        // 2024 is a leap year → DOY 366 is valid
        var frame = BuildFrame(minutes: 0, hours: 0, doy: 366, year: 24);
        var result = BcdDecoder.Decode(frame);
        Assert.NotNull(result);
        Assert.Equal(366, result.DayOfYear);
    }

    [Fact]
    public void Decode_Doy367_ReturnsNull()
    {
        var frame = BuildFrame(minutes: 0, hours: 0, doy: 367, year: 26);
        Assert.Null(BcdDecoder.Decode(frame));
    }

    [Fact]
    public void Decode_Hours24_ReturnsNull()
    {
        var frame = BuildFrame(minutes: 0, hours: 24, doy: 1, year: 26);
        Assert.Null(BcdDecoder.Decode(frame));
    }
}
