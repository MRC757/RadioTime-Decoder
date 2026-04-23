using WwvDecoder.Decoder;
using WwvDecoder.Dsp;

namespace WwvDecoder.Tests;

/// <summary>
/// End-to-end simulation: drive the full DSP pipeline with a synthetic WWV signal
/// from WwvSignalGenerator and verify the decoder produces the correct time.
/// </summary>
public class SimulationTests
{
    private const int SampleRate = 22050;
    private const int BlockSize  = SampleRate / 20; // 50 ms blocks

    [Fact]
    public void CleanSignal_DecodesCorrectTime_WithinFourMinutes()
    {
        var targetTime = new DateTime(2026, 4, 17, 14, 37, 0, DateTimeKind.Utc);
        var gen = new WwvSignalGenerator(SampleRate, seed: 42);
        gen.SetTime(targetTime);
        gen.NoiseSigma = 0.0; // perfect signal

        // Sample-based clock: FrameDecoder measures inter-marker gaps using Stopwatch
        // timestamps, which advance in real time. During simulation the blocks are processed
        // ~1000× faster than real time, so Stopwatch gaps would be milliseconds instead of
        // 10 seconds. Inject a fake clock that counts ticks proportional to samples rendered
        // so the gap arithmetic gives the correct simulated time.
        long totalSamples = 0;
        long SimTimestamp() => (long)(totalSamples * (double)System.Diagnostics.Stopwatch.Frequency / SampleRate);

        TimeFrame? decoded = null;
        var log = new List<string>();
        var pipeline = new DecoderPipeline(
            onSignalUpdate: _ => { },
            onFrameDecoded: frame => decoded = frame,
            onLog:          msg => log.Add(msg),
            getTimestamp:   SimTimestamp);

        // 4 minutes: enough for ALE convergence (~1-2 s) + P0→P1 anchor (~9 s)
        // + frame collection (~50 s) + possible second frame for Locked state.
        int maxBlocks = 4 * 60 * 20;
        for (int b = 0; b < maxBlocks && decoded == null; b++)
        {
            var block = gen.GenerateBlock(BlockSize);
            pipeline.ProcessSamples(block);
            totalSamples += block.Length;
            // Extra diagnostic: log every 60 simulated seconds
            if (b > 0 && b % (60 * 20) == 0)
                log.Add($"[SIM] t={totalSamples / (double)SampleRate:F0}s");
        }

        string fullLog = string.Join("\n", log);

        if (decoded == null)
            Assert.Fail($"Decoder produced no output within 4 minutes.\nFull log ({log.Count} lines):\n{fullLog}");

        // Always dump log so we can see what led to the decode
        bool hourOk    = decoded.UtcTime.Hour      == targetTime.Hour;
        bool minuteOk  = decoded.UtcTime.Minute    == targetTime.Minute;
        bool doyOk     = decoded.DayOfYear         == targetTime.DayOfYear;
        bool yearOk    = decoded.UtcTime.Year      == targetTime.Year;
        if (!hourOk || !minuteOk || !doyOk || !yearOk)
            Assert.Fail($"Decoded {decoded.UtcTime:HH:mm} DOY={decoded.DayOfYear} Year={decoded.UtcTime.Year} " +
                        $"(expected {targetTime:HH:mm} DOY={targetTime.DayOfYear} Year={targetTime.Year})\n" +
                        $"Full log ({log.Count} lines):\n{fullLog}");

        Assert.Equal(targetTime.Hour,      decoded.UtcTime.Hour);
        Assert.Equal(targetTime.Minute,    decoded.UtcTime.Minute);
        Assert.Equal(targetTime.DayOfYear, decoded.DayOfYear);
        Assert.Equal(targetTime.Year,      decoded.UtcTime.Year);
    }

    [Fact]
    public void GeneratorEncodes_AllMarkerPositions_Correctly()
    {
        // Verify the signal generator places Marker pulses (800 ms LOW) at P0–P6.
        var gen = new WwvSignalGenerator(SampleRate, seed: 0);
        gen.SetTime(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        gen.NoiseSigma = 0;

        // Generate 60 seconds of signal (one full frame)
        int totalSamples = SampleRate * 60;
        var all = new float[totalSamples];
        int offset = 0;
        while (offset < totalSamples)
        {
            var block = gen.GenerateBlock(Math.Min(BlockSize, totalSamples - offset));
            block.CopyTo(all, offset);
            offset += block.Length;
        }

        // Check second 0 (P0 marker): amplitude should be LowAmplitude for first 800 ms
        int lowSamples = (int)(0.80 * SampleRate);
        double avgLow  = all.Take(lowSamples).Select(s => Math.Abs((double)s)).Average();
        double avgHigh = all.Skip(lowSamples + 100).Take(SampleRate / 5)
                            .Select(s => Math.Abs((double)s)).Average();

        // During LOW: avg |sample| ≈ LowAmplitude × (2/π) ≈ 0.20
        // During HIGH: avg |sample| ≈ HighAmplitude × (2/π) ≈ 0.64
        Assert.True(avgLow < avgHigh,
            $"Expected P0 LOW amplitude ({avgLow:F3}) < HIGH amplitude ({avgHigh:F3})");
        Assert.InRange(avgLow / avgHigh, 0.25, 0.45); // ratio ≈ LowAmplitude = 0.316
    }

    [Fact]
    public void Generator_SecondBoundary_ProducesOnePulsePerSecond()
    {
        // Verify timing: for a Zero bit (200ms LOW), the gap from pulse start to
        // the start of the NEXT second should be ~800 ms.
        var gen = new WwvSignalGenerator(SampleRate, seed: 1);
        // Use a time where minute bits produce mostly Zero pulses
        gen.SetTime(new DateTime(2026, 4, 17, 14, 0, 0, DateTimeKind.Utc));
        gen.NoiseSigma = 0;

        // Generate 2 seconds and verify LOW/HIGH envelope pattern
        int twoSecSamples = SampleRate * 2;
        var buf = new float[twoSecSamples];
        int off = 0;
        while (off < twoSecSamples)
        {
            var block = gen.GenerateBlock(Math.Min(BlockSize, twoSecSamples - off));
            block.CopyTo(buf, off);
            off += block.Length;
        }

        // Second 0 = P0 Marker (800ms LOW). First 800ms should be at LowAmplitude.
        double avgSec0Low  = buf.Take((int)(0.80 * SampleRate))
                                .Select(s => Math.Abs((double)s)).Average();
        // After 800ms: HIGH for remainder of second 0.
        double avgSec0High = buf.Skip((int)(0.80 * SampleRate)).Take(SampleRate / 5)
                                .Select(s => Math.Abs((double)s)).Average();

        Assert.True(avgSec0Low < avgSec0High,
            $"P0 Marker: expected LOW({avgSec0Low:F3}) < HIGH({avgSec0High:F3})");
    }
}
