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

    public static TimeFrame Invalid => new() { IsValid = false };
}
