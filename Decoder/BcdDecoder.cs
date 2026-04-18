namespace WwvDecoder.Decoder;

/// <summary>
/// Decodes BCD time fields from the 60-bit WWV frame buffer.
///
/// WWV BCD frame layout (bit positions, seconds 0–59).
/// Markers occur every 10 seconds. BCD digits are LSB-first (units before tens).
///
///   Pos  0     = P0  reference marker
///   Pos  1– 4  = Minutes units  (1, 2, 4, 8)
///   Pos  5     = reserved (0)
///   Pos  6– 8  = Minutes tens   (10, 20, 40)
///   Pos  9     = P1  marker
///   Pos 10–11  = reserved (0)
///   Pos 12–15  = Hours units    (1, 2, 4, 8)
///   Pos 16     = reserved (0)
///   Pos 17–18  = Hours tens     (10, 20)
///   Pos 19     = P2  marker
///   Pos 20–21  = reserved (0)
///   Pos 22–25  = Day units      (1, 2, 4, 8)
///   Pos 26     = reserved (0)
///   Pos 27–28  = Day tens       (10, 20)
///   Pos 29     = P3  marker
///   Pos 30–31  = Day tens cont  (40, 80)
///   Pos 32     = reserved (0)
///   Pos 33–34  = Day hundreds   (100, 200)
///   Pos 35     = reserved (0)
///   Pos 36–37  = DUT1 sign (+, -)
///   Pos 38     = reserved (0)
///   Pos 39     = P4  marker
///   Pos 40–43  = DUT1 magnitude (1, 2, 4, 8) × 0.1 s
///   Pos 44     = reserved (0)
///   Pos 45–48  = Year units     (1, 2, 4, 8)
///   Pos 49     = P5  marker
///   Pos 50–53  = Year tens      (10, 20, 40, 80)
///   Pos 54     = reserved (0)
///   Pos 55     = Leap second warning
///   Pos 56     = DST bit 1
///   Pos 57     = DST bit 2
///   Pos 58     = reserved (0)
///   Pos 59     = P0  next frame reference marker
/// </summary>
public static class BcdDecoder
{
    public static TimeFrame? Decode(int[] bits)
    {
        if (bits.Length < 60) return null;

        // Validate position markers — WWV markers at every 10th second: 0, 9, 19, 29, 39, 49, 59
        if (!IsMarker(bits, 0)  || !IsMarker(bits, 9)  ||
            !IsMarker(bits, 19) || !IsMarker(bits, 29) ||
            !IsMarker(bits, 39) || !IsMarker(bits, 49) ||
            !IsMarker(bits, 59))
            return null;

        // Reject frames with excessive spurious markers at non-marker positions.
        // A clean frame has exactly 7 markers.  More than 5 extras (12 total) indicates
        // heavy signal corruption — HF fading, SDR audio-AGC pumping, or interference —
        // and decoding would produce a wrong time rather than no time.
        int totalMarkers = 0;
        for (int i = 0; i < 60; i++)
            if (bits[i] == 2) totalMarkers++;
        if (totalMarkers > 12) return null;

        // Validate reserved bit positions — WWV transmits 0 at these positions.
        // Any non-zero value indicates signal corruption or wrong frame alignment.
        ReadOnlySpan<int> reserved = [5, 10, 11, 16, 20, 21, 26, 32, 35, 38, 44, 54, 58];
        foreach (int r in reserved)
            if (bits[r] != 0) return null;

        int minutes = DecodeBcd(bits, [1, 2, 3, 4, 6, 7, 8]);          // skip pos 5
        int hours   = DecodeBcd(bits, [12, 13, 14, 15, 17, 18]);       // skip pos 16
        int doy     = DecodeBcd(bits, [22, 23, 24, 25, 27, 28, 30, 31, 33, 34]); // skip 26, 32; P3@29
        int year    = DecodeBcd(bits, [45, 46, 47, 48, 50, 51, 52, 53]); // P5@49

        // Sanity checks — year is 2-digit BCD (0–99)
        if (minutes > 59 || hours > 23 || doy < 1 || doy > 366 || year > 99) return null;

        // DUT1: sign bits 36–37, magnitude bits 40–43
        // Bit 36 = positive, bit 37 = negative. Magnitude is 0–9 (×0.1 s).
        double dut1Sign = bits[36] == 1 ? +1.0 : bits[37] == 1 ? -1.0 : 0.0;
        int dut1Magnitude = DecodeBcd(bits, [40, 41, 42, 43]);
        if (dut1Magnitude > 9) return null; // invalid BCD digit
        double dut1 = dut1Sign * dut1Magnitude * 0.1;

        bool leapPending = bits[55] == 1;
        bool dstActive   = bits[56] == 1 || bits[57] == 1;

        // Build UTC DateTime from year/doy/hours/minutes
        int fullYear = 2000 + year;
        var utc = new DateTime(fullYear, 1, 1, hours, minutes, 0, DateTimeKind.Utc)
                      .AddDays(doy - 1);

        return new TimeFrame
        {
            UtcTime           = utc,
            DayOfYear         = doy,
            Dut1Seconds       = dut1,
            DstActive         = dstActive,
            LeapSecondPending = leapPending,
            IsValid           = true
        };
    }

    // bit values [1,2,4,8, 10,20,40,80, 100,200] mapped to positions
    private static readonly int[] BcdWeights = [1, 2, 4, 8, 10, 20, 40, 80, 100, 200];

    private static int DecodeBcd(int[] bits, int[] positions)
    {
        int value = 0;
        for (int i = 0; i < positions.Length && i < BcdWeights.Length; i++)
            if (bits[positions[i]] == 1)
                value += BcdWeights[i];
        return value;
    }

    private static bool IsMarker(int[] bits, int pos) => bits[pos] == 2; // 2 = marker
}
