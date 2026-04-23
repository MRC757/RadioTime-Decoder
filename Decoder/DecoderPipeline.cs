using System.Diagnostics;
using WwvDecoder.Audio;
using WwvDecoder.Dsp;

namespace WwvDecoder.Decoder;

/// <summary>
/// Wires together the DSP chain and the frame decoder.
/// Accepts raw audio float samples and drives everything downstream.
///
/// DSP pipeline (in order):
///   1. Input trim / AGC    - operator test controls
///   2. HighpassFilter      - remove DC offset and sub-20 Hz rumble
///   3. NotchFilter 60 Hz   - suppress US power-line fundamental
///   4. NotchFilter 120 Hz  - suppress power-line second harmonic
///   5. Optional ALE        - periodic-signal enhancement for the 100 Hz path
///   6. SynchronousDetector - coherent I/Q demodulation at 100 Hz -> envelope
///   7. PulseDetector       - classify pulse widths (Zero/One/Marker)
///   8. FrameDecoder        - assemble 60-bit BCD frames and decode UTC time
///
/// All processing runs synchronously on the audio callback thread. UI updates
/// are marshaled back to the UI thread by the FrameDecoder's callbacks.
/// </summary>
public class DecoderPipeline
{
    private const double NarrowLockedLowpassHz = 2.0;

    private readonly InputAgc _agc;
    private readonly HighpassFilter _highpass;
    private readonly NotchFilter _notch60;
    private readonly NotchFilter _notch120;
    private readonly AdaptiveLineEnhancer _ale;
    private readonly SynchronousDetector _syncDetector;
    private readonly CarrierPll _pll;
    private readonly PulseDetector _pulseDetector;
    private readonly TickDetector _tickDetector;
    private readonly SyncQualityScorer _syncScorer;
    private readonly FrameDecoder _frameDecoder;
    private readonly Action<string>? _onLog;
    private readonly Action<SignalStatus> _onSignalUpdate;
    private readonly Func<DecoderRuntimeSettingsSnapshot> _getSettings;

    // Periodic status log every ~5 s (100 blocks x 50 ms).
    private int _blockCount;
    // Tracks whether the LP filter has been widened for fading conditions.
    private bool _lpWidened;

    public DecoderPipeline(Action<SignalStatus> onSignalUpdate, Action<TimeFrame> onFrameDecoded,
                           Action<string>? onLog = null, Action<FrameCell[]>? onFrameUpdate = null,
                           Func<long>? getTimestamp = null,
                           Func<DecoderRuntimeSettingsSnapshot>? getSettings = null)
    {
        int sr = AudioInputDevice.SampleRate;

        _onLog          = onLog;
        _onSignalUpdate = onSignalUpdate;
        _getSettings    = getSettings ?? (() => new DecoderRuntimeSettingsSnapshot(
            EnableAgc: true,
            EnableAle: true,
            EnableAdaptiveLowpass: true,
            InputTrimDb: 0.0));

        // Slow-attack AGC (3 s attack, 5 s decay) normalises HF fading without
        // pumping on individual pulse LOW periods.
        _agc          = new InputAgc(sr, attackSeconds: 3.0, decaySeconds: 5.0);
        _highpass     = new HighpassFilter(sr, cutoffHz: 20.0);
        _notch60      = new NotchFilter(sr, notchHz: 60.0,  notchWidthHz: 2.0);
        _notch120     = new NotchFilter(sr, notchHz: 120.0, notchWidthHz: 2.0);
        _ale          = new AdaptiveLineEnhancer(delay: 5, taps: 128, mu: 0.3);
        _syncDetector = new SynchronousDetector(sr, subcarrierHz: 100.0, lowpassHz: 8.0);
        _pll          = new CarrierPll(_syncDetector, sr, onLog: onLog);
        _pulseDetector = new PulseDetector(sr, _syncDetector) { OnLog = onLog };
        _tickDetector  = new TickDetector(sr);
        _syncScorer    = new SyncQualityScorer(sr);
        _frameDecoder  = new FrameDecoder(ForwardSignalUpdate, onFrameDecoded, onLog, onFrameUpdate, getTimestamp);

        _pulseDetector.PulseDetected += pulse =>
        {
            _syncScorer.ObservePulse(Stopwatch.GetTimestamp());
            _frameDecoder.OnPulse(pulse, _pulseDetector.PeakEnvelope, _syncDetector.NoiseFloor,
                                  _pulseDetector.LevelHigh);
        };

        _tickDetector.TickDetected += tick =>
        {
            if (tick.Type == TickType.SecondTick)
                _syncScorer.ObserveSecondTick(Stopwatch.GetTimestamp());

            _frameDecoder.OnTick(tick);
            if (tick.Type == TickType.MinutePulse)
            {
                // Abort any in-progress 100 Hz pulse so the P0 Marker can be
                // detected fresh. Without this, the P59 pulse (or a stuck LOW
                // state) overflows at the minute boundary and consumes P0.
                _pulseDetector.AbortCurrentPulse();
                MinutePulseDetected?.Invoke(tick.WidthSeconds);
            }
        };
    }

    /// <summary>
    /// Process a block of raw audio samples through the full DSP pipeline.
    /// Runs synchronously on the audio callback thread - must complete in < 50 ms.
    /// </summary>
    public void ProcessSamples(float[] samples)
    {
        var settings = _getSettings();

        Debug.Assert(System.Windows.Application.Current?.Dispatcher.CheckAccess() != true,
            "ProcessSamples must run on the audio callback thread, not the UI thread");

        // 1. Operator input trim and optional AGC.
        var trimmed = ApplyInputTrim(samples, settings.InputTrimDb);
        var agcOut = settings.EnableAgc ? _agc.ProcessBlock(trimmed) : trimmed;

        // 2. Remove DC offset and sub-20 Hz noise.
        var hpOut = _highpass.ProcessBlock(agcOut);

        // 3 & 4. Suppress 60 Hz and 120 Hz power-line interference.
        var n60Out = _notch60.ProcessBlock(hpOut);
        var n120Out = _notch120.ProcessBlock(n60Out);

        // 5. Optional ALE for A/B testing and weak-signal comparison.
        var detectorInput = settings.EnableAle ? _ale.ProcessBlock(n120Out) : n120Out;

        // Carrier quality metric on the ALE-enhanced signal so the Goertzel sees
        // the best available SNR (ALE suppresses broadband noise by ~6-10 dB).
        _syncScorer.ProcessBlock(detectorInput);

        // 6. Coherent I/Q demodulation -> 100 Hz envelope.
        var envelope = _syncDetector.ProcessBlock(detectorInput);

        // 7. PLL carrier tracking.
        _pll.Update(detectorInput.Length);

        // 8. Classify pulses using matched filter.
        _pulseDetector.ProcessBlock(envelope);

        // Adaptive LP: when rapid HF fading is detected, widen to 8 Hz so the
        // envelope follows the instantaneous amplitude. This is user-toggleable
        // for testing, but the default stays on.
        if (_pll.IsLocked)
        {
            bool shouldWiden = settings.EnableAdaptiveLowpass && _pulseDetector.IsAmplitudeUnstable;
            if (shouldWiden != _lpWidened)
            {
                _lpWidened = shouldWiden;
                _syncDetector.LowpassHz = shouldWiden ? 8.0 : NarrowLockedLowpassHz;
                _onLog?.Invoke($"[Adaptive LP] {(shouldWiden ? "Fading - LP->8 Hz" : "Stable - LP->2 Hz")}");
            }
        }
        else
        {
            _lpWidened = false;
        }

        // 9. Detect 1000 Hz second ticks and minute pulse on the separate channel.
        _tickDetector.ProcessBlock(n120Out);

        // 10. Zero the signal meter if no pulse has arrived recently.
        _frameDecoder.CheckSignalTimeout();

        if (++_blockCount % 100 == 0)
        {
            string pllState = _pll.IsLocked
                ? $"locked  offset={_pll.FrequencyErrorHz:+0.0;-0.0;+0.0} Hz  LP=2 Hz"
                : $"searching  offset={_pll.FrequencyErrorHz:+0.0;-0.0;+0.0} Hz  LP=8 Hz";
            double gainDb = settings.EnableAgc
                ? 20 * Math.Log10(Math.Max(_agc.CurrentGain, 1e-9))
                : settings.InputTrimDb;
            string agcState = settings.EnableAgc
                ? $"AGC gain={_agc.CurrentGain:F2}x ({gainDb:F1} dB)"
                : $"AGC bypassed  trim={settings.InputTrimDb:+0.0;-0.0;+0.0} dB";
            _onLog?.Invoke($"[Status] {agcState}  sync={_syncScorer.SyncScorePercent:F0}% @{_syncScorer.BestFrequencyHz:F1} Hz  " +
                           $"level={_agc.CurrentLevel:F4}  PLL={pllState}");
        }
    }

    /// <summary>
    /// Fires on the audio callback thread each time the 1000 Hz minute pulse ends.
    /// The argument is the measured pulse width in seconds (~0.8 s for a genuine WWV pulse).
    /// </summary>
    public event Action<double>? MinutePulseDetected;

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
        _syncScorer.Reset();
        _frameDecoder.Reset();
        _lpWidened = false;
    }

    private void ForwardSignalUpdate(SignalStatus status)
    {
        var settings = _getSettings();
        status.SyncScorePercent = _syncScorer.SyncScorePercent;
        status.CoarseCarrierHz = _syncScorer.BestFrequencyHz;
        status.AgcEnabled = settings.EnableAgc;
        status.AgcGainDb = settings.EnableAgc
            ? 20 * Math.Log10(Math.Max(_agc.CurrentGain, 1e-9))
            : settings.InputTrimDb;
        _onSignalUpdate(status);
    }

    private static float[] ApplyInputTrim(float[] input, double trimDb)
    {
        if (Math.Abs(trimDb) < 0.05)
            return input;

        double gain = Math.Pow(10.0, trimDb / 20.0);
        var output = new float[input.Length];
        for (int i = 0; i < input.Length; i++)
            output[i] = (float)(input[i] * gain);
        return output;
    }
}
