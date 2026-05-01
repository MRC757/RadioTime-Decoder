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
    /// <summary>Instantaneous signal-to-noise ratio in dB at the most recent pulse. NaN when no pulse observed.</summary>
    public double SnrDb { get; set; } = double.NaN;
    /// <summary>Mean soft-decision bit confidence for the most recently completed frame (0–100). NaN before first frame.</summary>
    public double FrameQualityPercent { get; set; } = double.NaN;
    /// <summary>Non-null when the receiver appears to be in the wrong demodulation mode.</summary>
    public string? ReceiverModeAlert { get; set; }
    /// <summary>Non-null when the input audio level is too high and the user should reduce volume.</summary>
    public string? InputSaturationAlert { get; set; }
}
