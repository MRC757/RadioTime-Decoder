using WwvDecoder.Dsp;

namespace WwvDecoder.Tests;

/// <summary>Diagnostic tests to isolate pipeline stages during simulation.</summary>
public class DiagnosticTests
{
    private const int Sr = 22050;

    [Fact]
    public void FullPipeline_DetectsPulsesWithinThirtySeconds()
    {
        // Verify the full DSP chain (AGC → ALE → SyncDet → PulseDetector) detects
        // at least 10 non-Tick pulses within 30 seconds of clean simulation signal.
        // This isolates whether the AGC+ALE initial transient prevents pulse detection.
        var gen = new WwvSignalGenerator(Sr, seed: 0);
        gen.SetTime(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        gen.NoiseSigma = 0;

        var agc  = new InputAgc(Sr, attackSeconds: 3.0, decaySeconds: 5.0);
        var ale  = new AdaptiveLineEnhancer(delay: 5, taps: 128, mu: 0.3);
        var det  = new SynchronousDetector(Sr, subcarrierHz: 100.0, lowpassHz: 8.0);
        var pd   = new PulseDetector(Sr, det);

        int pulseCount = 0;
        pd.PulseDetected += p => { if (p.Type != PulseType.Tick) pulseCount++; };

        int blockSize = Sr / 20; // 50 ms
        int maxBlocks = 30 * 20; // 30 seconds

        for (int b = 0; b < maxBlocks; b++)
        {
            var raw  = gen.GenerateBlock(blockSize);
            var agcO = agc.ProcessBlock(raw);
            var aleO = ale.ProcessBlock(agcO);
            var env  = det.ProcessBlock(aleO);
            pd.ProcessBlock(env);
        }

        Assert.True(pulseCount >= 10,
            $"Expected ≥10 non-Tick pulses within 30s; got {pulseCount}. " +
            $"LevelHigh={pd.LevelHigh:F4}");
    }

    [Fact]
    public void SynchronousDetector_DetectsAmplitudeModulation()
    {
        // Verify the sync detector produces a high envelope during HIGH and
        // a lower envelope during LOW, given a 100 Hz carrier.
        var det = new SynchronousDetector(Sr, subcarrierHz: 100.0, lowpassHz: 8.0);
        double omega = 2.0 * Math.PI * 100.0 / Sr;
        double phase = 0;

        // Generate 1 second HIGH followed by 0.2s LOW at 31% amplitude
        int highSamples = Sr;
        int lowSamples  = (int)(0.2 * Sr);
        var allSamples  = new float[highSamples + lowSamples];

        for (int i = 0; i < highSamples; i++)
        {
            allSamples[i] = (float)Math.Sin(phase);
            phase += omega;
            if (phase >= Math.PI * 2) phase -= Math.PI * 2;
        }
        for (int i = 0; i < lowSamples; i++)
        {
            allSamples[highSamples + i] = (float)(0.316 * Math.Sin(phase));
            phase += omega;
            if (phase >= Math.PI * 2) phase -= Math.PI * 2;
        }

        var env = det.ProcessBlock(allSamples);

        // Average envelope during last 200 ms of HIGH (after convergence)
        double avgHigh = env.Skip(highSamples - (int)(0.2 * Sr)).Take((int)(0.2 * Sr))
                            .Select(e => (double)e).Average();
        // Average envelope during LOW (skip first 50ms for transition)
        double avgLow = env.Skip(highSamples + (int)(0.05 * Sr))
                           .Take(lowSamples - (int)(0.05 * Sr))
                           .Select(e => (double)e).Average();

        Assert.True(avgHigh > 0.5, $"Expected HIGH envelope > 0.5, got {avgHigh:F3}");
        Assert.True(avgLow < avgHigh, $"Expected LOW({avgLow:F3}) < HIGH({avgHigh:F3})");
        Assert.True(avgLow / avgHigh < 0.80, $"Expected LOW/HIGH ratio < 0.80, got {avgLow / avgHigh:F3}");
    }

    [Fact]
    public void PulseDetector_DetectsMarkerAfterWarmup()
    {
        // After 1 second of HIGH (to build levelHigh), a Marker (800ms LOW) should be detected.
        var det = new SynchronousDetector(Sr, subcarrierHz: 100.0, lowpassHz: 8.0);
        var pd  = new PulseDetector(Sr, det);

        PulseEvent? captured = null;
        pd.PulseDetected += p => captured = p;

        double omega = 2.0 * Math.PI * 100.0 / Sr;
        double phase = 0;

        // 1 s HIGH warm-up
        var high = new float[Sr];
        for (int i = 0; i < Sr; i++) { high[i] = (float)Math.Sin(phase); phase += omega; if (phase >= Math.PI*2) phase -= Math.PI*2; }
        pd.ProcessBlock(det.ProcessBlock(high));

        // 800 ms LOW (Marker)
        var low = new float[(int)(0.8 * Sr)];
        for (int i = 0; i < low.Length; i++) { low[i] = (float)(0.316 * Math.Sin(phase)); phase += omega; if (phase >= Math.PI*2) phase -= Math.PI*2; }
        pd.ProcessBlock(det.ProcessBlock(low));

        // 500 ms HIGH (to let the pulse fire on envelope recovery)
        var high2 = new float[(int)(0.5 * Sr)];
        for (int i = 0; i < high2.Length; i++) { high2[i] = (float)Math.Sin(phase); phase += omega; if (phase >= Math.PI*2) phase -= Math.PI*2; }
        pd.ProcessBlock(det.ProcessBlock(high2));

        Assert.NotNull(captured);
        Assert.Equal(PulseType.Marker, captured.Type);
    }
}
