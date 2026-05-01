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
    /// Total number of Markov-verified increments since the decoder was last started.
    /// Never decremented — survives soft-decay events and re-anchors within a session.
    /// Used alongside ConfidenceFrames to show cumulative lock health in the UI.
    /// </summary>
    public int TotalLifetimeVerifications { get; set; }

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

    /// <summary>
    /// Fraction [0..1] of the 13 hour+minute BCD bit positions (10–13, 15–17, 20–23,
    /// 25–26) that were directly observed (non-zero bit weight) in this frame rather
    /// than gap-filled or inferred from the cross-frame accumulator.
    /// Values below MinDirectTimeBits/13 prevent the Markov anchor from being set.
    /// </summary>
    public float TimeFieldConfidence { get; set; }

    public static TimeFrame Invalid => new() { IsValid = false };
}
