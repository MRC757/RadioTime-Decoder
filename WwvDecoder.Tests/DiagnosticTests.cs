using WwvDecoder.Dsp;

namespace WwvDecoder.Tests;

/// <summary>Diagnostic tests to isolate pipeline stages during simulation.</summary>
public class DiagnosticTests
{
    private const int Sr = 22050;

    [Fact]
    public void TickDetector_RestoresPreBurstReference_AfterMinutePulse()
    {
        var tick = new TickDetector(Sr);
        var events = new List<TickEvent>();
        var logs = new List<string>();
        tick.TickDetected += events.Add;
        tick.OnLog = logs.Add;

        double phase = 0;
        float[] Tone(double seconds, double amp)
        {
            int n = (int)Math.Round(seconds * Sr);
            var buf = new float[n];
            double omega = 2.0 * Math.PI * 1000.0 / Sr;
            for (int i = 0; i < n; i++)
            {
                buf[i] = (float)(amp * Math.Sin(phase));
                phase += omega;
                if (phase >= Math.PI * 2) phase -= Math.PI * 2;
            }
            return buf;
        }
        static float[] Silence(double seconds) => new float[(int)Math.Round(seconds * Sr)];

        // Establish a clean second-tick reference at modest amplitude.
        tick.ProcessBlock(Tone(0.005, 0.20));
        tick.ProcessBlock(Silence(1.0));
        Assert.Contains(events, e => e.Type == TickType.SecondTick);

        // A much larger 800 ms P0 burst would normally inflate _levelHigh enough to
        // suppress the first modest 5 ms tick. The detector should restore the saved
        // pre-burst reference for the first 500 ms after P0.
        tick.ProcessBlock(Tone(0.800, 1.00));
        tick.ProcessBlock(Silence(0.050));
        Assert.Contains(events, e => e.Type == TickType.MinutePulse);
        Assert.True(tick.IsPostMinuteRecoveryActive);
        Assert.Contains(logs, l => l.Contains("Post-minute reference restored"));

        int secondTicksBefore = events.Count(e => e.Type == TickType.SecondTick);
        tick.ProcessBlock(Silence(0.150));
        tick.ProcessBlock(Tone(0.005, 0.20));
        tick.ProcessBlock(Silence(0.200));

        Assert.True(events.Count(e => e.Type == TickType.SecondTick) > secondTicksBefore,
            string.Join(Environment.NewLine, logs) + Environment.NewLine +
            string.Join(Environment.NewLine, events.Select(e => $"{e.Type} {e.WidthSeconds * 1000:F1}ms")));
    }

    [Fact]
    public void FullPipeline_DetectsPulsesWithinThirtySeconds()
    {
        // Verify the full DSP chain (AGC → SyncDet → TickDetector → PulseDetector) detects
        // at least 10 non-Tick pulses within 30 seconds of clean simulation signal.
        // PulseDetector requires NotifyTick() per second; TickDetector provides this.
        var gen = new WwvSignalGenerator(Sr, seed: 0);
        gen.SetTime(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        gen.NoiseSigma = 0;

        var agc  = new InputAgc(Sr, attackSeconds: 3.0, decaySeconds: 5.0);
        var det  = new SynchronousDetector(Sr, subcarrierHz: 100.0, lowpassHz: 8.0);
        var tick = new TickDetector(Sr);
        var pd   = new PulseDetector(Sr, det);

        int blockSize = Sr / 20; // 50 ms

        // Run a full 60-second frame through the pipeline before wiring event handlers.
        // This lets the AGC settle (τ=3s), TickDetector noise floor decay to its true
        // value, and PulseDetector._levelHigh stabilize at the normalized signal level.
        // Without warmup the initial 500× AGC gain inflates internal level trackers and
        // blocks pulse detection for the first ~20 seconds.
        for (int b = 0; b < 60 * 20; b++)
        {
            var raw  = gen.GenerateBlock(blockSize);
            var agcO = agc.ProcessBlock(raw);
            tick.ProcessBlock(agcO);
            var env  = det.ProcessBlock(agcO);
            pd.ProcessBlock(env);
        }

        // Wire TickDetector → PulseDetector.NotifyTick() so rising-edge windows open
        int tickCount = 0;
        var tickLog = new List<string>();
        tick.TickDetected += t => {
            tickCount++;
            tickLog.Add($"t={t.Type} w={t.WidthSeconds*1000:F0}ms lev={tick.LevelHigh:F3}");
            pd.NotifyTick(tick.LevelHigh);
        };

        int pulseCount = 0;
        var pulseLog = new List<string>();
        pd.PulseDetected += p => {
            if (p.Type != PulseType.Tick) pulseCount++;
            pulseLog.Add($"{p.Type} d={p.EffectiveDuration:F3}s conf={p.Confidence:F2}");
        };
        pd.OnLog = msg => pulseLog.Add("[PD] " + msg);

        int maxBlocks = 30 * 20; // 30 seconds

        for (int b = 0; b < maxBlocks; b++)
        {
            var raw  = gen.GenerateBlock(blockSize);
            var agcO = agc.ProcessBlock(raw);
            // TickDetector runs on filtered audio before envelope detection
            tick.ProcessBlock(agcO);
            var env  = det.ProcessBlock(agcO);
            pd.ProcessBlock(env);
        }

        string diagLog = string.Join("\n", pulseLog.Take(40));
        string tickDiag = string.Join("\n", tickLog.Take(35));
        Assert.True(pulseCount >= 10,
            $"Expected ≥10 non-Tick pulses within 30s; got {pulseCount}. " +
            $"Ticks fired: {tickCount}. LevelHigh={pd.LevelHigh:F4}\n" +
            $"--- Ticks ---\n{tickDiag}\n--- Pulses ---\n{diagLog}");
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
        // WWV positive-pulse model: each second the carrier is HIGH for the bit duration,
        // then LOW for the remainder.  PulseDetector requires NotifyTick() to arm the
        // 200 ms rising-edge detection window before the HIGH period begins.
        //
        // Test sequence:
        //   1. 1 s HIGH warm-up  → builds levelHigh (no tick, no pulse collected)
        //   2. NotifyTick()      → arms the 200 ms rising-edge window
        //   3. 800 ms HIGH       → Marker HIGH period; pulse buffer fills
        //   4. 200 ms LOW        → dropout tolerance exceeded → pulse fires
        var det = new SynchronousDetector(Sr, subcarrierHz: 100.0, lowpassHz: 8.0);
        var pd  = new PulseDetector(Sr, det);

        PulseEvent? captured = null;
        pd.PulseDetected += p => captured = p;

        double omega = 2.0 * Math.PI * 100.0 / Sr;
        double phase = 0;

        float[] MakeSamples(int n, double amp)
        {
            var buf = new float[n];
            for (int i = 0; i < n; i++)
            {
                buf[i] = (float)(amp * Math.Sin(phase));
                phase  += omega;
                if (phase >= Math.PI * 2) phase -= Math.PI * 2;
            }
            return buf;
        }

        // Step 1: 1 s HIGH warm-up — builds levelHigh so thresholds are meaningful
        pd.ProcessBlock(det.ProcessBlock(MakeSamples(Sr, amp: 1.0)));

        // Step 2: arm the rising-edge detection window (simulates the 1 kHz second tick)
        pd.NotifyTick(tickAmplitude: 1.0);

        // Step 3: 800 ms HIGH — Marker HIGH duration; samples accumulate in pulse buffer
        pd.ProcessBlock(det.ProcessBlock(MakeSamples((int)(0.8 * Sr), amp: 1.0)));

        // Step 4: 200 ms LOW — amplitude drops to −10 dBc (31.6%); dropout tolerance
        // (75 ms) is exceeded → pulse fires as Marker
        pd.ProcessBlock(det.ProcessBlock(MakeSamples((int)(0.2 * Sr), amp: 0.316)));

        Assert.NotNull(captured);
        Assert.Equal(PulseType.Marker, captured.Type);
    }
}
