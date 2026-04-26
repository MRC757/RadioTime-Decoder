using System.IO;
using System.Text;

namespace WwvDecoder.Logging;

/// <summary>
/// Writes machine-readable CSV diagnostic records for offline analysis and tuning.
///
/// Three CSV files are produced in the same directory as the text log:
///   diag-blocks-yyyy-MM-dd.csv  — per-second aggregate metrics from every pipeline stage
///   diag-pulses-yyyy-MM-dd.csv  — one row per detected pulse with full classification detail
///   diag-frames-yyyy-MM-dd.csv  — one row per decoded BCD frame
///
/// AutoFlush is off for the CSV writers to avoid per-write latency on the audio callback
/// thread. The pipeline calls Flush() every 5 seconds via the existing status-log tick.
/// </summary>
public sealed class DiagnosticLogger : IDisposable
{
    private readonly StreamWriter _blockWriter;
    private readonly StreamWriter _pulseWriter;
    private readonly StreamWriter _frameWriter;
    private readonly object _lock = new();
    private readonly long _sessionStartTick = System.Diagnostics.Stopwatch.GetTimestamp();

    public DiagnosticLogger()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "WwvDecoder");
        Directory.CreateDirectory(dir);
        string date = DateTime.Now.ToString("yyyy-MM-dd");

        _blockWriter = OpenCsv(Path.Combine(dir, $"diag-blocks-{date}.csv"),
            "timestamp_utc,elapsed_s," +
            "agc_gain_db,agc_level,input_rms_db," +
            "notch60_in_db,notch60_out_db,notch60_rej_db," +
            "notch120_in_db,notch120_out_db,notch120_rej_db," +
            "ale_in_rms_db,ale_out_rms_db,ale_improvement_db,ale_weight_norm," +
            "envelope,noise_floor,snr_db," +
            "lp_hz," +
            "level_high,peak_envelope,is_fading,is_amplitude_unstable,amplitude_variability," +
            "sync_score_pct,carrier_score,cadence_score,best_carrier_hz");

        _pulseWriter = OpenCsv(Path.Combine(dir, $"diag-pulses-{date}.csv"),
            "timestamp_utc,elapsed_s," +
            "type,duration_ms,effective_duration_ms,confidence," +
            "matched_type,duration_type,classifiers_agree," +
            "level_high,noise_floor,snr_db,is_fading");

        _frameWriter = OpenCsv(Path.Combine(dir, $"diag-frames-{date}.csv"),
            "timestamp_utc,elapsed_s," +
            "decoded_utc,day_of_year,dut1_s,dst_active,leap_pending," +
            "confidence_frames,is_valid");
    }

    public double ElapsedSeconds =>
        (double)(System.Diagnostics.Stopwatch.GetTimestamp() - _sessionStartTick) /
        System.Diagnostics.Stopwatch.Frequency;

    public void WriteBlock(DiagBlockMetrics m)
    {
        lock (_lock)
        {
            try
            {
                _blockWriter.WriteLine(
                    $"{m.TimestampUtc},{m.ElapsedSeconds:F2}," +
                    $"{m.AgcGainDb:F2},{m.AgcLevel:F6},{m.InputRmsDb:F2}," +
                    $"{m.Notch60InDb:F2},{m.Notch60OutDb:F2},{m.Notch60RejDb:F2}," +
                    $"{m.Notch120InDb:F2},{m.Notch120OutDb:F2},{m.Notch120RejDb:F2}," +
                    $"{m.AleInRmsDb:F2},{m.AleOutRmsDb:F2},{m.AleImprovementDb:F2},{m.AleWeightNorm:F4}," +
                    $"{m.Envelope:F6},{m.NoiseFloor:F6},{m.SnrDb:F2}," +
                    $"{m.LpHz:F1}," +
                    $"{m.LevelHigh:F6},{m.PeakEnvelope:F6}," +
                    $"{B(m.IsFading)},{B(m.IsAmplitudeUnstable)},{m.AmplitudeVariability:F4}," +
                    $"{m.SyncScorePct:F2},{m.CarrierScore:F4},{m.CadenceScore:F4},{m.BestCarrierHz:F3}");
            }
            catch { }
        }
    }

    public void WritePulse(DiagPulseRecord p)
    {
        lock (_lock)
        {
            try
            {
                _pulseWriter.WriteLine(
                    $"{p.TimestampUtc},{p.ElapsedSeconds:F3}," +
                    $"{p.Type},{p.DurationMs:F1},{p.EffectiveDurationMs:F1},{p.Confidence:F3}," +
                    $"{p.MatchedType ?? "none"},{p.DurationType},{B(p.ClassifiersAgree)}," +
                    $"{p.LevelHigh:F6},{p.NoiseFloor:F6},{p.SnrDb:F2},{B(p.IsFading)}");
            }
            catch { }
        }
    }

    public void WriteFrame(DiagFrameRecord f)
    {
        lock (_lock)
        {
            try
            {
                _frameWriter.WriteLine(
                    $"{f.TimestampUtc},{f.ElapsedSeconds:F2}," +
                    $"{f.DecodedUtc},{f.DayOfYear},{f.Dut1Seconds:F1}," +
                    $"{B(f.DstActive)},{B(f.LeapPending)}," +
                    $"{f.ConfidenceFrames},{B(f.IsValid)}");
            }
            catch { }
        }
    }

    public void Flush()
    {
        lock (_lock)
        {
            try { _blockWriter.Flush(); } catch { }
            try { _pulseWriter.Flush(); } catch { }
            try { _frameWriter.Flush(); } catch { }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            try { _blockWriter.Flush(); _blockWriter.Dispose(); } catch { }
            try { _pulseWriter.Flush(); _pulseWriter.Dispose(); } catch { }
            try { _frameWriter.Flush(); _frameWriter.Dispose(); } catch { }
        }
    }

    private static string B(bool v) => v ? "1" : "0";

    private static StreamWriter OpenCsv(string path, string header)
    {
        bool needHeader = !File.Exists(path) || new FileInfo(path).Length == 0;
        var sw = new StreamWriter(path, append: true, Encoding.UTF8) { AutoFlush = false };
        if (needHeader) sw.WriteLine(header);
        return sw;
    }
}

// ── Record types ─────────────────────────────────────────────────────────────

/// <summary>Aggregate signal-chain metrics computed once per second.</summary>
public record DiagBlockMetrics(
    string TimestampUtc,
    double ElapsedSeconds,
    // AGC
    double AgcGainDb,
    double AgcLevel,
    double InputRmsDb,
    // Notch effectiveness (Goertzel on the 1 s accumulated buffer before/after each notch)
    double Notch60InDb,
    double Notch60OutDb,
    double Notch60RejDb,
    double Notch120InDb,
    double Notch120OutDb,
    double Notch120RejDb,
    // ALE
    double AleInRmsDb,
    double AleOutRmsDb,
    double AleImprovementDb,
    double AleWeightNorm,
    // Synchronous detector
    double Envelope,
    double NoiseFloor,
    double SnrDb,
    double LpHz,
    // Pulse detector
    double LevelHigh,
    double PeakEnvelope,
    bool IsFading,
    bool IsAmplitudeUnstable,
    double AmplitudeVariability,
    // Sync quality scorer
    double SyncScorePct,
    double CarrierScore,
    double CadenceScore,
    double BestCarrierHz);

/// <summary>Full classification context for a single detected pulse.</summary>
public record DiagPulseRecord(
    string TimestampUtc,
    double ElapsedSeconds,
    string Type,
    double DurationMs,
    double EffectiveDurationMs,
    double Confidence,
    string? MatchedType,
    string DurationType,
    bool ClassifiersAgree,
    double LevelHigh,
    double NoiseFloor,
    double SnrDb,
    bool IsFading);

/// <summary>Result of a single BCD frame decode attempt.</summary>
public record DiagFrameRecord(
    string TimestampUtc,
    double ElapsedSeconds,
    string DecodedUtc,
    int DayOfYear,
    double Dut1Seconds,
    bool DstActive,
    bool LeapPending,
    int ConfidenceFrames,
    bool IsValid);
