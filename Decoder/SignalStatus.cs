using WwvDecoder.ViewModels;

namespace WwvDecoder.Decoder;

public enum TickLockState { NoSignal, Searching, Locked }

public class SignalStatus
{
    public double SignalStrengthPercent { get; set; }
    public double SubcarrierStrengthPercent { get; set; }
    public double LockStrengthPercent { get; set; }
    public LockState LockState { get; set; }
    public int FrameSecondsRemaining { get; set; }
    public double SyncScorePercent { get; set; }
    public double CoarseCarrierHz { get; set; }
    public double AgcGainDb { get; set; }
    public bool AgcEnabled { get; set; }
    public TickLockState TickState { get; set; }
    /// <summary>Non-null when the receiver appears to be in the wrong demodulation mode.</summary>
    public string? ReceiverModeAlert { get; set; }
}
