using System.Diagnostics;
using WwvDecoder.Decoder;
using WwvDecoder.Dsp;

namespace WwvDecoder.Tests;

public class FrameDecoderTests
{
    private static readonly double TickScale = Stopwatch.Frequency;

    [Fact]
    public void FirstGapFilledFrame_DoesNotImmediatelyEstablishTimeAnchor()
    {
        long now = 0;
        var frames = new List<TimeFrame>();
        var logs = new List<string>();
        var decoder = new FrameDecoder(_ => { }, frames.Add, logs.Add, getTimestamp: () => now);

        EmitFrame(decoder, ref now, new DateTime(2026, 5, 4, 17, 50, 0, DateTimeKind.Utc),
            skipPositions: [12, 13]);

        Assert.Single(frames);
        Assert.True(frames[0].SlowFieldsConfident);
        Assert.False(frames[0].HoursMinutesConfident);
        Assert.False(frames[0].MarkovPassed);
        Assert.Equal(11, frames[0].DirectTimeBits);
        Assert.Contains(logs, l => l.Contains("Initial anchor pending"));
    }

    [Fact]
    public void CompatibleSecondGapFilledFrame_PromotesInitialAnchor()
    {
        long now = 0;
        var frames = new List<TimeFrame>();
        var logs = new List<string>();
        var decoder = new FrameDecoder(_ => { }, frames.Add, logs.Add, getTimestamp: () => now);

        EmitFrame(decoder, ref now, new DateTime(2026, 5, 4, 18, 12, 0, DateTimeKind.Utc),
            skipPositions: [12, 13]);
        EmitFrame(decoder, ref now, new DateTime(2026, 5, 4, 18, 13, 0, DateTimeKind.Utc),
            skipPositions: [12, 13]);

        Assert.True(frames.Count >= 2);
        Assert.False(frames[0].HoursMinutesConfident);
        Assert.True(frames[^1].HoursMinutesConfident, string.Join(Environment.NewLine, logs));
        Assert.True(frames[^1].MarkovPassed);
        Assert.Contains(logs, l => l.Contains("Initial anchor promoted"));
    }

    [Fact]
    public void IncompatibleSecondGapFilledFrame_ReplacesPendingAnchor()
    {
        long now = 0;
        var frames = new List<TimeFrame>();
        var logs = new List<string>();
        var decoder = new FrameDecoder(_ => { }, frames.Add, logs.Add, getTimestamp: () => now);

        EmitFrame(decoder, ref now, new DateTime(2026, 5, 4, 17, 50, 0, DateTimeKind.Utc),
            skipPositions: [12, 13]);
        EmitFrame(decoder, ref now, new DateTime(2026, 5, 4, 19, 40, 0, DateTimeKind.Utc),
            skipPositions: [12, 13]);

        Assert.True(frames.Count >= 2);
        Assert.False(frames[^1].HoursMinutesConfident);
        Assert.False(frames[^1].MarkovPassed);
        Assert.Contains(logs, l => l.Contains("Initial anchor candidate replaced"));
    }

    [Fact]
    public void FirstLatePostMinuteTick_DoesNotGapFillEarlyBits()
    {
        long now = 0;
        var logs = new List<string>();
        var decoder = new FrameDecoder(_ => { }, _ => { }, logs.Add, getTimestamp: () => now);

        decoder.OnTick(new TickEvent(TickType.MinutePulse, 0.8));
        now = Seconds(1.23);
        decoder.OnPulse(Pulse(PulseType.Zero), peakEnvelope: 1, noiseFloor: 0.1, subcarrierLevel: 1);
        now = Seconds(2.23);
        decoder.OnPulse(Pulse(PulseType.Zero), peakEnvelope: 1, noiseFloor: 0.1, subcarrierLevel: 1);

        now = Seconds(4.02);
        decoder.OnTick(new TickEvent(TickType.SecondTick, 0.005));

        Assert.Contains(logs, l => l.Contains("Post-minute tick recovery miss"));
        Assert.DoesNotContain(logs, l => l.Contains("Tick gap fill: [03]"));
    }

    private static void EmitFrame(FrameDecoder decoder, ref long now, DateTime utc,
                                  int[]? skipPositions = null)
    {
        var skip = skipPositions?.ToHashSet() ?? [];
        var bits = BuildFrameBits(utc);

        now += Seconds(60);
        decoder.OnTick(new TickEvent(TickType.MinutePulse, 0.8));

        for (int pos = 1; pos < 60; pos++)
        {
            now += Seconds(pos == 1 ? 1.23 : 1.0);
            if (skip.Contains(pos)) continue;

            decoder.OnPulse(Pulse(bits[pos] switch
            {
                2 => PulseType.Marker,
                1 => PulseType.One,
                _ => PulseType.Zero
            }), peakEnvelope: 1, noiseFloor: 0.1, subcarrierLevel: 1);
        }
    }

    private static PulseEvent Pulse(PulseType type)
    {
        double width = type switch
        {
            PulseType.Marker => 0.80,
            PulseType.One => 0.50,
            _ => 0.20
        };
        return new PulseEvent(width, type, 1.0, width);
    }

    private static int[] BuildFrameBits(DateTime utc)
    {
        var bits = new int[60];
        foreach (int m in new[] { 9, 19, 29, 39, 49, 59 })
            bits[m] = 2;

        EncodeBcd(bits, [10, 11, 12, 13, 15, 16, 17], utc.Minute);
        EncodeBcd(bits, [20, 21, 22, 23, 25, 26], utc.Hour);
        EncodeBcd(bits, [30, 31, 32, 33, 35, 36, 37, 38, 40, 41], utc.DayOfYear);
        EncodeBcd(bits, [4, 5, 6, 7, 51, 52, 53, 54], utc.Year % 100);
        bits[50] = 1;
        return bits;
    }

    private static void EncodeBcd(int[] bits, int[] positions, int value)
    {
        int[] weights = [1, 2, 4, 8, 10, 20, 40, 80, 100, 200];
        int remaining = value;
        for (int i = positions.Length - 1; i >= 0; i--)
        {
            int w = weights[i];
            if (remaining >= w)
            {
                bits[positions[i]] = 1;
                remaining -= w;
            }
        }
    }

    private static long Seconds(double seconds) => (long)(seconds * TickScale);
}
