namespace WwvDecoder.Decoder;

/// <summary>
/// One decoded 60-second WWV time frame.
/// </summary>
public class TimeFrame
{
    public DateTime UtcTime { get; init; }
    public int DayOfYear { get; init; }
    public double Dut1Seconds { get; init; }
    public bool DstActive { get; init; }
    public bool LeapSecondPending { get; init; }
    public bool IsValid { get; init; }
    public int ConfidenceFrames { get; set; }

    /// <summary>
    /// True when the BCD decode passed all structural checks and the date gate.
    /// Date, DOY, DUT1, DST, and leap fields are trustworthy; hours/minutes may not be.
    /// </summary>
    public bool SlowFieldsConfident { get; set; }

    /// <summary>
    /// True when the frame also passed the Markov clock check (at least one verified
    /// +1-minute increment). Hours and minutes are trustworthy.
    /// </summary>
    public bool HoursMinutesConfident { get; set; }

    public static TimeFrame Invalid => new() { IsValid = false };
}
