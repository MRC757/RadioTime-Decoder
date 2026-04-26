using System.Diagnostics;
using WwvDecoder.Audio;
using WwvDecoder.Dsp;
using WwvDecoder.Logging;

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
///   5. SynchronousDetector - coherent I/Q demodulation at 100 Hz -> envelope
///   7. PulseDetector       - classify pulse widths (Zero/One/Marker)
///   8. FrameDecoder        - assemble 60-bit BCD frames and decode UTC time
///
/// No carrier PLL: the 100 Hz subcarrier is derived from an atomic clock and its
/// frequency is exact in the audio output of an AM receiver. Frequency correction
/// is not needed. The synchronous detector runs at 2 Hz lowpass from the start;
/// the adaptive LP widens to 8 Hz only when HF amplitude fading is detected.
///
/// All processing runs synchronously on the audio callback thread. UI updates
/// are marshaled back to the UI thread by the FrameDecoder's callbacks.
/// </summary>
public class DecoderPipeline
{
    private const double NarrowLowpassHz = 2.0;
    private const double WideLowpassHz   = 8.0;

    private readonly InputAgc _agc;
    private readonly HighpassFilter _highpass;
    private readonly NotchFilter _notch60;
    private readonly NotchFilter _notch120;
    private readonly SynchronousDetector _syncDetector;
    private readonly PulseDetector _pulseDetector;
    private readonly TickDetector _tickDetector;
    private readonly SyncQualityScorer _syncScorer;
    private readonly FrameDecoder _frameDecoder;
    private readonly Action<string>? _onLog;
    private readonly Action<SignalStatus> _onSignalUpdate;
    private readonly Func<DecoderRuntimeSettingsSnapshot> _getSettings;
    private readonly DiagnosticLogger? _diag;

    // Periodic status log every ~5 s (100 blocks × 50 ms).
    private int _blockCount;
    // Tracks whether the LP filter has been widened for fading conditions.
    private bool _lpWidened;

    // ── 1 kHz tick cadence tracking ──────────────────────────────────────────
    // Derives TickLockState from the regularity of SecondTick/MinutePulse events:
    //   Locked   : 3+ consecutive ticks spaced 0.5–1.5 s apart
    //   Searching: fewer than 3 good ticks, or recently lost regularity
    //   NoSignal : no tick seen for more than 7 s
    private long _lastTickTimestamp;
    private int _consecutiveGoodTicks;
    private TickLockState _tickLockState = TickLockState.NoSignal;

    // ── Diagnostic accumulation buffers ──────────────────────────────────────
    // Intermediate signal stages are copied here on each block. Every
    // DiagIntervalBlocks (~1 s) the buffers are analysed and a row written to
    // the CSV. Three stages for notch-rejection diagnostics:
    //   _diagHp    — after highpass, before any notch  (Goertzel @ 60 & 120 Hz)
    //   _diagN60   — after 60 Hz notch, before 120 Hz  (Goertzel @ 60 Hz → rejection)
    //   _diagN120  — after both notches                (Goertzel @ 120 Hz → rejection)
    private const int DiagIntervalBlocks = 20; // log every ~1 second (20 × 50 ms)
    private readonly float[] _diagHp;
    private readonly float[] _diagN60;
    private readonly float[] _diagN120;
    private int _diagCount;

    public DecoderPipeline(Action<SignalStatus> onSignalUpdate, Action<TimeFrame> onFrameDecoded,
                           Action<string>? onLog = null, Action<FrameCell[]>? onFrameUpdate = null,
                           Func<long>? getTimestamp = null,
                           Func<DecoderRuntimeSettingsSnapshot>? getSettings = null,
                           DiagnosticLogger? diagnosticLogger = null)
    {
        int sr = AudioInputDevice.SampleRate;

        _onLog          = onLog;
        _onSignalUpdate = onSignalUpdate;
        _getSettings    = getSettings ?? (() => new DecoderRuntimeSettingsSnapshot(
            EnableAgc: true,
            EnableAdaptiveLowpass: true,
            InputTrimDb: 0.0));
        _diag = diagnosticLogger;

        // Pre-allocate diagnostic buffers sized for 1 second of audio.
        int diagBufSize = sr;
        _diagHp   = new float[diagBufSize];
        _diagN60  = new float[diagBufSize];
        _diagN120 = new float[diagBufSize];

        // Slow-attack AGC (3 s attack, 5 s decay) normalises HF fading without
        // pumping on individual pulse LOW periods.
        _agc          = new InputAgc(sr, attackSeconds: 3.0, decaySeconds: 5.0);
        _highpass     = new HighpassFilter(sr, cutoffHz: 20.0);
        _notch60      = new NotchFilter(sr, notchHz: 60.0,  notchWidthHz: 2.0);
        _notch120     = new NotchFilter(sr, notchHz: 120.0, notchWidthHz: 2.0);
        // Start at 2 Hz: the 100 Hz subcarrier is atomic-clock-derived and its frequency
        // is exact in AM-demodulated audio — no frequency correction needed.
        // Adaptive LP widens to 8 Hz only when HF amplitude fading is detected.
        _syncDetector = new SynchronousDetector(sr, subcarrierHz: 100.0, lowpassHz: NarrowLowpassHz);
        _pulseDetector = new PulseDetector(sr, _syncDetector) { OnLog = onLog };
        _tickDetector  = new TickDetector(sr);
        _syncScorer    = new SyncQualityScorer(sr);
        _frameDecoder  = new FrameDecoder(ForwardSignalUpdate,
            frame =>
            {
                _diag?.WriteFrame(BuildFrameRecord(frame));
                onFrameDecoded(frame);
            },
            onLog, onFrameUpdate, getTimestamp);

        _pulseDetector.PulseDetected += pulse =>
        {
            _syncScorer.ObservePulse(Stopwatch.GetTimestamp());
            _frameDecoder.OnPulse(pulse, _pulseDetector.PeakEnvelope, _syncDetector.NoiseFloor,
                                  _pulseDetector.LevelHigh);
            _diag?.WritePulse(BuildPulseRecord(pulse));
        };

        _tickDetector.TickDetected += tick =>
        {
            if (tick.Type == TickType.SecondTick)
                _syncScorer.ObserveSecondTick(Stopwatch.GetTimestamp());

            // Update tick cadence state and fire heartbeat for the UI indicator.
            if (tick.Type == TickType.SecondTick || tick.Type == TickType.MinutePulse)
            {
                long now = Stopwatch.GetTimestamp();
                if (_lastTickTimestamp != 0)
                {
                    double dt = (double)(now - _lastTickTimestamp) / Stopwatch.Frequency;
                    if (dt >= 0.5 && dt <= 1.5)
                        _consecutiveGoodTicks = Math.Min(_consecutiveGoodTicks + 1, 10);
                    else
                        _consecutiveGoodTicks = 0;
                }
                _lastTickTimestamp = now;
                _tickLockState = _consecutiveGoodTicks >= 3 ? TickLockState.Locked : TickLockState.Searching;
                TickHeartbeat?.Invoke(_tickLockState);
            }

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
        var n60Out  = _notch60.ProcessBlock(hpOut);
        var n120Out = _notch120.ProcessBlock(n60Out);

        // Carrier quality metric on the post-notch signal.
        _syncScorer.ProcessBlock(n120Out);
        _syncScorer.UpdateCarrierCenter(100.0);

        // 5. Coherent I/Q demodulation -> 100 Hz envelope.
        var envelope = _syncDetector.ProcessBlock(n120Out);

        // 6. Classify pulses using matched filter.
        _pulseDetector.ProcessBlock(envelope);

        // Feed the current envelope into the three-point discriminator so it can
        // sample at ~350 ms and ~650 ms after each 1000 Hz second tick, providing
        // an independent bit classification that does not rely on threshold crossing.
        _frameDecoder.FeedEnvelope(
            _pulseDetector.CurrentEnvelope,
            _pulseDetector.LevelHigh,
            Stopwatch.GetTimestamp());

        // Adaptive LP: start narrow (2 Hz) for best SNR. Widen to 8 Hz only when
        // rapid HF amplitude fading is detected so the envelope can follow the
        // instantaneous carrier level. Reverts to 2 Hz once fading subsides.
        bool shouldWiden = settings.EnableAdaptiveLowpass && _pulseDetector.IsAmplitudeUnstable;
        if (shouldWiden != _lpWidened)
        {
            _lpWidened = shouldWiden;
            _syncDetector.LowpassHz = shouldWiden ? WideLowpassHz : NarrowLowpassHz;
            _onLog?.Invoke($"[Adaptive LP] {(shouldWiden ? "Fading — LP→8 Hz" : "Stable — LP→2 Hz")}");
        }

        // 9. Detect 1000 Hz second ticks and minute pulse on the separate channel.
        _tickDetector.ProcessBlock(n120Out);

        // 10. Zero the signal meter if no pulse has arrived recently.
        _frameDecoder.CheckSignalTimeout();

        // Decay tick lock state to NoSignal if no tick has arrived for 7 seconds.
        if (_lastTickTimestamp != 0 &&
            (double)(Stopwatch.GetTimestamp() - _lastTickTimestamp) / Stopwatch.Frequency > 7.0)
        {
            _tickLockState = TickLockState.NoSignal;
            _consecutiveGoodTicks = 0;
            _lastTickTimestamp = 0;
        }

        // Accumulate intermediate stage buffers for diagnostic analysis.
        if (_diag != null)
            AccumulateDiagBuffers(agcOut, hpOut, n60Out, n120Out);

        ++_blockCount;

        if (_blockCount % 100 == 0)
        {
            double gainDb = settings.EnableAgc
                ? 20 * Math.Log10(Math.Max(_agc.CurrentGain, 1e-9))
                : settings.InputTrimDb;
            string agcState = settings.EnableAgc
                ? $"AGC gain={_agc.CurrentGain:F2}x ({gainDb:F1} dB)"
                : $"AGC bypassed  trim={settings.InputTrimDb:+0.0;-0.0;+0.0} dB";
            string lpState = _lpWidened ? "LP=8 Hz (fading)" : "LP=2 Hz";
            _onLog?.Invoke($"[Status] {agcState}  sync={_syncScorer.SyncScorePercent:F0}% @{_syncScorer.BestFrequencyHz:F1} Hz  " +
                           $"level={_agc.CurrentLevel:F4}  {lpState}");

            // Flush CSV writers every 5 seconds to bound data loss on crash/exit.
            _diag?.Flush();
        }

        if (_diag != null && _blockCount % DiagIntervalBlocks == 0)
            FlushDiagBlock(settings);
    }

    /// <summary>
    /// Fires on the audio callback thread each time the 1000 Hz minute pulse ends.
    /// The argument is the measured pulse width in seconds (~0.8 s for a genuine WWV pulse).
    /// </summary>
    public event Action<double>? MinutePulseDetected;

    /// <summary>
    /// Fires on the audio callback thread each time a 1 kHz second tick is confirmed.
    /// Argument is the current tick lock state at the moment the tick arrived.
    /// Used by the UI to flash the tick indicator and update its lock state.
    /// </summary>
    public event Action<TickLockState>? TickHeartbeat;

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
        _syncDetector.Reset();
        _pulseDetector.Reset();
        _tickDetector.Reset();
        _syncScorer.Reset();
        _frameDecoder.Reset();
        _lpWidened = false;
        _diagCount = 0;
        _lastTickTimestamp = 0;
        _consecutiveGoodTicks = 0;
        _tickLockState = TickLockState.NoSignal;
    }

    // ── Diagnostic helpers ────────────────────────────────────────────────────

    private void AccumulateDiagBuffers(
        float[] agcOut, float[] hpOut, float[] n60Out, float[] n120Out)
    {
        int space = _diagHp.Length - _diagCount;
        int copy  = Math.Min(space, hpOut.Length);
        if (copy <= 0) return;

        Array.Copy(hpOut,  0, _diagHp,   _diagCount, copy);
        Array.Copy(n60Out, 0, _diagN60,  _diagCount, copy);
        Array.Copy(n120Out,0, _diagN120, _diagCount, copy);
        _diagCount += copy;
    }

    private void FlushDiagBlock(DecoderRuntimeSettingsSnapshot settings)
    {
        int n = _diagCount;
        if (n == 0) return;

        int sr = AudioInputDevice.SampleRate;

        double inputRmsDb    = RmsDb(_diagHp,   n);
        double notch60InDb   = GoertzelDb(_diagHp,  n, 60.0,  sr);
        double notch60OutDb  = GoertzelDb(_diagN60, n, 60.0,  sr);
        double notch120InDb  = GoertzelDb(_diagN60,  n, 120.0, sr);
        double notch120OutDb = GoertzelDb(_diagN120, n, 120.0, sr);
        double aleInDb       = RmsDb(_diagN120, n);
        double aleOutDb      = aleInDb;

        double gainDb = settings.EnableAgc
            ? 20 * Math.Log10(Math.Max(_agc.CurrentGain, 1e-9))
            : settings.InputTrimDb;
        double envelope   = _pulseDetector.CurrentEnvelope;
        double noiseFloor = _syncDetector.NoiseFloor;
        double snrDb      = 20 * Math.Log10(Math.Max(envelope / Math.Max(noiseFloor, 1e-9), 1e-9));

        _diag!.WriteBlock(new DiagBlockMetrics(
            TimestampUtc:         DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            ElapsedSeconds:       _diag.ElapsedSeconds,
            AgcGainDb:            gainDb,
            AgcLevel:             _agc.CurrentLevel,
            InputRmsDb:           inputRmsDb,
            Notch60InDb:          notch60InDb,
            Notch60OutDb:         notch60OutDb,
            Notch60RejDb:         notch60InDb  - notch60OutDb,
            Notch120InDb:         notch120InDb,
            Notch120OutDb:        notch120OutDb,
            Notch120RejDb:        notch120InDb - notch120OutDb,
            AleInRmsDb:           aleInDb,
            AleOutRmsDb:          aleOutDb,
            AleImprovementDb:     aleInDb - aleOutDb,
            AleWeightNorm:        0.0,
            Envelope:             envelope,
            NoiseFloor:           noiseFloor,
            SnrDb:                snrDb,
            LpHz:                 _syncDetector.LowpassHz,
            LevelHigh:            _pulseDetector.LevelHigh,
            PeakEnvelope:         _pulseDetector.PeakEnvelope,
            IsFading:             _pulseDetector.IsFading,
            IsAmplitudeUnstable:  _pulseDetector.IsAmplitudeUnstable,
            AmplitudeVariability: _pulseDetector.AmplitudeVariability,
            SyncScorePct:         _syncScorer.SyncScorePercent,
            CarrierScore:         _syncScorer.CarrierScore,
            CadenceScore:         _syncScorer.CadenceScore,
            BestCarrierHz:        _syncScorer.BestFrequencyHz));

        _diagCount = 0;
    }

    private DiagPulseRecord BuildPulseRecord(PulseEvent pulse)
    {
        double noiseFloor = _syncDetector.NoiseFloor;
        double levelHigh  = _pulseDetector.LevelHigh;
        double snrDb = 20 * Math.Log10(Math.Max(levelHigh / Math.Max(noiseFloor, 1e-9), 1e-9));

        return new DiagPulseRecord(
            TimestampUtc:        DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            ElapsedSeconds:      _diag!.ElapsedSeconds,
            Type:                pulse.Type.ToString(),
            DurationMs:          pulse.WidthSeconds * 1000.0,
            EffectiveDurationMs: pulse.EffectiveDuration * 1000.0,
            Confidence:          pulse.Confidence,
            MatchedType:         pulse.MatchedType?.ToString(),
            DurationType:        pulse.DurationType.ToString(),
            ClassifiersAgree:    !pulse.MatchedType.HasValue || pulse.MatchedType == pulse.DurationType,
            LevelHigh:           levelHigh,
            NoiseFloor:          noiseFloor,
            SnrDb:               snrDb,
            IsFading:            _pulseDetector.IsFading);
    }

    private DiagFrameRecord BuildFrameRecord(TimeFrame frame)
    {
        return new DiagFrameRecord(
            TimestampUtc:     DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            ElapsedSeconds:   _diag!.ElapsedSeconds,
            DecodedUtc:       frame.UtcTime.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            DayOfYear:        frame.DayOfYear,
            Dut1Seconds:      frame.Dut1Seconds,
            DstActive:        frame.DstActive,
            LeapPending:      frame.LeapSecondPending,
            ConfidenceFrames: frame.ConfidenceFrames,
            IsValid:          frame.IsValid);
    }

    /// <summary>Goertzel spectral power at <paramref name="targetHz"/> over the first
    /// <paramref name="count"/> samples, returned in dBFS.</summary>
    private static double GoertzelDb(float[] buf, int count, double targetHz, int sampleRate)
    {
        double omega = 2.0 * Math.PI * targetHz / sampleRate;
        double coeff = 2.0 * Math.Cos(omega);
        double q0 = 0.0, q1 = 0.0, q2 = 0.0;
        for (int i = 0; i < count; i++)
        {
            q0 = coeff * q1 - q2 + buf[i];
            q2 = q1;
            q1 = q0;
        }
        double power = (q1 * q1 + q2 * q2 - coeff * q1 * q2) / Math.Max(count, 1);
        return 10.0 * Math.Log10(Math.Max(power, 1e-12));
    }

    /// <summary>RMS power over the first <paramref name="count"/> samples, in dBFS.</summary>
    private static double RmsDb(float[] buf, int count)
    {
        double sum = 0;
        for (int i = 0; i < count; i++) sum += buf[i] * (double)buf[i];
        return 10.0 * Math.Log10(Math.Max(sum / Math.Max(count, 1), 1e-12));
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
        status.TickState = _tickLockState;
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
