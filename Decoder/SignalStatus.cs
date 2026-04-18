using WwvDecoder.ViewModels;

namespace WwvDecoder.Decoder;

public class SignalStatus
{
    public double SignalStrengthPercent { get; init; }
    public double SubcarrierStrengthPercent { get; init; }
    public double LockStrengthPercent { get; init; }
    public LockState LockState { get; init; }
    public int FrameSecondsRemaining { get; init; }
}
