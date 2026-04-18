using System.Diagnostics;
using WwvDecoder.Audio;
using WwvDecoder.Dsp;

namespace WwvDecoder.Decoder;

/// <summary>
/// Wires together the DSP chain and the frame decoder.
/// Accepts raw audio float samples and drives everything downstream.
///
/// DSP pipeline (in order):
///   1. InputAGC          — normalize audio level (handles variable SDR gain)
///   2. HighpassFilter    — remove DC offset and sub-20 Hz rumble
///   3. NotchFilter 60 Hz — suppress US power-line fundamental
///   4. NotchFilter 120 Hz— suppress power-line second harmonic
///   5. SynchronousDetector — coherent I/Q demodulation at 100 Hz → envelope
///   6. PulseDetector     — classify pulse widths (Zero/One/Marker)
///   7. FrameDecoder      — assemble 60-bit BCD frames and decode UTC time
///
/// All processing runs synchronously on the audio callback thread. UI updates
/// are marshaled back to the UI thread by the FrameDecoder's callbacks.
/// </summary>
public class DecoderPipeline
{
    private readonly InputAgc _agc;
    private readonly HighpassFilter _highpass;
    private readonly NotchFilter _notch60;
    private readonly NotchFilter _notch120;
    private readonly AdaptiveLineEnhancer _ale;
    private readonly SynchronousDetector _syncDetector;
    private readonly CarrierPll _pll;
    private readonly PulseDetector _pulseDetector;
    private readonly TickDetector _tickDetector;
    private readonly FrameDecoder _frameDecoder;
    private readonly Action<string>? _onLog;

    // Periodic status log every ~5 s (100 blocks × 50 ms).
    private int _blockCount;

    public DecoderPipeline(Action<SignalStatus> onSignalUpdate, Action<TimeFrame> onFrameDecoded,
                           Action<string>? onLog = null, Action<FrameCell[]>? onFrameUpdate = null)
    {
        int sr = AudioInputDevice.SampleRate;

        // Slow-attack AGC (3 s attack, 5 s decay) normalises HF fading — which varies
        // over seconds to minutes — without pumping on individual pulse LOW periods.
        // At τ=3 s, a 200 ms Zero LOW causes only ~7% gain change; an 800 ms Marker LOW
        // causes ~23%. Both leave the PulseDetector's adaptive thresholds well within range.
        // Without this, disabled SDR-AGC or deep HF fades cause levelHigh to undertrack
        // the carrier by 2–3×, pushing exitThreshold down to the WWV LOW carrier level
        // and making every ionospheric flicker look like a pulse.
        _onLog        = onLog;
        _agc          = new InputAgc(sr, attackSeconds: 3.0, decaySeconds: 5.0);
        _highpass     = new HighpassFilter(sr, cutoffHz: 20.0);
        _notch60      = new NotchFilter(sr, notchHz: 60.0,  notchWidthHz: 2.0);
        _notch120     = new NotchFilter(sr, notchHz: 120.0, notchWidthHz: 2.0);
        // ALE: 5-sample delay (0.23 ms @ 22050 Hz) gives 0.99 correlation with the 100 Hz
        // subcarrier while decorrelating broadband noise. 128 taps for sharp line resolution;
        // μ=0.3 is conservative — stable across varying SDR audio levels after the AGC.
        _ale          = new AdaptiveLineEnhancer(delay: 5, taps: 128, mu: 0.3);
        // Wide lowpass (8 Hz) for frequency acquisition; PLL narrows to 2 Hz after lock.
        _syncDetector  = new SynchronousDetector(sr, subcarrierHz: 100.0, lowpassHz: 8.0);
        _pll           = new CarrierPll(_syncDetector, sr, onLog: onLog);
        _pulseDetector = new PulseDetector(sr, _syncDetector);
        _tickDetector  = new TickDetector(sr);
        _frameDecoder  = new FrameDecoder(onSignalUpdate, onFrameDecoded, onLog, onFrameUpdate);

        _pulseDetector.PulseDetected += pulse =>
            _frameDecoder.OnPulse(pulse, _pulseDetector.PeakEnvelope, _syncDetector.NoiseFloor,
                                  _pulseDetector.LevelHigh);

        _tickDetector.TickDetected += tick => _frameDecoder.OnTick(tick);
    }

    /// <summary>
    /// Process a block of raw audio samples through the full DSP pipeline.
    /// Runs synchronously on the audio callback thread — must complete in &lt; 50 ms.
    /// </summary>
    public void ProcessSamples(float[] samples)
    {
        // Verify this is called from the audio thread, not the UI thread.
        // Use null-conditional access — Application.Current can be null during shutdown.
        Debug.Assert(System.Windows.Application.Current?.Dispatcher.CheckAccess() != true,
            "ProcessSamples must run on the audio callback thread, not the UI thread");

        // 1. Normalize audio level against HF fading and variable SDR gain settings.
        var agcOut = _agc.ProcessBlock(samples);

        // 2. Remove DC offset and sub-20 Hz noise
        var hpOut = _highpass.ProcessBlock(agcOut);

        // 3 & 4. Suppress 60 Hz and 120 Hz power-line interference
        var n60Out  = _notch60.ProcessBlock(hpOut);
        var n120Out = _notch120.ProcessBlock(n60Out);

        // 5. Adaptive Line Enhancer: reinforces the periodic 100 Hz subcarrier while
        // suppressing broadband noise (voice, static, wideband SDR hash). The NLMS filter
        // adapts a delayed copy of the input to predict the current sample; for a periodic
        // signal the prediction is accurate, for noise it is not — so the output converges
        // to the dominant periodic component. Expected SNR improvement: 5–15 dB.
        var aleOut = _ale.ProcessBlock(n120Out);

        // 6. Coherent I/Q demodulation → 100 Hz envelope.
        var envelope = _syncDetector.ProcessBlock(aleOut);

        // 7. PLL carrier tracking: adjusts the sync detector's reference oscillator to
        // match the actual carrier frequency, then narrows the lowpass from 8 → 5 Hz
        // once locked (~1–2 seconds). The narrower bandwidth gives ~12 dB SNR improvement.
        _pll.Update(aleOut.Length);

        // 8. Classify pulses using matched filter (counts genuinely-LOW samples)
        _pulseDetector.ProcessBlock(envelope);

        // 9. Detect 1000 Hz second ticks and minute pulse on the separate 1000 Hz channel.
        // Uses pre-ALE audio (n120Out): the ALE is tuned for the 100 Hz subcarrier and
        // could distort the 1000 Hz tick envelope. The notch filters are sufficient here.
        _tickDetector.ProcessBlock(n120Out);

        // 10. Zero the signal meter if no pulse has arrived recently
        _frameDecoder.CheckSignalTimeout();

        // Periodic diagnostic: AGC gain + PLL state every ~5 s (100 × 50 ms blocks).
        if (++_blockCount % 100 == 0)
        {
            string pllState = _pll.IsLocked
                ? $"locked  offset={_pll.FrequencyErrorHz:+0.0;-0.0;+0.0} Hz  LP=2 Hz"
                : $"searching  offset={_pll.FrequencyErrorHz:+0.0;-0.0;+0.0} Hz  LP=8 Hz";
            double gainDb = 20 * Math.Log10(Math.Max(_agc.CurrentGain, 1e-9));
            _onLog?.Invoke($"[Status] AGC gain={_agc.CurrentGain:F0}x ({gainDb:F1} dB)  " +
                           $"level={_agc.CurrentLevel:F4}  PLL={pllState}");
        }
    }

    /// <summary>Pre-fills year and day-of-year bits from an operator-supplied UTC date.</summary>
    public void SetKnownDate(DateTime utcDate) => _frameDecoder.SetKnownDate(utcDate);

    /// <summary>Clears the operator-supplied date hint from the persistent bit store.</summary>
    public void ClearKnownDate() => _frameDecoder.ClearKnownDate();

    public void Reset()
    {
        _agc.Reset();
        _highpass.Reset();
        _notch60.Reset();
        _notch120.Reset();
        _ale.Reset();
        _syncDetector.Reset();
        _pll.Reset();
        _pulseDetector.Reset();
        _tickDetector.Reset();
        _frameDecoder.Reset();
    }
}
