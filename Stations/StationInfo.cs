namespace WwvDecoder.Stations;

/// <summary>
/// The time-code encoding format broadcast by a station.
/// Determines which decoder pipeline is used.
/// </summary>
public enum TimeCodeFormat
{
    /// <summary>100 Hz audio subcarrier with pulse-width BCD time code (WWV, WWVH, BPM, LOL).</summary>
    WwvBcd,

    /// <summary>300 baud Bell-103 FSK bursts carrying ASCII time code (CHU Canada).</summary>
    ChuFsk,

    /// <summary>Phase-shift-keyed 100 Hz subcarrier (RWM Russia).</summary>
    RwmPhase,

    /// <summary>Audio ticks only — no machine-readable time code in audio.</summary>
    TicksOnly
}

public enum StationStatus { Active, Inactive, Uncertain }

public class StationInfo
{
    /// <summary>ITU call sign (e.g. "WWV")</summary>
    public required string CallSign { get; init; }

    /// <summary>City / facility name</summary>
    public required string Location { get; init; }

    /// <summary>Country or territory</summary>
    public required string Country { get; init; }

    /// <summary>Approximate latitude, longitude for propagation reference</summary>
    public double Latitude { get; init; }
    public double Longitude { get; init; }

    /// <summary>Broadcast frequencies in MHz, lowest to highest</summary>
    public required double[] FrequenciesMHz { get; init; }

    public TimeCodeFormat TimeCodeFormat { get; init; } = TimeCodeFormat.WwvBcd;
    public StationStatus Status { get; init; } = StationStatus.Active;

    /// <summary>Operating agency</summary>
    public required string Agency { get; init; }

    /// <summary>Extra notes shown in the reference table</summary>
    public string Notes { get; init; } = string.Empty;

    /// <summary>True if this station's time code is supported by the current decoder.</summary>
    public bool IsDecoderSupported => TimeCodeFormat == TimeCodeFormat.WwvBcd;

    public string FrequencyList => string.Join(", ", FrequenciesMHz.Select(f => $"{f} MHz"));

    /// <summary>Display name shown in combo boxes.</summary>
    public string DisplayName => $"{CallSign}  —  {Location}, {Country}";

    public override string ToString() => DisplayName;
}
