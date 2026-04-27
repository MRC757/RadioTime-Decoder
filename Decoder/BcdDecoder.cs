namespace WwvDecoder.Decoder;

/// <summary>
/// Decodes BCD time fields from the 60-bit WWV frame buffer.
///
/// WWV IRIG-H time code format (NIST), bit positions = second numbers 0–59.
/// All BCD fields are LSB-first (units before tens).
///
///   Pos  0     = P0  reference marker
///   Pos  1     = unused (0)
///   Pos  2     = DST1  (DST in effect today at 00:00 UTC)
///   Pos  3     = LSW   (leap-second warning — pending at end of month)
///   Pos  4– 7  = Year units      (1, 2, 4, 8)
///   Pos  8     = unused (0)
///   Pos  9     = P1  marker
///   Pos 10–13  = Minutes units   (1, 2, 4, 8)
///   Pos 14     = unused (0)
///   Pos 15–17  = Minutes tens    (10, 20, 40)
///   Pos 18     = unused (0)
///   Pos 19     = P2  marker
///   Pos 20–23  = Hours units     (1, 2, 4, 8)
///   Pos 24     = unused (0)
///   Pos 25–26  = Hours tens      (10, 20)
///   Pos 27–28  = unused (0)
///   Pos 29     = P3  marker
///   Pos 30–33  = DOY units       (1, 2, 4, 8)
///   Pos 34     = unused (0)
///   Pos 35–38  = DOY tens        (10, 20, 40, 80)
///   Pos 39     = P4  marker
///   Pos 40–41  = DOY hundreds    (100, 200)
///   Pos 42–48  = unused (0)
///   Pos 49     = P5  marker
///   Pos 50     = DUT1 sign       (1 = positive, 0 = negative)
///   Pos 51–54  = Year tens       (10, 20, 40, 80)
///   Pos 55     = DST2  (DST in effect tomorrow at 24:00 UTC)
///   Pos 56–58  = DUT1 magnitude  (0.1, 0.2, 0.4 s; max = 0.7 s)
///   Pos 59     = P0  next-frame reference marker
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

        // Validate unused bit positions — WWV transmits 0 at these positions.
        // Any non-zero value indicates signal corruption or wrong frame alignment.
        ReadOnlySpan<int> unused = [1, 8, 14, 18, 24, 27, 28, 34, 42, 43, 44, 45, 46, 47, 48];
        foreach (int r in unused)
            if (bits[r] != 0) return null;

        int minutes = DecodeBcd(bits, [10, 11, 12, 13, 15, 16, 17]);              // skip pos 14
        int hours   = DecodeBcd(bits, [20, 21, 22, 23, 25, 26]);                  // skip pos 24
        int doy     = DecodeBcd(bits, [30, 31, 32, 33, 35, 36, 37, 38, 40, 41]); // skip 34; P4@39
        int year    = DecodeBcd(bits, [4, 5, 6, 7, 51, 52, 53, 54]);              // units 4–7, tens 51–54

        // Sanity checks — year is 2-digit BCD (0–99)
        if (minutes > 59 || hours > 23 || doy < 1 || doy > 366 || year > 99) return null;

        // DUT1: sign bit 50 (1=positive, 0=negative), magnitude bits 56–58 (0.1, 0.2, 0.4 s)
        double dut1Sign      = bits[50] == 1 ? +1.0 : -1.0;
        double dut1Magnitude = (bits[56] == 1 ? 0.1 : 0.0)
                             + (bits[57] == 1 ? 0.2 : 0.0)
                             + (bits[58] == 1 ? 0.4 : 0.0);
        double dut1 = dut1Sign * dut1Magnitude;

        bool leapPending = bits[3]  == 1;
        bool dstActive   = bits[2]  == 1 || bits[55] == 1;

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
