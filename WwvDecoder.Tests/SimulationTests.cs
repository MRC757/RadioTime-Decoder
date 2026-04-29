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
        // Verify the signal generator correctly implements the NIST frame structure:
        //   - Second 0 is the frame-reference hole (Pr): 100Hz at LOW for entire second.
        //   - P1 marker at second 9: 770ms HIGH starting at t+30ms, then LOW.
        var gen = new WwvSignalGenerator(SampleRate, seed: 0);
        gen.SetTime(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        gen.NoiseSigma    = 0;
        gen.TickAmplitude = 0; // isolate 100 Hz carrier; tick contamination shifts ratio

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

        // Second 0 = hole: 100Hz must be at LOW amplitude throughout.
        // avg|sample| ≈ LowAmplitude × (2/π) ≈ 0.201
        int holeWindow = (int)(0.80 * SampleRate); // 800 ms sample of second 0
        double avgHole = all.Take(holeWindow).Select(s => Math.Abs((double)s)).Average();

        // P1 marker (second 9): HIGH for 770ms starting at 9.030s, then LOW.
        // Measure well inside the HIGH region and well inside the LOW region.
        int p1HighStart  = (int)(9.060 * SampleRate); // 60ms into second 9 (past 30ms gap)
        int p1HighWindow = (int)(0.50  * SampleRate); // 500ms sample inside the 770ms HIGH
        int p1LowStart   = (int)(9.820 * SampleRate); // 820ms into second 9 (after HIGH ends)
        int p1LowWindow  = SampleRate / 5;             // 200ms sample

        double avgHigh = all.Skip(p1HighStart).Take(p1HighWindow).Select(s => Math.Abs((double)s)).Average();
        double avgLow  = all.Skip(p1LowStart).Take(p1LowWindow).Select(s => Math.Abs((double)s)).Average();

        // Hole and LOW period should both be at LowAmplitude × (2/π) ≈ 0.201
        // HIGH period should be at HighAmplitude × (2/π) ≈ 0.637
        Assert.True(avgHigh > avgLow,
            $"Expected P1 HIGH ({avgHigh:F3}) > LOW ({avgLow:F3})");
        Assert.InRange(avgLow  / avgHigh, 0.25, 0.45); // ≈ 0.316 (LowAmplitude)
        Assert.InRange(avgHole / avgHigh, 0.25, 0.45); // hole = LOW amplitude, not HIGH
    }

    [Fact]
    public void Generator_SecondBoundary_HoleAndFirstBit()
    {
        // Verify the NIST frame boundary:
        //   Second 0 = hole (Pr): 100Hz carrier at LOW amplitude for the entire second.
        //   Second 1 = first data bit (bit 1 = reserved = Zero): 30ms suppression,
        //              then 170ms HIGH, then LOW for the rest of the second.
        var gen = new WwvSignalGenerator(SampleRate, seed: 1);
        gen.SetTime(new DateTime(2026, 4, 17, 14, 0, 0, DateTimeKind.Utc));
        gen.NoiseSigma    = 0;
        gen.TickAmplitude = 0; // isolate 100 Hz carrier

        int twoSecSamples = SampleRate * 2;
        var buf = new float[twoSecSamples];
        int off = 0;
        while (off < twoSecSamples)
        {
            var block = gen.GenerateBlock(Math.Min(BlockSize, twoSecSamples - off));
            block.CopyTo(buf, off);
            off += block.Length;
        }

        // Second 0 = hole: entirely LOW amplitude
        double avgHole = buf.Take((int)(0.80 * SampleRate))
                            .Select(s => Math.Abs((double)s)).Average();

        // Second 1 Zero bit: HIGH from 1.030s to 1.200s (170ms after 30ms suppression)
        // Measure inside the HIGH window (1.060–1.180s) and the LOW window (1.400–1.600s)
        double avgSec1High = buf.Skip((int)(1.060 * SampleRate)).Take((int)(0.10 * SampleRate))
                                .Select(s => Math.Abs((double)s)).Average();
        double avgSec1Low  = buf.Skip((int)(1.400 * SampleRate)).Take(SampleRate / 5)
                                .Select(s => Math.Abs((double)s)).Average();

        Assert.True(avgSec1High > avgHole,
            $"Sec1 HIGH ({avgSec1High:F3}) should exceed hole ({avgHole:F3})");
        Assert.True(avgSec1High > avgSec1Low,
            $"Sec1 HIGH ({avgSec1High:F3}) > LOW ({avgSec1Low:F3})");
        // Hole and sec1 LOW are both at LowAmplitude
        Assert.InRange(avgHole / avgSec1High, 0.25, 0.45);
    }
}
