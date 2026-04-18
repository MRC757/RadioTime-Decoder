namespace WwvDecoder.Stations;

/// <summary>
/// Reference database of HF / shortwave standard time-and-frequency (STF) stations
/// broadcasting worldwide as of 2026.
///
/// Sources:
///   NIST Time and Frequency Division (WWV/WWVH)
///   Natural Resources Canada (CHU)
///   ITU-R TF.768 — Standard-frequency and time-signal emissions
///   National Measurement Institute reports
/// </summary>
public static class StationsDatabase
{
    public static readonly IReadOnlyList<StationInfo> All = new List<StationInfo>
    {
        // ── North America ──────────────────────────────────────────────────────

        new()
        {
            CallSign        = "WWV",
            Location        = "Fort Collins, Colorado",
            Country         = "USA",
            Latitude        = 40.6753,
            Longitude       = -105.0394,
            FrequenciesMHz  = [2.5, 5.0, 10.0, 15.0, 20.0, 25.0],
            TimeCodeFormat  = TimeCodeFormat.WwvBcd,
            Status          = StationStatus.Active,
            Agency          = "NIST",
            Notes           = "Operated by NIST since 1923. 100 Hz BCD time code on audio subcarrier. " +
                              "Male voice announcements. 1 kHz/600 Hz tones."
        },

        new()
        {
            CallSign        = "WWVH",
            Location        = "Kekaha, Kauai, Hawaii",
            Country         = "USA",
            Latitude        = 21.9874,
            Longitude       = -159.7697,
            FrequenciesMHz  = [2.5, 5.0, 10.0, 15.0],
            TimeCodeFormat  = TimeCodeFormat.WwvBcd,
            Status          = StationStatus.Active,
            Agency          = "NIST",
            Notes           = "Same BCD format as WWV. Female voice announcements. " +
                              "1.2 kHz/500 Hz tones. Announces 30 s before WWV on shared frequencies."
        },

        new()
        {
            CallSign        = "CHU",
            Location        = "Ottawa, Ontario",
            Country         = "Canada",
            Latitude        = 45.2956,
            Longitude       = -75.7529,
            FrequenciesMHz  = [3.330, 7.850, 14.670],
            TimeCodeFormat  = TimeCodeFormat.ChuFsk,
            Status          = StationStatus.Active,
            Agency          = "NRC / NRCan",
            Notes           = "300 baud Bell-103 FSK bursts (seconds 31–39 of each minute) carry " +
                              "ASCII time code. Audio ticks at each second. French/English voice. " +
                              "Decoder support: future implementation."
        },

        // ── South America ──────────────────────────────────────────────────────

        new()
        {
            CallSign        = "YVTO",
            Location        = "Caracas",
            Country         = "Venezuela",
            Latitude        = 10.4880,
            Longitude       = -66.8792,
            FrequenciesMHz  = [5.0],
            TimeCodeFormat  = TimeCodeFormat.TicksOnly,
            Status          = StationStatus.Active,
            Agency          = "Observatorio Cagigal",
            Notes           = "1 kHz ticks only. No machine-readable audio time code. " +
                              "Single frequency, ~1 kW. UTC."
        },

        new()
        {
            CallSign        = "HD2IOA",
            Location        = "Guayaquil",
            Country         = "Ecuador",
            Latitude        = -2.1666,
            Longitude       = -79.8833,
            FrequenciesMHz  = [3.810, 7.600],
            TimeCodeFormat  = TimeCodeFormat.TicksOnly,
            Status          = StationStatus.Uncertain,
            Agency          = "INEN",
            Notes           = "Ticks only. Reported active intermittently. " +
                              "Reception varies significantly."
        },

        new()
        {
            CallSign        = "LOL",
            Location        = "Buenos Aires",
            Country         = "Argentina",
            Latitude        = -34.6118,
            Longitude       = -58.4173,
            FrequenciesMHz  = [5.0, 10.0, 15.0],
            TimeCodeFormat  = TimeCodeFormat.WwvBcd,
            Status          = StationStatus.Uncertain,
            Agency          = "INTI",
            Notes           = "Uses 100 Hz BCD subcarrier similar to WWV. " +
                              "Operation has been intermittent in recent years."
        },

        // ── Europe ─────────────────────────────────────────────────────────────

        // Note: European LF stations (MSF 60 kHz, DCF77 77.5 kHz, TDF 162 kHz)
        // are not listed here — they require dedicated LF receivers, not HF radios.

        // ── Russia / Central Asia ──────────────────────────────────────────────

        new()
        {
            CallSign        = "RWM",
            Location        = "Taldom, Moscow Oblast",
            Country         = "Russia",
            Latitude        = 56.7333,
            Longitude       = 37.6833,
            FrequenciesMHz  = [4.996, 9.996, 14.996],
            TimeCodeFormat  = TimeCodeFormat.RwmPhase,
            Status          = StationStatus.Active,
            Agency          = "VNIIFTRI",
            Notes           = "Phase-shift-keyed time code on 100 Hz subcarrier. " +
                              "1 kW on each frequency. UTC. Decoder support: future implementation."
        },

        // ── Asia / Pacific ─────────────────────────────────────────────────────

        new()
        {
            CallSign        = "BPM",
            Location        = "Pucheng, Shaanxi",
            Country         = "China",
            Latitude        = 34.9447,
            Longitude       = 109.5414,
            FrequenciesMHz  = [2.5, 5.0, 10.0, 15.0],
            TimeCodeFormat  = TimeCodeFormat.WwvBcd,
            Status          = StationStatus.Active,
            Agency          = "NTSC / CAS",
            Notes           = "100 Hz BCD time code compatible with WWV format. " +
                              "UTC+8 voice announcements but time code is UTC. ~10 kW."
        },

        new()
        {
            CallSign        = "HLA",
            Location        = "Daejeon",
            Country         = "South Korea",
            Latitude        = 36.3547,
            Longitude       = 127.3922,
            FrequenciesMHz  = [5.0],
            TimeCodeFormat  = TimeCodeFormat.TicksOnly,
            Status          = StationStatus.Active,
            Agency          = "KRISS",
            Notes           = "Audio ticks. Korean voice announcements. UTC+9 local time " +
                              "announced alongside UTC."
        },

        new()
        {
            CallSign        = "BSF",
            Location        = "Zhongli, Taoyuan",
            Country         = "Taiwan",
            Latitude        = 24.9575,
            Longitude       = 121.2244,
            FrequenciesMHz  = [5.0],
            TimeCodeFormat  = TimeCodeFormat.TicksOnly,
            Status          = StationStatus.Uncertain,
            Agency          = "TL / NML",
            Notes           = "Ticks only. UTC+8 voice. Operation reported intermittent."
        },
    };

    /// <summary>Stations whose time code the current decoder can process.</summary>
    public static IEnumerable<StationInfo> Supported =>
        All.Where(s => s.IsDecoderSupported && s.Status != StationStatus.Inactive);

    /// <summary>All active or uncertain stations (for reference display).</summary>
    public static IEnumerable<StationInfo> ActiveOrUncertain =>
        All.Where(s => s.Status != StationStatus.Inactive);
}
