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
///   6. TickDetector        - detect 1000 Hz second ticks and minute pulse
///   7. PulseDetector       - classify pulse widths (Zero/One/Marker)
///   8. FrameDecoder        - assemble 60-bit BCD frames and decode UTC time
///
/// Signal model (NIST IRIG-H positive-pulse):
///   The 100 Hz subcarrier rises to full power ~30 ms after each 1 kHz second tick
///   and holds HIGH for 170/470/770 ms (Zero/One/Marker). The pulse detector is
///   tick-anchored: NotifyTick() backstops the previous pulse and opens a 200 ms
///   rising-edge window for the next.
///
/// No carrier PLL: the 100 Hz subcarrier is derived from an atomic clock and its
/// frequency is exact in the audio output of an AM receiver. The synchronous
/// detector runs at 2 Hz lowpass from the start; the adaptive LP widens to 8 Hz
/// only when HF amplitude fading is detected.
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
    private readonly SecondTickPll _secondTickPll;
    private readonly SyncQualityScorer _syncScorer;
    private readonly FrameDecoder _frameDecoder;
    private readonly Action<string>? _onLog;
    private readonly Action<SignalStatus> _onSignalUpdate;
    private readonly Func<DecoderRuntimeSettingsSnapshot> _getSettings;
    private readonly Func<long> _getTimestamp;
    private readonly DiagnosticLogger? _diag;

    // Periodic status log every ~5 s (100 blocks × 50 ms).
    private int _blockCount;
    // Tracks whether the LP filter has been widened for fading conditions.
    private bool _lpWidened;

    // Pre-minute-pulse noise floor snapshot.
    // Updated every SecondTick so it holds the quiet-period floor from the second
    // just before P0. Restored after the 800 ms burst so ticks 1–3 are detectable
    // against the correct baseline rather than the burst-inflated floor.
    private double _preMinutePulseNoiseFloor = 0.001;

    // ── 1 kHz tick cadence tracking ──────────────────────────────────────────
    // Derives TickLockState from the regularity of SecondTick/MinutePulse events:
    //   Locked   : 3+ consecutive ticks spaced 0.5–1.5 s apart
    //   Searching: fewer than 3 good ticks, or recently lost regularity
    //   NoSignal : no tick seen for more than 7 s
    private long _lastTickTimestamp;
    private int _consecutiveGoodTicks;
    private TickLockState _tickLockState = TickLockState.NoSignal;

    // ── Predictive tick generation during signal fades ──────────────────────
    // When the 1 kHz signal fades (PLL in FadingOut state), synthesize ticks
    // at the PLL's expected timing to keep the frame decoder advancing while
    // awaiting signal recovery. Eliminates the 5+ second recovery period.
    private long _lastGeneratedSyntheticTickTime;

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
        _getTimestamp   = getTimestamp ?? Stopwatch.GetTimestamp;
        _diag           = diagnosticLogger;

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
        _pulseDetector  = new PulseDetector(sr, _syncDetector) { OnLog = onLog };
        _tickDetector   = new TickDetector(sr) { OnLog = onLog };
        _secondTickPll  = new SecondTickPll(onLog);
        _syncScorer     = new SyncQualityScorer(sr);
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
            long now = _getTimestamp();

            if (tick.Type == TickType.SecondTick)
            {
                // Check whether this tick falls in the WWV voice-announcement window
                // (seconds 52–59) or at a NIST-omitted position (29, 59).
                // WWV broadcasts a voice announcement starting ~52 s into each minute;
                // the 1000 Hz channel carries real energy from voice audio that can
                // trigger false SecondTick detections, corrupting the PLL phase.
                // Per NIST, no 1000 Hz tick is broadcast at seconds 29 or 59 — any
                // detection there is definitionally false.
                //
                // Voice-window ticks bypass the PLL to protect its phase estimate,
                // but still reach NotifyTick() and FrameDecoder.OnTick() so the 100 Hz
                // BCD rising-edge window is armed and bit position snap still works.
                int secondIdx = _frameDecoder.GetCurrentSecondIndex();
                bool inVoiceWindow = secondIdx >= 52; // seconds 52–59: voice announcement
                bool isNistOmitted = secondIdx == 29 || secondIdx == 59;

                if (inVoiceWindow)
                {
                    // Bypass PLL — do not let voice-induced ticks corrupt the phase estimate.
                    // Still update level snapshot so minute-pulse recovery baseline is current.
                    if (isNistOmitted)
                        _onLog?.Invoke($"[Tick] Spurious 1kHz at second {secondIdx} (NIST omits tick here; likely voice) — PLL bypassed");
                    _agc.SnapshotLevel();
                    _preMinutePulseNoiseFloor = _syncDetector.NoiseFloor;
                }
                else
                {
                    // Normal path: route through the PLL for glitch rejection.
                    // The most important rejection is the BPF/LP ring-down spurious tick that
                    // fires ~20 ms after the P0 minute pulse ends. The PLL, once locked,
                    // expects the next tick ~200 ms after P0 end; the spurious tick is ~180 ms
                    // early — outside the ±150 ms lock gate — and is silently discarded here.
                    // NIST omits the 1 kHz tick at positions 29 and 59.
                    // Tell the PLL to advance by 2 s when we're at the preceding position
                    // (28 or 58) so it doesn't lose lock waiting for a tick that won't come.
                    bool nextOmitted = secondIdx == 28 || secondIdx == 58;
                    long? pllTs = _secondTickPll.SubmitSecondTick(now, nextOmitted);
                    if (pllTs == null) return; // rejected as glitch — do not forward downstream
                    now = pllTs.Value;

                    _syncScorer.ObserveSecondTick(now);
                    _agc.SnapshotLevel();
                    _preMinutePulseNoiseFloor = _syncDetector.NoiseFloor;
                }
            }
            else if (tick.Type == TickType.MinutePulse)
            {
                // Seed the PLL from the cesium-accurate P0 end timestamp so it can predict
                // exactly where tick 1 should arrive (~200 ms later for an 800 ms pulse).
                _secondTickPll.SeedFromMinutePulse(tick.WidthSeconds, now);
            }

            // Notify the pulse detector of each second boundary so it can backstop
            // any stuck pulse and arm the post-tick rising-edge detection window.
            // For the minute pulse, open a 400 ms window (vs the normal 200 ms) because
            // the 1000 Hz detector exits the 800 ms pulse ~190 ms before the true
            // second-1 epoch, so the bit-1 100 Hz rising edge (at +1030 ms from anchor)
            // arrives 220 ms after this early tick — outside a 200 ms window. The 400 ms
            // window keeps detection open until +1210 ms, well past the rising edge.
            if (tick.Type == TickType.SecondTick || tick.Type == TickType.MinutePulse)
                _pulseDetector.NotifyTick(_tickDetector.LevelHigh,
                    windowMs: tick.Type == TickType.MinutePulse ? 400 : 200);

            // After the minute pulse, reset all level trackers so bits 1–4 are not
            // suppressed by the 800 ms pulse inflating the AGC and percentile reference.
            // Order: NotifyTick() must read _tickDetector.LevelHigh first (above);
            // TickDetector._levelHigh is zeroed by the TickDetected event (in TickDetector.cs);
            // ClearLevelReference() and BeginFastRecovery() execute after that zero is applied.
            if (tick.Type == TickType.MinutePulse)
            {
                _pulseDetector.ClearLevelReference();
                // Restore noise floor to the pre-burst quiet-period level so that
                // ticks 1–3 (~200 ms after P0 end) are detectable against the correct
                // baseline rather than the 800 ms burst-inflated floor.
                _syncDetector.NoiseFloor = _preMinutePulseNoiseFloor;
                var settings = _getSettings();
                if (settings.EnableAgc)
                    _agc.BeginFastRecovery();
            }

            // Update tick cadence state and fire heartbeat for the UI indicator.
            if (tick.Type == TickType.SecondTick || tick.Type == TickType.MinutePulse)
            {
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
                MinutePulseDetected?.Invoke(tick.WidthSeconds, DateTime.UtcNow);
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

        // 6. Detect 1000 Hz second ticks BEFORE classifying 100 Hz pulses.
        // The tick fires synchronously mid-buffer inside ProcessBlock, calling
        // NotifyTick() on the pulse detector. This arms the post-tick rising-edge
        // window so that when ProcessBlock runs next (step 7), the 100 Hz carrier's
        // rising edge (~30 ms after the tick) is already within the open window.
        _tickDetector.ProcessBlock(n120Out);

        // 7. Classify pulses using matched filter.
        _pulseDetector.ProcessBlock(envelope);

        // Feed the current envelope into the three-point discriminator so it can
        // sample at ~350 ms and ~650 ms after each 1000 Hz second tick, providing
        // an independent bit classification that does not rely on threshold crossing.
        _frameDecoder.FeedEnvelope(
            _pulseDetector.CurrentEnvelope,
            _pulseDetector.LevelHigh,
            Stopwatch.GetTimestamp());

        // Adaptive LP: start narrow for best SNR. Widen only when
        // rapid HF amplitude fading is detected so envelopes can follow
        // instantaneous carrier level. Reverts to narrow once fading subsides.
        bool shouldWiden = settings.EnableAdaptiveLowpass && _pulseDetector.IsAmplitudeUnstable;
        if (shouldWiden != _lpWidened)
        {
            _lpWidened = shouldWiden;
            _syncDetector.LowpassHz = shouldWiden ? WideLowpassHz : NarrowLowpassHz;
            _tickDetector.IsLowpassWidened = shouldWiden;
            _onLog?.Invoke($"[Adaptive LP] {(shouldWiden ? "Fading — Widen LP" : "Stable — Narrow LP")}");
        }

        // 10. Zero the signal meter if no pulse has arrived recently.
        _frameDecoder.CheckSignalTimeout();

        // 11. Generate predictive ticks during fade periods to keep frame decoder advancing.
        // When the PLL loses lock due to HF fading, it enters FadingOut state and generates
        // synthetic tick expectations at 1000 ms intervals. This allows rapid recovery when
        // the signal returns, eliminating the 5+ second wait for 5 consecutive real ticks.
        long now = _getTimestamp();
        if (_secondTickPll.IsFadingOut)
        {
            long? syntheticTs = _secondTickPll.GeneratePredictiveSecondTick(now);
            if (syntheticTs != null)
            {
                // Process synthetic tick through the frame decoder (but not the pulse detector,
                // since there is no real 100 Hz pulse during a complete fade).
                var syntheticTick = new TickEvent(TickType.SecondTick, 0.005); // nominal 5 ms tick
                _frameDecoder.OnTick(syntheticTick);
                _lastGeneratedSyntheticTickTime = syntheticTs.Value;
                _onLog?.Invoke($"[TickPLL] Generated predictive SecondTick");
            }
        }

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
    /// Arguments: pulse width in seconds (~0.8 s), and the UTC timestamp captured on the
    /// audio thread at the moment of detection (use this instead of DateTime.UtcNow in
    /// dispatcher-deferred handlers to avoid accumulating dispatcher-queue delay).
    /// </summary>
    public event Action<double, DateTime>? MinutePulseDetected;

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
        _secondTickPll.Reset();
        _syncScorer.Reset();
        _frameDecoder.Reset();
        _lpWidened = false;
        _diagCount = 0;
        _lastTickTimestamp = 0;
        _consecutiveGoodTicks = 0;
        _tickLockState = TickLockState.NoSignal;
    }

    /// <summary>
    /// Returns a formatted session statistics summary from the frame decoder.
    /// Call before Reset() to preserve the current counters.
    /// </summary>
    public string GetSessionSummary() => _frameDecoder.GetSessionSummary();

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
            Notch60RejDb:         Math.Max(0.0, notch60InDb  - notch60OutDb),
            Notch120InDb:         notch120InDb,
            Notch120OutDb:        notch120OutDb,
            Notch120RejDb:        Math.Max(0.0, notch120InDb - notch120OutDb),
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
            IsValid:          frame.IsValid,
            SlowFieldsConfident: frame.SlowFieldsConfident,
            HoursMinutesConfident: frame.HoursMinutesConfident,
            TimeFieldConfidence: frame.TimeFieldConfidence,
            DirectTimeBits: frame.DirectTimeBits,
            GapFilledTimeBits: frame.GapFilledTimeBits,
            MarkovPassed: frame.MarkovPassed,
            ClockDriftSeconds: frame.ClockDriftSeconds);
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
        // AGC gain well below 0 dB means the raw input is over-driven.
        // Threshold: -6 dB (AGC had to cut signal to less than half) while AGC is active.
        if (settings.EnableAgc && status.AgcGainDb < -6.0)
            status.InputSaturationAlert = "Input level too high — reduce audio input volume";
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
