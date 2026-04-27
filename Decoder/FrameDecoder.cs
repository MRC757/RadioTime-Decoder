using System.Diagnostics;
using WwvDecoder.Dsp;
using WwvDecoder.ViewModels;

namespace WwvDecoder.Decoder;

/// <summary>
/// State machine that assembles PulseEvents into 60-bit frames.
///
/// States:
///   Searching  — looking for any marker pulse to orient on
///   Syncing    — found markers, counting seconds to align to P0
///   Locked     — decoding full 60-bit frames
/// </summary>
public class FrameDecoder
{
    private enum State { Searching, Syncing, Locked }

    private State _state = State.Searching;
    private readonly int[] _bits = new int[60]; // 0=zero, 1=one, 2=marker
    private int _bitIndex;
    private int _consecutiveValid;
    private int _consecutiveInvalid;
    private TimeFrame? _latestFrame;

    // Persistent bit store for slowly-changing WWV fields.
    // These positions carry year, day-of-year, DUT1, DST, and leap-second data.
    // Year changes once per year; DOY once per day; DUT1/DST/leap rarely.
    // All change far more slowly than the 1-minute frame period, so a value confirmed
    // by one successful decode can fill missing positions in subsequent frames even
    // after anchor transitions clear the short-term ring buffer.
    //
    // Updated only from successfully BCD-validated frames (not from raw voted guesses).
    // Survives Reset() so a decoder restart during the same minute reuses the prior data.
    // Positions: DST1/leap (2-3), year units (4-7), DOY (30-41), DUT1 sign (50),
    //            year tens (51-54), DST2 (55), DUT1 magnitude (56-58).
    // Value of -1 = not yet observed.
    private static readonly int[] SlowBitPositions =
        [2, 3,                                     // DST1, leap second warning
         4, 5, 6, 7,                               // year units
         30, 31, 32, 33, 35, 36, 37, 38, 40, 41,  // day of year
         50,                                       // DUT1 sign
         51, 52, 53, 54,                           // year tens
         55, 56, 57, 58];                          // DST2, DUT1 magnitude
    private readonly int[] _persistentBits = Enumerable.Repeat(-1, 60).ToArray();

    // Operator-supplied (or auto-seeded) UTC date hint.
    // When set, any decoded frame whose date falls outside ±7 days of this hint is
    // rejected as corrupted — wrong year or DOY bits that happen to pass BCD range
    // checks (e.g. year=74 is valid BCD but clearly wrong when today is 2026).
    // Cleared by ClearKnownDate(); reset to null by Reset() only if never set by the
    // operator (auto-seeds are overwritten each listen session).
    private DateTime? _knownDateUtc;

    // Per-bit running accumulator (NTP driver 36 §3.2 "per-bit voting").
    // Each data-bit position carries a signed evidence score; marker and reserved
    // positions are structurally determined and never stored here.
    // Positive → evidence bit is 1.  Negative → evidence bit is 0.
    // Updated every minute from both the 100 Hz pulse measurement and the 1000 Hz
    // three-point discriminator.  Persists across re-anchors so clean frames from
    // prior minutes remain influential during subsequent faded minutes.
    // Cleared only by a full Reset() (user-initiated decoder restart).
    private readonly double[] _bitAccumulator = new double[60];

    // Three-point bipolar discriminator state (NTP driver 36 §5).
    // After each 1000 Hz second tick at position N, schedule envelope samples at
    // ~350 ms (between the Zero/One transition) and ~650 ms (between One/Marker).
    // Carrier HIGH at 350 ms → Zero.  HIGH only at 650 ms → One.  Both LOW → erasure.
    // This classifies the bit WITHOUT relying on carrier return-to-threshold detection,
    // which fails completely during HF fades that extend past the LOW period.
    private long   _envTickTimestamp = 0;   // Stopwatch timestamp of triggering tick
    private int    _envTickBitPos    = -1;  // bit position being sampled; -1 = idle
    private double _envSampleA       = 0;   // envelope at ~350 ms after tick
    private bool   _envGotA          = false;

    // Per-bit confidence for the frame currently being assembled.
    // True when the bit came from an actual pulse whose MatchedFilter and duration
    // classifications agreed. False for gap-filled positions or ambiguous pulses.
    private readonly bool[] _bitConfident = new bool[60];

    // Soft-decision confidence weight per bit [0.0 = erasure .. 1.0 = solid].
    // Derived from MatchedFilter.ClassifyWithConfidence: how far the measured LOW-sample
    // count sits from the nearest classification boundary.
    // Gap-filled and structurally-corrected bits receive weight 0.
    private readonly double[] _bitWeight  = new double[60];

    // Per-frame hit/miss counting: how many of the ~60 pulses this minute were
    // confidently classified (matched filter == duration classifier).
    // Mills' driver 36 tracks this over 6 minutes; we show it per-frame in the log.
    private int _frameHits;
    private int _frameTotal;

    // Clock advance prediction (Markov validation from Mills' driver 36):
    // after a successful decode, advance 1 minute and compare against the next
    // decode. Mismatch > 30 s indicates the frame decoder has drifted or re-aligned.
    private DateTime? _clockExpected;
    private int _clockVerifiedCount;

    // P0→P1 gap confirmation — require a confirmed 9-second inter-marker gap before
    // anchoring. Prevents re-anchoring on the constant Marker-length pulses that appear
    // during deep HF fades (where every pulse measures ~0.8s regardless of true width).
    // The 9-second P0→P1 gap is unique in the 60-second frame; all other inter-marker
    // gaps are 10 seconds, so this anchor is unambiguous.
    // When the 1000 Hz tick detector fires a MinutePulse, this gap confirmation is
    // bypassed — the minute pulse is unambiguous without needing a second Marker.
    // 0 = not set (equivalent to DateTime.MinValue).
    private long   _candidateAnchorTick;
    // Time from UTC second 0 to when the candidate P0 100 Hz Marker fired (30 ms rise + EffectiveDuration).
    // Used to back-project _candidateAnchorTick to the true second-0 epoch in _anchorWallTick.
    private double _candidateP0OffsetSeconds;

    // Per-bit state flags for the visualization grid (set in StoreBit / gap-fill / correction).
    private readonly bool[] _bitGapFilled = new bool[60];
    private readonly bool[] _bitCorrected = new bool[60];

    // Stopwatch tick of the most recent P0 anchor.
    // Used by the second-tick alignment check: tick N should arrive at approximately
    // _anchorWallTick + N seconds. A discrepancy > 2 positions indicates the BCD
    // bit counter has drifted (gap fills or double-triggers have shifted the index).
    // 0 = not set. Stopwatch timestamps are always positive.
    private long _anchorWallTick;

    // Anchor-phase diagnostic state.
    // Captures the minute-pulse anchor parameters so the very next SecondTick can be
    // logged with its gap from end-of-minute-pulse and its elapsed-since-anchor value.
    // If back-projection is correct, gap ≈ 200 ms (1.0 s tick interval − 0.8 s pulse end)
    // and elapsed-since-anchor ≈ 1.0 s, yielding tickBit=1.
    private long   _diagMinutePulseEndTick;
    private double _diagMinutePulseWidth;
    private bool   _diagAwaitingFirstSecondTick;

    // Pending 1000 Hz second-tick state.
    // Each second tick (except 29 and 59, which are omitted per NIST) records the
    // tick-derived bit index. The next 100 Hz BCD pulse is snapped to that position:
    //   • gap between _bitIndex and target → fill with erasures then store normally
    //   • _bitIndex ahead of target → pulse discarded (double-trigger absorbed extra bit)
    //   • exact match → store normally
    // This decouples bit-position tracking from pulse counting so a missed or extra BCD
    // pulse does not shift all subsequent bits in the frame.
    private int  _tickPendingBitIndex = -1; // -1 = no pending tick
    private long _tickPendingTimestamp;

    // Concurrent P0 Marker absorb window (deadline tick).
    // When OnTick(MinutePulse) anchors at P0 (bitIndex=1), the 100 Hz PulseDetector's
    // P0 Marker fires in the same or next audio block (~0–20 ms later, because the 1000 Hz
    // envelope exits its threshold ~8 ms before the 100 Hz envelope exits its threshold).
    // That 100 Hz Marker must be treated as a P0 confirmation, not stored as bit 1, or
    // CheckFrameCorrupted would immediately see consecutive Markers and reset to Searching.
    // 0 = window not active.
    private long _skip100HzP0UntilTick;

    // Last known signal percent, cached so OnTick's ReportStatus call doesn't show 0%.
    private double _lastSignalPercent;

    // Tick-based fade detection.
    // SecondTick events fire at each 1 Hz boundary except positions 29 and 59 (omitted
    // by NIST from the 1000 Hz channel). Two or more consecutive non-omitted ticks that
    // fail to arrive indicate genuine HF signal loss, independently of the 100 Hz BCD
    // amplitude. TickFadeActive zeroes the soft-weight of any BCD pulse stored while the
    // 1000 Hz channel is dark — complementing PulseDetector.IsFading (100 Hz amplitude,
    // gated to between-pulse periods).
    // Maximum legitimate gap between two consecutive received ticks is 2.0 s (one omitted
    // position). A 1.3 s window is used to begin counting: if the elapsed time since the
    // last tick exceeds 1.3 s, at least one tick interval has passed; the 29/59 exclusion
    // then determines whether that constitutes a genuine miss or an expected omission.
    private long _lastSecondTickTimestamp; // Stopwatch timestamp of most recent SecondTick
    private int  _lastSecondTickBit = -1;  // bit position of last SecondTick; -1 = none yet
    public bool TickFadeActive { get; private set; }

    // Marker saturation gate: during deep ionospheric fades the 100 Hz carrier drops
    // for ~0.8s even during what should be 0.2/0.5s data-bit periods, so nearly all
    // pulses are classified as Marker. Normal WWV has 7/60 = 11.7% Markers; >60%
    // indicates the signal is too corrupted to anchor on.
    // Hysteresis: enter at 60%, exit at 25% (avoids rapid oscillation on marginal signals).
    private const int    MarkerRateWindowSize = 20;
    private readonly Queue<bool> _recentMarkerFlags = new();
    private bool _signalTooFaded;
    private const double MarkerSatHigh = 0.60; // enter faded state above this rate
    private const double MarkerSatLow  = 0.25; // exit faded state below this rate

    private readonly Action<SignalStatus> _onSignalUpdate;
    private readonly Action<TimeFrame> _onFrameDecoded;
    private readonly Action<string>? _onLog;
    private readonly Action<FrameCell[]>? _onFrameUpdate;

    // Monotonic clock for pulse timing. Defaults to _getTimestamp().
    // Tests may inject a sample-based clock so timing works at simulation speed.
    private readonly Func<long> _getTimestamp;

    // Receiver mode detection: warn when broadband audio signal is present but the 100 Hz
    // subcarrier level is consistently near zero, which is the signature of the receiver being
    // tuned in SSB or narrow CW mode rather than AM. WWV requires AM (DSB-LC) to preserve
    // the 100 Hz subcarrier. SSB strips the carrier entirely — nothing will ever decode.
    private const double SubcarrierAbsentThreshold = 5.0;  // %
    private const double AudioPresentThreshold     = 20.0; // % of peak
    private const int    ReceiverModeCheckInterval = 200;  // audio blocks (~10 s at 50 ms/block)
    private int _receiverModeCheckCount;
    private int _subcarrierAbsentCount;
    private bool _receiverModeWarned;

    // Signal metering
    private double _lockQuality; // 0..1
    private double _subcarrierPercent;
    private long _lastPulseTick; // Stopwatch tick of most recent pulse; 0 = no pulse yet

    // Inter-pulse cadence guard.
    // WWV produces exactly one pulse per second.
    //   < MinPulseGap: double-trigger — brief carrier recovery re-entered the LOW threshold.
    //   > FillGap:     ≥1 second was swallowed by a deep HF fade. Fill and continue.
    //   > ResetGap:    too many unknowns (>half the frame) to fill reliably — reset.
    //
    // Minimum legitimate consecutive-pulse gap depends on the preceding pulse type:
    //   After a Marker: Marker fires at +0.83 s; next Zero fires at +1.23 s → gap 0.40 s.
    //     Use 0.35 s threshold so Marker→Zero pairs are NOT suppressed.
    //   After a Zero/One: shortest legitimate gap is One→Zero ≈ 0.53+0.23 = 0.76 s,
    //     well above the 0.50 s threshold used for those cases.
    private long _lastNonTickPulseTick; // Stopwatch tick; 0 = no pulse yet
    private PulseType _lastStoredPulseType = PulseType.Zero;
    private bool _lastPulseWasSynthetic; // set by tick gap fill; bypasses double-trigger check for the next real pulse
    private const double MinPulseGapAfterMarker  = 0.35;
    private const double MinPulseGapAfterOther   = 0.50;
    private const double FillGapSeconds          = 2.0;
    private const double ResetGapSeconds         = 30.0;

    public FrameDecoder(Action<SignalStatus> onSignalUpdate, Action<TimeFrame> onFrameDecoded,
                        Action<string>? onLog = null, Action<FrameCell[]>? onFrameUpdate = null,
                        Func<long>? getTimestamp = null)
    {
        _onSignalUpdate  = onSignalUpdate;
        _onFrameDecoded  = onFrameDecoded;
        _onLog           = onLog;
        _onFrameUpdate   = onFrameUpdate;
        _getTimestamp    = getTimestamp ?? Stopwatch.GetTimestamp;
    }

    public void Reset()
    {
        _state = State.Searching;
        _bitIndex = 0;
        _consecutiveValid = 0;
        _consecutiveInvalid = 0;
        _latestFrame = null;
        _lockQuality = 0;
        _subcarrierPercent = 0;
        _lastPulseTick = 0;
        _lastNonTickPulseTick = 0;
        _lastStoredPulseType = PulseType.Zero;
        _lastPulseWasSynthetic = false;
        Array.Clear(_bitAccumulator, 0, 60);
        Array.Clear(_bitConfident,   0, 60);
        _envTickTimestamp = 0;
        _envTickBitPos    = -1;
        _envGotA          = false;
        _frameHits = 0;
        _frameTotal = 0;
        _clockExpected = null;
        _clockVerifiedCount = 0;
        _candidateAnchorTick    = 0;
        _candidateP0OffsetSeconds = 0;
        _anchorWallTick         = 0;
        _skip100HzP0UntilTick   = 0;
        _tickPendingBitIndex      = -1;
        _tickPendingTimestamp     = 0;
        _lastSignalPercent        = 0;
        _lastSecondTickTimestamp  = 0;
        _lastSecondTickBit        = -1;
        TickFadeActive            = false;
        _recentMarkerFlags.Clear();
        _signalTooFaded = false;
        ReportStatus();
    }

    public void OnPulse(PulseEvent pulse, double peakEnvelope, double noiseFloor, double subcarrierLevel)
    {
        // Update signal metering — peakEnvelope is the running peak amplitude from
        // PulseDetector, which correctly reflects signal strength during the pulse body
        // (not the near-zero value at pulse-end that CurrentEnvelope would give).
        _lastPulseTick = _getTimestamp();
        _subcarrierPercent = Math.Min(100, subcarrierLevel * 100.0);
        double snr = noiseFloor > 0 ? peakEnvelope / noiseFloor : 1.0;
        double signalPercent = Math.Min(100, snr * 10.0);
        _lastSignalPercent = signalPercent;
        ReportStatus(signalPercent);

        // Tick pulses (< 100 ms) are transition artifacts, not BCD data — skip them entirely.
        if (pulse.Type == PulseType.Tick)
        {
            if (_onLog != null && (_state == State.Syncing || _state == State.Locked))
                _onLog($"[{_bitIndex:D2}] TICK (discarded) {pulse.WidthSeconds:F3}s d={pulse.EffectiveDuration:F3}s");
            return;
        }

        // Track per-frame hit/miss: confident = both classifiers agree.
        // Low hit rate indicates a faded minute — interpret output with caution.
        if (_state == State.Syncing || _state == State.Locked)
        {
            _frameTotal++;
            bool confident = !pulse.MatchedType.HasValue || pulse.MatchedType == pulse.DurationType;
            if (confident) _frameHits++;
        }

        // Marker saturation gate: update the rolling window in all states so it is
        // primed when we return to Searching after a frame failure.
        _recentMarkerFlags.Enqueue(pulse.Type == PulseType.Marker);
        if (_recentMarkerFlags.Count > MarkerRateWindowSize)
            _recentMarkerFlags.Dequeue();
        if (_state == State.Searching && _recentMarkerFlags.Count >= MarkerRateWindowSize)
        {
            double markerRate = (double)_recentMarkerFlags.Count(m => m) / _recentMarkerFlags.Count;
            bool wasFaded = _signalTooFaded;
            _signalTooFaded = markerRate > MarkerSatHigh
                || (_signalTooFaded && markerRate > MarkerSatLow);
            if (_signalTooFaded && !wasFaded)
            {
                _candidateAnchorTick = 0; // discard stale candidate
                _onLog?.Invoke($"Signal too faded ({markerRate:P0} Marker rate, last {MarkerRateWindowSize} pulses) — anchor paused");
            }
            else if (!_signalTooFaded && wasFaded)
                _onLog?.Invoke($"Signal recovering ({markerRate:P0} Marker rate) — resuming anchor search");
        }

        // Ratio of peak envelope to tracked carrier level (subcarrierLevel = PulseDetector.LevelHigh).
        // Should be ~1.0 when the tracker is keeping up with the true carrier.
        // Ratio >> 1 means levelHigh is severely undertracked — the matched filter fell back
        // to peakEnvelope as its reference. If peakEnvelope was inflated by a multipath spike
        // (constructive interference), the fallback reference is too high: midThreshold exceeds
        // the true LOW carrier, every buffer sample counts as LOW, and effective duration equals
        // the raw buffer length (~0.78 s) → Marker. Both classifiers agree → stored as confident
        // Marker, defeating the soft-decision voting. Erasure is better than a wrong confident vote.
        double levelTrackRatio = subcarrierLevel > 0 ? peakEnvelope / subcarrierLevel : 0;

        if (_onLog != null && (_state == State.Syncing || _state == State.Locked))
        {
            char typeChar = pulse.Type switch
            {
                PulseType.Marker => 'M',
                PulseType.One    => '1',
                _                => '0'
            };
            string matchNote = pulse.MatchedType.HasValue && pulse.MatchedType != pulse.DurationType
                ? $" [dur={pulse.DurationType}]"
                : string.Empty;
            // d=effective LOW duration (MatchedFilter energy integral, not raw edge width)
            // conf=distance from nearest boundary (1.0=solid, 0.0=on boundary)
            string dNote = pulse.EffectiveDuration > 0
                ? $"  d={pulse.EffectiveDuration:F3}s conf={pulse.Confidence:F2}"
                : string.Empty;
            _onLog($"[{_bitIndex:D2}] {typeChar} {pulse.WidthSeconds:F3}s  env/H={levelTrackRatio:F2}{matchNote}{dNote}");
        }

        // Inter-pulse cadence guard — enforce the 1-pulse-per-second cadence.
        // Stopwatch timestamps are monotonic — immune to system clock changes (e.g. minute-start sync).
        long now = _getTimestamp();
        if (_lastNonTickPulseTick != 0)
        {
            double gap = TicksToSeconds(now - _lastNonTickPulseTick);

            // Double-trigger: brief carrier recovery re-entered the LOW threshold within
            // the same second. The minimum gap threshold is smaller after a Marker because
            // a Marker→Zero sequence has only a 0.40 s gap between pulse-fire events.
            double minGap = _lastStoredPulseType == PulseType.Marker
                ? MinPulseGapAfterMarker
                : MinPulseGapAfterOther;
            // Bypass double-trigger suppression when the previous "pulse" was synthetic
            // (tick gap fill). The gap will be short but the pulse is real.
            if (!_lastPulseWasSynthetic && gap < minGap)
            {
                // Suppress log when faded — double-triggers during a dead signal are noise,
                // not frame events worth tracking.
                if (!_signalTooFaded && _state != State.Searching)
                    _onLog?.Invoke($"[{_bitIndex:D2}] Suppressed double-trigger ({gap * 1000:F0} ms after last)");
                return;
            }
            _lastPulseWasSynthetic = false;

            if (gap > FillGapSeconds && (_state == State.Syncing || _state == State.Locked))
            {
                if (gap > ResetGapSeconds)
                {
                    // Blackout too long to fill reliably — abandon and re-anchor.
                    _onLog?.Invoke($"Signal blackout {gap:F1}s → SEARCHING");
                    _consecutiveValid = 0;
                    _consecutiveInvalid = 0;
                    ResetToSearching(signalPercent);
                    // Fall through — if this pulse is a Marker, re-anchor immediately.
                }
                else
                {
                    // Estimate seconds swallowed: each skipped second = one skipped bit.
                    // Subtract 1 because the current pulse accounts for the second after the gap.
                    int skipped = Math.Max(0, (int)Math.Round(gap) - 1);
                    _onLog?.Invoke($"Signal gap {gap:F1}s at [{_bitIndex:D2}] — filling {skipped} bit(s)");

                    // Fill each missing position. Known marker positions get value 2;
                    // data/reserved positions get 0 (the most common value in any clean frame).
                    // Gap-filled bits are marked NOT confident — they are estimates, not
                    // measurements, and must not outvote confirmed bits from prior frames.
                    for (int i = 0; i < skipped && _bitIndex < 60; i++)
                    {
                        _bits[_bitIndex]         = IsExpectedMarkerPosition(_bitIndex) ? 2 : 0;
                        _bitConfident[_bitIndex] = false;
                        _bitWeight[_bitIndex]    = 0.0;
                        _bitGapFilled[_bitIndex] = true;
                        _bitIndex++;
                        PublishFrameVisualization();
                    }

                    // If filling completed the frame, attempt decode now. The current pulse
                    // is the first bit of the next frame and will be stored by the switch below.
                    if (_bitIndex >= 60)
                        TryDecode(signalPercent);
                    // Do NOT reset here — fall through so the switch stores the current pulse
                    // and continues frame collection (or re-anchors if decode failed twice).
                }
            }
        }
        // Absorb the concurrent 100 Hz P0 Marker when the 1000 Hz minute pulse already
        // anchored P0 in the same (or immediately prior) audio block.
        // The 1000 Hz envelope exits ~8 ms before the 100 Hz envelope exits (the 1000 Hz
        // exit threshold is 25% vs the 100 Hz's 62%, so the 1000 Hz falls faster).
        // If that P0 Marker were stored it would land at bitIndex=1, causing CheckFrameCorrupted
        // to see consecutive Markers at positions 0 and 1 and immediately reset.
        if (_skip100HzP0UntilTick != 0)
        {
            if (now <= _skip100HzP0UntilTick && pulse.Type == PulseType.Marker && _bitIndex == 1)
            {
                _skip100HzP0UntilTick = 0;
                _lastNonTickPulseTick = now;
                _lockQuality = Math.Min(1.0, _lockQuality + 0.10);
                _onLog?.Invoke("[00] 100 Hz P0 confirmed (concurrent with minute pulse)");
                return;
            }
            // Expired — clear
            if (now > _skip100HzP0UntilTick) _skip100HzP0UntilTick = 0;
        }

        // Tick-derived bit alignment: snap _bitIndex to the position the most recent
        // 1000 Hz second tick identified, provided the pulse arrives within the same
        // one-second window as that tick.  This corrects drift from missed or extra
        // 100 Hz BCD pulses without waiting for the large-drift gap-fill threshold.
        if (_tickPendingBitIndex >= 0 && (_state == State.Syncing || _state == State.Locked))
        {
            double tickAge = TicksToSeconds(now - _tickPendingTimestamp);
            if (tickAge < 0.95) // BCD pulse must arrive within one second of the tick
            {
                int target = _tickPendingBitIndex;
                if (target > _bitIndex && target < 60)
                {
                    // Fill any skipped bit positions with erasures.
                    _onLog?.Invoke($"Tick sync: fill [{_bitIndex:D2}]→[{target:D2}] ({target - _bitIndex} erased)");
                    while (_bitIndex < target)
                    {
                        _bits[_bitIndex]         = IsExpectedMarkerPosition(_bitIndex) ? 2 : 0;
                        _bitConfident[_bitIndex] = false;
                        _bitWeight[_bitIndex]    = 0.0;
                        _bitGapFilled[_bitIndex] = true;
                        _bitIndex++;
                    }
                    PublishFrameVisualization();
                    if (_bitIndex >= 60)
                    {
                        TryDecode(signalPercent);
                        _tickPendingBitIndex = -1;
                        return;
                    }
                }
                else if (target < _bitIndex)
                {
                    // BCD counter ran ahead of the tick — a double-triggered pulse was
                    // counted. Discard the current pulse; _bitIndex is already correct.
                    _onLog?.Invoke($"Tick sync: pulse at [{_bitIndex:D2}] discarded (tick says [{target:D2}])");
                    _tickPendingBitIndex = -1;
                    return;
                }
                // target == _bitIndex: perfect alignment, no correction needed.
            }
            _tickPendingBitIndex = -1;
        }

        _lastNonTickPulseTick = now;

        switch (_state)
        {
            case State.Searching:
                if (_signalTooFaded) break; // wait for marker rate to recover

                if (pulse.Type == PulseType.Marker)
                {
                    if (_candidateAnchorTick == 0)
                    {
                        // First Marker seen — record as candidate P0, wait for P1 to confirm.
                        _candidateAnchorTick      = now;
                        // 100 Hz carrier rises ~30 ms after the second tick, then holds HIGH
                        // for EffectiveDuration. This offset projects the fire time back to
                        // the true second-0 epoch when P0→P1 is later confirmed.
                        _candidateP0OffsetSeconds = 0.030 + pulse.EffectiveDuration;
                        _onLog?.Invoke($"Candidate P0 (width={pulse.WidthSeconds:F3}s) — waiting for P1 in ~9s");
                        _onLog?.Invoke($"[Anchor diag] P0→P1 fallback candidate: " +
                                       $"100 Hz Marker width={pulse.WidthSeconds * 1000:F1}ms, " +
                                       $"effDuration={pulse.EffectiveDuration * 1000:F1}ms, " +
                                       $"projected offset = (Marker end) − {_candidateP0OffsetSeconds * 1000:F1}ms " +
                                       $"(30 ms rise delay + effDuration)");
                    }
                    else
                    {
                        double interMarkerGap = TicksToSeconds(now - _candidateAnchorTick);
                        if (interMarkerGap >= 8.5 && interMarkerGap <= 9.5)
                        {
                            // Confirmed P0→P1: the unique 9-second gap at the top of each minute.
                            // Fill seconds-field bits 1-8 as gap-filled (not confident), confirm
                            // both markers, and enter SYNCING starting at position 10.
                            _bits[0] = 2; _bitConfident[0] = true;
                            for (int i = 1; i <= 8; i++)
                            {
                                _bits[i] = 0;
                                _bitConfident[i] = false; // gap-filled estimate — not confident
                            }
                            _bits[9] = 2; _bitConfident[9] = true;
                            _bitIndex = 10;
                            _state = State.Syncing;
                            _consecutiveInvalid = 0;
                            _lastStoredPulseType = PulseType.Marker;
                            _lockQuality = 0.15; // partial credit for confirmed P0→P1 pair
                            _frameHits = 2; _frameTotal = 2; // two confirmed markers: P0 and P1
                            // Back-project to true second-0 epoch: _candidateAnchorTick is
                            // when the P0 100 Hz Marker fired (rise delay + HIGH duration
                            // after second 0). Subtracting the stored offset gives UTC second 0.
                            _anchorWallTick = _candidateAnchorTick - SecondsToTicks(_candidateP0OffsetSeconds);
                            _candidateAnchorTick    = 0;
                            _candidateP0OffsetSeconds = 0;
                            _onLog?.Invoke($"Confirmed P0→P1 ({interMarkerGap:F1}s gap) → SYNCING at bit 10");
                            ReportStatus(signalPercent);
                            PublishFrameVisualization();
                        }
                        else if (interMarkerGap >= 9.5 && interMarkerGap <= 10.5)
                        {
                            // ~10-second gap (P1→P2 or later) — valid inter-marker spacing, but
                            // we don't know which pair. Update candidate to current marker and keep
                            // looking; the next gap check may confirm P0→P1 of the next minute.
                            _candidateAnchorTick = now;
                            _onLog?.Invoke($"Inter-marker {interMarkerGap:F1}s (P1+ pair) — updating candidate");
                        }
                        else
                        {
                            // Wrong gap — this Marker becomes the new candidate.
                            // Only log if the gap is in a plausible range (> 2s); shorter gaps
                            // are obvious consecutive-Marker noise and clutter the log.
                            if (interMarkerGap >= 2.0)
                                _onLog?.Invoke($"Bad gap {interMarkerGap:F1}s — reset candidate");
                            _candidateAnchorTick = now;
                        }
                    }
                }
                // Non-Marker pulses in Searching don't reset the candidate — data bits 1-8
                // between the candidate P0 and the expected P1 arrive here and are expected.
                break;

            case State.Syncing:
                StoreBit(pulse);
                // Downgrade confidence when levelHigh undertracked the true carrier.
                // Graded: moderate undertracking (env/H 2.0–3.5) halves the vote weight
                // so these bits participate with reduced influence; severe undertracking
                // (env/H > 3.5) is a full erasure — the matched-filter reference is too
                // unreliable to trust at all.
                if (levelTrackRatio > 2.0 && _bitIndex > 0)
                {
                    if (levelTrackRatio > 3.5)
                    {
                        _bitConfident[_bitIndex - 1] = false;
                        _bitWeight[_bitIndex - 1]    = 0.0;
                        _onLog?.Invoke($"[{_bitIndex - 1:D2}] Erased: env/H={levelTrackRatio:F1} — severe undertracking");
                    }
                    else
                    {
                        _bitWeight[_bitIndex - 1] *= 0.5;
                        _onLog?.Invoke($"[{_bitIndex - 1:D2}] Downweighted ×0.5: env/H={levelTrackRatio:F1}");
                    }
                }
                // Accumulate lock quality as markers appear at expected positions.
                if (pulse.Type == PulseType.Marker && IsExpectedMarkerPosition(_bitIndex - 1))
                {
                    _lockQuality = Math.Min(1.0, _lockQuality + 0.15);
                }
                if (CheckFrameCorrupted(signalPercent)) break;
                if (_bitIndex == 60)
                {
                    TryDecode(signalPercent);
                    // Accumulator-based voting: the bitAccumulator persists across frames so
                    // each new frame adds evidence without needing to keep a ring buffer.
                    // TryDecode's _consecutiveInvalid >= 2 check handles giving up on total failure.
                    if (_state != State.Locked)
                        _bitIndex = 0;
                }
                break;

            case State.Locked:
                StoreBit(pulse);
                if (levelTrackRatio > 2.0 && _bitIndex > 0)
                {
                    if (levelTrackRatio > 3.5)
                    {
                        _bitConfident[_bitIndex - 1] = false;
                        _bitWeight[_bitIndex - 1]    = 0.0;
                        _onLog?.Invoke($"[{_bitIndex - 1:D2}] Erased: env/H={levelTrackRatio:F1} — severe undertracking");
                    }
                    else
                    {
                        _bitWeight[_bitIndex - 1] *= 0.5;
                        _onLog?.Invoke($"[{_bitIndex - 1:D2}] Downweighted ×0.5: env/H={levelTrackRatio:F1}");
                    }
                }
                if (CheckFrameCorrupted(signalPercent)) break;
                if (_bitIndex == 60)
                    TryDecode(signalPercent);
                break;
        }
    }

    /// <summary>
    /// Called when the 1000 Hz tick detector fires.
    ///
    /// MinutePulse (800 ms) = unambiguous P0 marker. Anchor directly without waiting
    /// for the 9-second P0→P1 gap confirmation. This is more reliable than the 100 Hz
    /// channel alone because:
    ///   - The 1000 Hz tone is separate from the BCD modulation — no amplitude ambiguity.
    ///   - The 800 ms minute pulse is the longest and loudest feature in the audio.
    ///   - Detection does not require comparing two consecutive pulses.
    ///
    /// SecondTick (5 ms) = second boundary marker. Logged for diagnostics.
    /// Future use: sample I-channel at 15/200/500 ms from tick for bipolar matched filter.
    /// </summary>
    public void OnTick(TickEvent tick)
    {
        // SecondTick (5 ms tone at each second boundary): logged only during active
        // frame collection so the pulse timing is visible without flooding the log.
        // Future use: sample the 100 Hz I-channel at 15/200/500 ms from this epoch
        // for the bipolar matched filter from NTP driver 36.
        if (tick.Type == TickType.SecondTick)
        {
            // Only act when a P0 anchor is established and the frame is in progress.
            if (_anchorWallTick == 0 || (_state != State.Syncing && _state != State.Locked))
                return;

            long tickNow = _getTimestamp();
            double elapsed = TicksToSeconds(tickNow - _anchorWallTick);
            int tickBit = (int)Math.Round(elapsed) % 60;

            // Anchor-phase diagnostic: log the very first SecondTick after a minute anchor.
            // Reveals whether the back-projection of _anchorWallTick is correct.
            //   gap          = time from end-of-minute-pulse to this tick (expect ~200 ms)
            //   elapsed      = time from _anchorWallTick to this tick    (expect ~1000 ms)
            //   roundError   = elapsed − round(elapsed)                   (≈ 0 if anchor true)
            // A non-zero roundError or a tickBit ≠ 1 means the anchor is off, and that
            // offset will systematically distort every bit window in the frame.
            if (_diagAwaitingFirstSecondTick)
            {
                _diagAwaitingFirstSecondTick = false;
                double gap        = TicksToSeconds(tickNow - _diagMinutePulseEndTick);
                double roundError = elapsed - Math.Round(elapsed);
                _onLog?.Invoke($"[Anchor diag] First SecondTick after MinutePulse: " +
                               $"gap={gap * 1000:F1}ms (expect ~200 ms), " +
                               $"elapsed-since-anchor={elapsed * 1000:F1}ms (expect ~1000 ms), " +
                               $"round-error={roundError * 1000:+0.0;-0.0}ms, tickBit={tickBit} (expect 1)");
            }

            // Record arrival for tick-based fade detection regardless of whether this
            // position is 29/59. A received tick always resets the missing-tick counter.
            _lastSecondTickTimestamp = tickNow;
            _lastSecondTickBit = tickBit;
            if (TickFadeActive)
            {
                TickFadeActive = false;
                _onLog?.Invoke($"[{tickBit:D2}] Tick fade cleared");
            }

            // NIST: the 29th and 59th second pulses are omitted from the 1000 Hz channel.
            // The 100 Hz BCD channel still transmits position markers at those seconds;
            // normal pulse counting handles them without tick guidance.
            if (tickBit == 29 || tickBit == 59)
                return;

            // Log each received second tick so the 1000 Hz channel's health is visible.
            // One line per second alongside the 100 Hz BCD pulse lines.
            if (_state == State.Locked)
                _onLog?.Invoke($"  Tick [{tickBit:D2}]");

            // Record the tick-derived position. OnPulse will snap _bitIndex to this
            // value when the next 100 Hz BCD pulse arrives (within a 950 ms window).
            _tickPendingBitIndex  = tickBit;
            _tickPendingTimestamp = tickNow;

            // Three-point discriminator: schedule envelope samples at ~350 ms and ~650 ms
            // after this tick so FeedEnvelope() can classify the bit independently of
            // whether the 100 Hz carrier threshold-crossing detection succeeds.
            // Only schedule for data-bit positions; markers are structurally known.
            if (!IsExpectedMarkerPosition(tickBit) && !IsReservedPosition(tickBit) && !TickFadeActive)
            {
                _envTickTimestamp = tickNow;
                _envTickBitPos    = tickBit;
                _envGotA          = false;
            }

            // Large-drift immediate gap fill: when the BCD channel has been completely
            // silent for ≥ FillGapSeconds AND we are ≥ 3 positions behind, fill now
            // rather than waiting for the next BCD pulse to trigger the fill in OnPulse.
            int drift = tickBit - _bitIndex;
            if (drift >= 3 && _bitIndex < 59)
            {
                double gapFrom100Hz = _lastNonTickPulseTick != 0
                    ? TicksToSeconds(tickNow - _lastNonTickPulseTick)
                    : double.MaxValue;
                if (gapFrom100Hz >= FillGapSeconds)
                {
                    int fillTo = Math.Min(tickBit, 59); // leave pos 59 for the frame-end pulse
                    _onLog?.Invoke($"Tick gap fill: [{_bitIndex:D2}]→[{fillTo:D2}] ({fillTo - _bitIndex} bits)");
                    while (_bitIndex < fillTo)
                    {
                        _bits[_bitIndex]         = IsExpectedMarkerPosition(_bitIndex) ? 2 : 0;
                        _bitConfident[_bitIndex] = false;
                        _bitWeight[_bitIndex]    = 0.0;
                        _bitGapFilled[_bitIndex] = true;
                        _bitIndex++;
                    }
                    _lastNonTickPulseTick = tickNow;
                    _lastPulseWasSynthetic = true;
                    PublishFrameVisualization();
                    // Pending bit index now consumed by the fill; clear it so OnPulse
                    // doesn't try to fill again when the next BCD pulse arrives at fillTo.
                    _tickPendingBitIndex = -1;
                }
                else if (Math.Abs(drift) > 2)
                {
                    _onLog?.Invoke($"Tick: bitIndex={_bitIndex}, tick-derived={tickBit} ({drift:+0;-0} positions)");
                }
            }
            return;
        }

        // MinutePulse — direct P0 anchor (Stopwatch for monotonic timing).
        long now = _getTimestamp();

        // Edge case: 100 Hz P0 Marker arrived first in the same audio block
        // (PulseDetector runs before TickDetector in DecoderPipeline.ProcessSamples).
        // If bitIndex==1 and bits[0]==2, the 100 Hz channel already correctly anchored P0.
        // Just confirm and boost lock quality — no re-anchor needed.
        bool alreadyAtP0 = (_state == State.Syncing || _state == State.Locked)
                           && _bitIndex == 1 && _bits[0] == 2;
        if (alreadyAtP0)
        {
            _lockQuality = Math.Min(1.0, _lockQuality + 0.10);
            _onLog?.Invoke($"Minute pulse confirms P0 ({tick.WidthSeconds:F3}s)  [100 Hz was first]");
            return;
        }

        // If the current frame is nearly complete (fading killed the last 1–5 pulses),
        // gap-fill the remaining bits and call TryDecode before discarding the frame.
        // This is the most common failure mode: ionospheric fading drops P6 (bit 59)
        // right at the end, so _bitIndex stalls at 58–59 and TryDecode never fires.
        if ((_state == State.Syncing || _state == State.Locked) && _bitIndex >= 55 && _bitIndex < 60)
        {
            int stoppedAt = _bitIndex;
            while (_bitIndex < 60)
            {
                _bits[_bitIndex]         = IsExpectedMarkerPosition(_bitIndex) ? 2 : 0;
                _bitConfident[_bitIndex] = false;
                _bitWeight[_bitIndex]    = 0.0;
                _bitGapFilled[_bitIndex] = true;
                _bitIndex++;
            }
            _onLog?.Invoke($"Near-complete frame rescued at minute boundary (gap-filled {stoppedAt}–59)");
            TryDecode(_lastSignalPercent);
            // TryDecode resets _bitIndex to 0; fall through to re-anchor below.
        }

        // Anchor (or re-anchor) at P0.
        // Clear in-progress frame state so collection starts fresh from bit 1.
        int prevIndex = _bitIndex;
        Array.Clear(_bits,         0, 60);
        Array.Clear(_bitConfident, 0, 60);
        Array.Clear(_bitWeight,    0, 60);
        Array.Clear(_bitGapFilled, 0, 60);
        Array.Clear(_bitCorrected, 0, 60);
        _bits[0]         = 2;
        _bitConfident[0] = true;
        _bitWeight[0]    = 1.0; // P0 marker is certain
        _bitIndex = 1;
        _frameHits = 1;
        _frameTotal = 1;
        _lastStoredPulseType = PulseType.Marker;
        _candidateAnchorTick = 0;
        // Back-project to the true second-0 epoch: the TickDetector fires at the END
        // of the 800 ms minute pulse. Subtracting the measured width gives UTC second 0,
        // so (tickNow - _anchorWallTick) for second-N ticks equals N seconds (not N-1).
        _anchorWallTick = now - SecondsToTicks(tick.WidthSeconds);

        // Anchor-phase diagnostic: capture the minute-pulse parameters so the very next
        // SecondTick handler can log its arrival timing relative to this anchor. If the
        // back-projection at line above is correct, the next SecondTick (real second 1)
        // should arrive ~200 ms after the minute pulse ended, and elapsed-since-anchor
        // should be ~1.0 s (tickBit=1).
        _diagMinutePulseEndTick      = now;
        _diagMinutePulseWidth        = tick.WidthSeconds;
        _diagAwaitingFirstSecondTick = true;
        _onLog?.Invoke($"[Anchor diag] MinutePulse end-of-tone: width={tick.WidthSeconds * 1000:F1}ms, " +
                       $"projected anchor = (end-of-tone) − {tick.WidthSeconds * 1000:F1}ms " +
                       $"(nominal width = 800.0 ms)");

        // Reset the inter-pulse gap timer to P0 so the gap-fill algorithm measures gaps
        // relative to this anchor, not to the last 100 Hz pulse from the previous minute.
        // Without this reset, a signal fade spanning the minute boundary (e.g., last 100 Hz
        // pulse 3 s before the minute) inflates the apparent gap for bit [01] to 3 s and
        // causes gap-fill to over-insert 2 positions, creating a persistent 3-position
        // frame offset. The 100 Hz P0 Marker (absorbed by _skip100HzP0UntilTick below) will
        // update _lastNonTickPulseTick again when it fires ~800 ms from now — which is
        // fine because the gap for bit [01] then becomes only 0.2 s (no fill needed).
        _lastNonTickPulseTick = now;

        // The concurrent 100 Hz P0 Marker arrives up to ~20 ms later. Set the absorb
        // window so it is treated as P0 confirmation, not stored as bit 1.
        _skip100HzP0UntilTick = now + SecondsToTicks(0.1);

        // Clear the saturation gate and tick fade — the minute pulse is definitive
        // evidence that the 1000 Hz channel is above noise and the signal is present.
        if (_signalTooFaded)
        {
            _recentMarkerFlags.Clear();
            _signalTooFaded = false;
        }
        if (TickFadeActive)
        {
            TickFadeActive = false;
            _onLog?.Invoke("Tick fade cleared by minute pulse");
        }
        _lastSecondTickTimestamp = now;  // minute pulse counts as a tick arrival

        if (_state != State.Locked)
        {
            _consecutiveInvalid = 0;
            bool wasSearching = _state != State.Syncing;
            _state = State.Syncing;
            if (wasSearching)
            {
                _lockQuality = 0.20;
                _onLog?.Invoke($"Minute pulse → P0 anchor ({tick.WidthSeconds:F3}s) → SYNCING");
            }
            else
            {
                _lockQuality = Math.Min(1.0, _lockQuality + 0.05);
                _onLog?.Invoke($"Minute pulse → P0 re-anchor ({tick.WidthSeconds:F3}s)");
            }
        }
        else
        {
            // Locked: new frame started. Stay Locked — only re-anchor if noticeably late.
            string note = prevIndex > 2 ? $" [was at [{prevIndex:D2}], re-anchored]" : string.Empty;
            _onLog?.Invoke($"Minute pulse → P0 anchor ({tick.WidthSeconds:F3}s){note}");
        }

        ReportStatus(_lastSignalPercent);
        PublishFrameVisualization();
    }

    private void StoreBit(PulseEvent pulse)
    {
        if (_bitIndex < 60)
        {
            _bits[_bitIndex] = pulse.Type switch
            {
                PulseType.Marker => 2,
                PulseType.One    => 1,
                _                => 0
            };
            // Confident when both classifiers agree. Disagreement means the matched
            // filter and edge-timing reached different conclusions. The matched filter
            // (energy integral) is the more reliable measurement; raw duration is biased
            // by envelope rise/fall time and noise-induced threshold re-crossings.
            bool classifiersAgree = !pulse.MatchedType.HasValue
                || pulse.MatchedType == pulse.DurationType;
            _bitConfident[_bitIndex] = classifiersAgree || pulse.MatchedType.HasValue;

            // Soft-decision weight:
            //   Agree: full confidence score (distance from nearest boundary).
            //   Disagree but matched filter present: half weight — matched filter energy
            //     integral is reliable; raw duration was inflated (noise re-entry, envelope
            //     transition). Half weight lets the vote participate without dominating.
            //   No matched filter at all: erasure (0.0) — no reliable measurement.
            _bitWeight[_bitIndex] = pulse.MatchedType.HasValue
                ? (classifiersAgree ? pulse.Confidence : pulse.Confidence * 0.5)
                : (classifiersAgree ? pulse.Confidence : 0.0);

            // Structural confidence: if the stored value contradicts known WWV structure,
            // override to not-confident and zero-weight regardless of classifier agreement.
            //   — non-Marker at a known marker position
            //   — Marker at a known non-marker position (spurious Marker at reserved/data bit)
            //   — non-zero at a reserved position (WWV always transmits 0 there)
            bool structurallyExpected = pulse.Type == PulseType.Marker
                ? IsExpectedMarkerPosition(_bitIndex)
                : !IsExpectedMarkerPosition(_bitIndex);
            if (!structurallyExpected)
            {
                _bitConfident[_bitIndex] = false;
                _bitWeight[_bitIndex]    = 0.0;
            }
            if (IsReservedPosition(_bitIndex) && _bits[_bitIndex] != 0)
            {
                _bitConfident[_bitIndex] = false;
                _bitWeight[_bitIndex]    = 0.0;
            }

            // Tick-based fade override: if 2+ consecutive 1000 Hz ticks are missing,
            // zero the weight regardless of matched-filter confidence. The tick channel
            // provides independent confirmation that the signal is absent; a BCD pulse
            // arriving during a tick fade is probably a noise artefact or multipath
            // remnant, not genuine data.
            if (TickFadeActive)
            {
                _bitConfident[_bitIndex] = false;
                _bitWeight[_bitIndex]    = 0.0;
            }

            _bitIndex++;
            _lastStoredPulseType = pulse.Type;
            PublishFrameVisualization();
        }
    }

    private void TryDecode(double signalPercent)
    {
        // Update per-bit accumulator from this frame's 100 Hz pulse measurements.
        // Confident bits (classifiers agreed, not fade-zeroed) push the accumulator
        // toward ±1 with strength proportional to the matched-filter confidence.
        // Erasures apply a slow decay (×15/16) matching the NIST d=16 comb filter rate so
        // evidence from clean frames persists through multi-minute fades before falling below
        // the vote threshold (half-life ≈ 11 min vs ≈ 7 min with the old ×0.90 rate).
        for (int i = 0; i < 60; i++)
        {
            if (IsExpectedMarkerPosition(i) || IsReservedPosition(i)) continue;
            if (_bitConfident[i] && _bitWeight[i] > 0)
            {
                // Slow bits with a known persistent-store value get a smaller alpha cap.
                // With alpha=0.60 a single confident-wrong pulse (weight ~0.9) pushes acc
                // from 0 → 0.54, exceeding the 0.15 vote threshold and bypassing the store.
                // At alpha=0.10 the same pulse moves acc to 0.09 — below threshold — so the
                // persistent store stays authoritative until several frames consistently agree.
                bool isProtectedSlowBit = SlowBitPositions.Contains(i) && _persistentBits[i] >= 0;
                double alpha = isProtectedSlowBit
                    ? Math.Min(0.10, _bitWeight[i])
                    : Math.Min(0.60, _bitWeight[i]);
                double target = _bits[i] == 1 ? +1.0 : -1.0;
                _bitAccumulator[i] += alpha * (target - _bitAccumulator[i]);
            }
            else
            {
                _bitAccumulator[i] *= (15.0 / 16.0); // slow decay — NIST d=16 rate
            }
        }

        // Reset per-frame counters for next minute
        int frameHits = _frameHits, frameTotal = _frameTotal;
        _frameHits = 0; _frameTotal = 0;

        // Produce voted bit array from per-bit accumulator + persistent store fallback.
        var (votedBits, persistFallbacks, structFallbacks, minMargin, minMarginPos) = VoteBits();

        // Dump the full 60-bit frame for diagnostics (raw, then voted).
        // 0=zero, 1=one, 2=marker(M). Lowercase = not confident (erasure).
        if (_onLog != null)
        {
            var chars = new char[60];
            for (int i = 0; i < 60; i++)
            {
                char c = _bits[i] switch { 2 => 'M', 1 => '1', _ => '0' };
                chars[i] = _bitConfident[i] ? c : char.ToLower(c);
            }
            int erased = _bitConfident.Count(c => !c);
            string hitNote = frameTotal > 0 ? $"  hits={frameHits}/{frameTotal}" : string.Empty;
            _onLog($"Frame: {new string(chars)}  [{erased} erased{hitNote}]");

            var votedChars = new char[60];
            for (int i = 0; i < 60; i++)
                votedChars[i] = votedBits[i] switch { 2 => 'M', 1 => '1', _ => '0' };
            bool differs = false;
            for (int i = 0; i < 60; i++)
                if (votedBits[i] != _bits[i]) { differs = true; break; }
            // Show accumulator strength summary: min/mean/max over data bits
            double[] dataAcc = _bitAccumulator
                .Where((_, j) => !IsExpectedMarkerPosition(j) && !IsReservedPosition(j))
                .ToArray();
            int strongPos = dataAcc.Count(a => Math.Abs(a) >= 0.5);
            int weakPos   = dataAcc.Count(a => Math.Abs(a) < 0.15);
            _onLog($"Voted: {new string(votedChars)}{(differs ? " (corrected)" : "")}  " +
                   $"[acc strong={strongPos} weak={weakPos}]");

            var markers = new List<int>();
            for (int i = 0; i < 60; i++)
                if (votedBits[i] == 2) markers.Add(i);
            int spurious = markers.Count(p => !IsExpectedMarkerPosition(p));
            string spuriousNote = spurious > 0 ? $"  ({spurious} spurious)" : string.Empty;
            _onLog($"Markers at: [{string.Join(", ", markers)}]  (expected: 0,9,19,29,39,49,59){spuriousNote}");

            var segErased = new int[6];
            for (int i = 0; i < 60; i++)
                if (!_bitConfident[i]) segErased[i / 10]++;
            _onLog($"Erasures/seg: {string.Join("  ", segErased.Select((e, j) => $"[{j * 10:D2}]={e}"))}");

            if (persistFallbacks > 0 || structFallbacks > 0)
                _onLog($"Vote fallbacks: {persistFallbacks} persistent-store, {structFallbacks} structural-default");
            if (minMarginPos >= 0)
                _onLog($"Weakest vote: bit [{minMarginPos:D2}] acc={_bitAccumulator[minMarginPos]:+0.000;-0.000}  " +
                       $"(|acc|>0.5 solid, <0.15 marginal)");
        }

        // BCD-constrained post-processing: verify each BCD digit group is in its valid range
        // and correct single-bit overflows by clearing the highest-weight contributing bit.
        // This catches cases where a marginal high-weight vote flips a digit from 5→6 (minutes
        // tens), 2→3 (hours tens), or 9→10+ (any units group) — invalid BCD that BcdDecoder
        // would reject outright, but recoverable with one bit correction.
        int bcdCorrections = ApplyBcdConstraints(votedBits);
        if (bcdCorrections > 0)
            _onLog?.Invoke($"BCD constraints: {bcdCorrections} bit(s) corrected");

        var frame = BcdDecoder.Decode(votedBits);
        _bitIndex = 0;

        // Date hint validation: if a known UTC date is set, reject any frame whose
        // decoded date falls more than 14 days away. BCD range checks cannot catch a
        // wrong-but-in-range year (e.g. 74 instead of 26) — only a plausibility gate
        // against an external reference can.
        // Widened 7 → 14 days: on weak HF signals the slow-field bits (DOY) are frequently
        // erased. Even with the persistent store weight raised, a cluster of low-confidence
        // live votes can push DOY off by 7–10 days, causing valid-time frames to be rejected.
        // 14 days still blocks clearly-wrong years while surviving marginal DOY errors.
        if (frame != null && _knownDateUtc.HasValue)
        {
            double dayDiff = Math.Abs((frame.UtcTime.Date - _knownDateUtc.Value).TotalDays);
            if (dayDiff > 14.0)
            {
                _onLog?.Invoke($"Date mismatch: decoded {frame.UtcTime:yyyy-MM-dd} " +
                               $"is {dayDiff:F0} days from expected {_knownDateUtc.Value:yyyy-MM-dd} — frame rejected");

                // The persistent store year/DOY bits are clearly stale — wipe them so
                // subsequent frames fall back to the auto-seeded value rather than
                // perpetuating the wrong year on every frame.
                foreach (int pos in (int[])[4, 5, 6, 7, 51, 52, 53, 54,             // year
                                            30, 31, 32, 33, 35, 36, 37, 38, 40, 41]) // DOY
                    _persistentBits[pos] = -1;

                // Re-seed from the known date so the next frame has a correct year/DOY
                // starting point without waiting for a clean live-signal frame.
                SetKnownDate(_knownDateUtc.Value);

                // Wipe year+DOY accumulator entries and re-seed from the known-date persistent
                // store so the next frame starts from a known value rather than zero.
                // Starting at zero allows a single measurement to immediately exceed the 0.15
                // threshold; seeding to ±0.4 means incoming evidence must overcome real resistance.
                int[] yearDoyPositions = [4, 5, 6, 7, 51, 52, 53, 54,             // year
                                          30, 31, 32, 33, 35, 36, 37, 38, 40, 41];  // DOY
                foreach (int pos in yearDoyPositions)
                    _bitAccumulator[pos] = _persistentBits[pos] >= 0
                        ? (_persistentBits[pos] == 1 ? 0.4 : -0.4)
                        : 0.0;

                frame = null;
            }
        }

        if (frame != null)
        {
            // Clock advance prediction (Markov validation from NTP driver 36):
            // compare decoded time against the expected time advanced from the
            // prior successful decode. A drift > 30 s means framing has slipped.
            if (_clockExpected.HasValue)
            {
                double drift = (frame.UtcTime - _clockExpected.Value).TotalSeconds;
                if (Math.Abs(drift) <= 30.0)
                {
                    _clockVerifiedCount++;
                    _onLog?.Invoke($"Verified #{_clockVerifiedCount}: {frame.UtcTime:HH:mm} " +
                                   $"(drift {drift:+0.0;-0.0}s from expected)");
                    _clockExpected = frame.UtcTime.AddMinutes(1);
                }
                else
                {
                    // Reject the frame: the decoded time is inconsistent with the
                    // established timeline. Advance _clockExpected by one minute based
                    // on the last good prediction so subsequent frames continue to be
                    // checked against the correct timeline rather than the wrong decoded time.
                    _onLog?.Invoke($"Clock mismatch: expected {_clockExpected.Value:HH:mm} " +
                                   $"got {frame.UtcTime:HH:mm} (drift {drift:+0.0;-0.0}s) — rejected");
                    _clockVerifiedCount = 0;
                    _clockExpected = _clockExpected.Value.AddMinutes(1);
                    frame = null;
                }
            }
            else
            {
                // First decode — no prior reference. Accept unconditionally and
                // establish the baseline for subsequent Markov checks.
                _clockExpected = frame.UtcTime.AddMinutes(1);
            }

            // Update the persistent slow-bit store from this validated frame.
            // Only slow-changing positions are stored (DOY, year, DUT1, DST, leap).
            // Fast-changing positions (minutes, hours) are deliberately excluded so a
            // stale persistent entry never supplies wrong time data.
            foreach (int pos in SlowBitPositions)
                _persistentBits[pos] = votedBits[pos];

            // Advance the known date from the validated frame so the date gate
            // stays current across midnight UTC and end-of-year boundaries.
            _knownDateUtc = frame.UtcTime.Date;

            _consecutiveValid++;
            _consecutiveInvalid = 0;
            _lockQuality = Math.Min(1.0, _lockQuality + 0.2);

            // ConfidenceFrames counts Markov-verified increments, not raw accepted frames.
            // First-frame decodes have ConfidenceFrames=0; the count only rises after
            // the second and subsequent frames each pass the drift-≤30s clock check.
            // The UI gates the hours/minutes display on ConfidenceFrames >= 3.
            frame.ConfidenceFrames = _clockVerifiedCount;
            _latestFrame = frame;
            _state = State.Locked;

            _onFrameDecoded(frame);
        }
        else
        {
            _consecutiveInvalid++;
            _consecutiveValid = 0;
            _lockQuality = Math.Max(0, _lockQuality - 0.3);
            if (_consecutiveInvalid >= 2)
            {
                _state = State.Searching;
                _lockQuality = 0;
            }
        }

        // Advance anchor by 60 s so tick-alignment checks stay accurate in the next frame.
        // The minute-pulse handler overwrites this with the exact P0 wall-clock time whenever
        // a MinutePulse arrives, keeping long-term accuracy without accumulated drift.
        if (_anchorWallTick != 0 && _state != State.Searching)
            _anchorWallTick += SecondsToTicks(60);

        ReportStatus(signalPercent);
    }

    /// <summary>
    /// Called on every audio buffer (~50 ms). Zeros the signal meter if no pulse has
    /// arrived in the last 2 seconds, even when the pulse detector fires no events.
    ///
    /// Also handles saturation gate time-based recovery: if the gate is active but no
    /// pulses have arrived for >20 s, the propagation condition has changed. The stale
    /// Marker readings in the rolling window no longer represent the current signal, so
    /// clearing the queue gives any returning carrier a fresh evaluation without waiting
    /// for 20 new pulses to flush 20 old ones.
    /// </summary>
    public void CheckSignalTimeout()
    {
        long now = _getTimestamp();
        if (_lastPulseTick != 0 && TicksToSeconds(now - _lastPulseTick) > 2.0)
        {
            _lastPulseTick = 0;
            ReportStatus(0);
        }

        if (_signalTooFaded && _lastNonTickPulseTick != 0
            && TicksToSeconds(now - _lastNonTickPulseTick) > 20.0)
        {
            _recentMarkerFlags.Clear();
            _signalTooFaded = false;
            _candidateAnchorTick = 0;
            _onLog?.Invoke("Signal absent >20s — saturation gate reset, resuming anchor search");
        }

        // Receiver mode check: periodically evaluate whether audio is present but the
        // 100 Hz subcarrier is absent — the signature of SSB or CW mode rather than AM.
        // WWV requires AM reception; SSB strips the carrier so nothing will ever decode.
        _receiverModeCheckCount++;
        bool audioPresent    = _lastSignalPercent >= AudioPresentThreshold;
        bool subcarrierAbsent = _subcarrierPercent < SubcarrierAbsentThreshold;
        if (audioPresent && subcarrierAbsent)
            _subcarrierAbsentCount++;

        if (_receiverModeCheckCount >= ReceiverModeCheckInterval)
        {
            double absentFraction = (double)_subcarrierAbsentCount / _receiverModeCheckCount;
            if (absentFraction >= 0.80 && !_receiverModeWarned)
            {
                _receiverModeWarned = true;
                _onLog?.Invoke("[WARNING] Audio signal present but 100 Hz subcarrier absent (>80% of last 10 s). " +
                               "Check that the receiver is in AM mode, not SSB/CW.");
            }
            else if (absentFraction < 0.20 && _receiverModeWarned)
            {
                _receiverModeWarned = false; // subcarrier has returned — clear warning
            }
            _receiverModeCheckCount = 0;
            _subcarrierAbsentCount  = 0;
        }

        // Tick-based fade: count how many expected (non-omitted) second ticks have gone
        // missing since the last received tick. Two or more consecutive missed ticks confirm
        // a genuine HF propagation fade on the 1000 Hz channel.
        // Only active when a P0 anchor is set and the frame is in progress (we need
        // _anchorWallTick to compute which bit positions should have fired ticks).
        if (_anchorWallTick != 0 && _lastSecondTickBit >= 0
            && (_state == State.Syncing || _state == State.Locked))
        {
            double gapSinceLastTick = TicksToSeconds(now - _lastSecondTickTimestamp);
            if (gapSinceLastTick >= 1.3) // at least one full tick interval has elapsed
            {
                // Compute which bit position the clock expects now.
                double elapsedFromAnchor = TicksToSeconds(now - _anchorWallTick);
                int currentBit = (int)Math.Round(elapsedFromAnchor) % 60;

                // Walk every position between the last received tick and the current
                // expected position. Count those that are not NIST-omitted (29/59).
                // If ≥ 2 non-omitted positions have gone missing, declare a tick fade.
                int span = ((currentBit - _lastSecondTickBit) + 60) % 60;
                int expectedMissed = 0;
                for (int i = 1; i <= span; i++)
                {
                    int pos = (_lastSecondTickBit + i) % 60;
                    if (pos != 29 && pos != 59) expectedMissed++;
                }

                if (expectedMissed >= 2 && !TickFadeActive)
                {
                    TickFadeActive = true;
                    _onLog?.Invoke(
                        $"Tick fade: {expectedMissed} ticks missing since [{_lastSecondTickBit:D2}]" +
                        $" (gap={gapSinceLastTick:F1}s)");
                }
            }
        }
    }

    /// <summary>
    /// Called on every audio block with the current 100 Hz demodulated envelope value
    /// and the tracked carrier level.  Implements the three-point bipolar discriminator:
    ///
    ///   After each 1000 Hz second tick at position N, sample the envelope at two offsets:
    ///     sampleA @ ~350 ms: between the Zero (200 ms) and One (500 ms) LOW-period ends.
    ///     sampleB @ ~650 ms: between the One (500 ms) and Marker (800 ms) LOW-period ends.
    ///
    ///   HIGH at 350 ms → Zero  (carrier already returned)
    ///   LOW at 350 ms + HIGH at 650 ms → One  (carrier returned between 350–650 ms)
    ///   Both LOW → erasure (Marker or fade — carrier still absent at 650 ms)
    ///
    /// Updates _bitAccumulator[N] directly, providing independent evidence separate from
    /// the 100 Hz threshold-crossing measurement already done by PulseDetector.
    /// </summary>
    public void FeedEnvelope(double envelope, double levelHigh, long timestamp)
    {
        if (_envTickBitPos < 0) return;
        if (_state != State.Syncing && _state != State.Locked) return;

        double elapsed = TicksToSeconds(timestamp - _envTickTimestamp);

        // Sample A: first crossing point at ~350 ms
        if (!_envGotA && elapsed >= 0.35)
        {
            _envSampleA = envelope;
            _envGotA    = true;
            return;
        }

        // Sample B: second crossing point at ~650 ms — classify and update accumulator
        if (_envGotA && elapsed >= 0.65)
        {
            int bitPos = _envTickBitPos;
            _envTickBitPos = -1;
            _envGotA       = false;

            // Need a valid carrier reference to threshold against.
            // Guard: if levelHigh is too low the ratio is meaningless (signal absent).
            if (levelHigh < 1e-6) return;

            double threshold = levelHigh * 0.5;
            bool aHigh = _envSampleA > threshold;
            bool bHigh = envelope    > threshold;

            // Alpha cap: slow bits with a known persistent-store value are protected against
            // single large-swing measurements — same policy as TryDecode.
            bool isProtectedSlowBit = SlowBitPositions.Contains(bitPos) && _persistentBits[bitPos] >= 0;
            double alpha = isProtectedSlowBit ? 0.10 : 0.50;

            if (aHigh)
            {
                // Carrier HIGH at 350 ms → bit is Zero (carrier returned within 200 ms LOW)
                _bitAccumulator[bitPos] += alpha * (-1.0 - _bitAccumulator[bitPos]);
            }
            else if (bHigh)
            {
                // Carrier LOW at 350 ms, HIGH at 650 ms → bit is One (500 ms LOW period)
                _bitAccumulator[bitPos] += alpha * (+1.0 - _bitAccumulator[bitPos]);
            }
            // Both LOW → Marker or fade erasure — no accumulator update
            // (the accumulator decays in TryDecode when the 100 Hz channel also erases it)
        }
    }

    private void ReportStatus(double signalPercent = 0)
    {
        var lockState = _state switch
        {
            State.Locked    => LockState.Locked,
            State.Syncing   => LockState.Syncing,
            _               => LockState.Searching
        };

        int remaining = (_state == State.Syncing || _state == State.Locked)
            ? 60 - _bitIndex
            : 0;

        _onSignalUpdate(new SignalStatus
        {
            SignalStrengthPercent      = signalPercent,
            SubcarrierStrengthPercent  = _subcarrierPercent,
            LockStrengthPercent        = _lockQuality * 100.0,
            LockState                  = lockState,
            FrameSecondsRemaining      = remaining,
            ReceiverModeAlert          = _receiverModeWarned
                ? "No 100 Hz subcarrier — check AM mode (not SSB/CW)"
                : null
        });
    }

    /// <summary>
    /// Checks two structural invariants after each bit is stored.
    /// Returns true and resets to Searching if the frame is provably corrupt.
    ///
    /// 1. Consecutive Markers: no valid WWV frame ever has two adjacent Marker bits
    ///    (minimum marker-to-marker separation is 9 seconds). A run of consecutive
    ///    Markers is the signature of sustained HF fades mis-classified as Markers.
    ///
    /// 2. Progressive marker check: at every 10-second boundary (positions 9, 19, 29,
    ///    39, 49) the expected position marker must be present. This catches misalignment
    ///    within 10 seconds instead of waiting 60 seconds for TryDecode to fail.
    /// </summary>
    private bool CheckFrameCorrupted(double signalPercent)
    {
        // Check 1: two consecutive Markers → impossible in any valid frame.
        // Before resetting, check if either marker is structurally wrong (at a non-marker
        // position) and not-confident. If so, correct it to 0 and continue — a spurious
        // Marker at a data/reserved position is recoverable; true misalignment is not.
        // Examine the most-recently-stored bit first (most likely the spurious one).
        if (_bitIndex >= 2 && _bits[_bitIndex - 1] == 2 && _bits[_bitIndex - 2] == 2)
        {
            int posB = _bitIndex - 1;
            int posA = _bitIndex - 2;
            if (!IsExpectedMarkerPosition(posB) && !_bitConfident[posB])
            {
                _onLog?.Invoke($"Correcting spurious marker at non-marker pos {posB:D2} → 0");
                _bits[posB] = 0;
                _bitCorrected[posB] = true;
                PublishFrameVisualization();
            }
            else if (!IsExpectedMarkerPosition(posA) && !_bitConfident[posA])
            {
                _onLog?.Invoke($"Correcting spurious marker at non-marker pos {posA:D2} → 0");
                _bits[posA] = 0;
                _bitCorrected[posA] = true;
                PublishFrameVisualization();
            }
            else
            {
                _onLog?.Invoke($"Consecutive markers at [{posA:D2}][{posB:D2}] → SEARCHING");
                ResetToSearching(signalPercent);
                return true;
            }
        }

        // Check 2: expected marker must be present at each 10-second boundary.
        // Only check positions 9, 19, 29, 39, 49 (not 59 — that's handled by TryDecode).
        if (_bitIndex is 10 or 20 or 30 or 40 or 50)
        {
            int markerPos = _bitIndex - 1;
            if (_bits[markerPos] != 2)
            {
                if (!_bitConfident[markerPos])
                {
                    // Not-confident bit at a known marker position: the two classifiers
                    // disagreed (e.g. duration=Marker, matched=One). We know a priori that
                    // this position is always a Marker — correct it and continue rather than
                    // discarding up to 20 seconds of valid collection.
                    _onLog?.Invoke($"Correcting ambiguous bit at marker pos {markerPos} " +
                                   $"({_bits[markerPos]} → M, classifiers disagreed)");
                    _bits[markerPos]         = 2;
                    _bitConfident[markerPos] = false; // remains erasure — voted with caution
                    _bitCorrected[markerPos] = true;
                    PublishFrameVisualization();
                }
                else
                {
                    // Confident wrong value at a marker position — this is a real misalignment.
                    _onLog?.Invoke($"Missing marker at pos {markerPos} (got {_bits[markerPos]}) → SEARCHING");
                    ResetToSearching(signalPercent);
                    return true;
                }
            }
        }

        return false;
    }

    private void ResetToSearching(double signalPercent)
    {
        _state = State.Searching;
        _lockQuality = 0;
        _bitIndex = 0;
        Array.Clear(_bitConfident, 0, 60);
        Array.Clear(_bitWeight,    0, 60);
        Array.Clear(_bitGapFilled, 0, 60);
        Array.Clear(_bitCorrected, 0, 60);
        _frameHits = 0;
        _frameTotal = 0;
        _clockExpected = null;
        _clockVerifiedCount = 0;
        _candidateAnchorTick      = 0;
        _candidateP0OffsetSeconds = 0;
        _skip100HzP0UntilTick     = 0;
        _lastPulseWasSynthetic    = false;
        TickFadeActive = false;
        _envTickBitPos = -1;
        _envGotA       = false;
        // Keep _bitAccumulator: evidence from prior clean frames should survive re-anchors.
        // Keep _signalTooFaded and _recentMarkerFlags: signal quality, not frame state.
        ReportStatus(signalPercent);
        PublishFrameVisualization();
    }

    private static bool IsExpectedMarkerPosition(int pos) =>
        pos is 0 or 9 or 19 or 29 or 39 or 49 or 59;

    // WWV always transmits 0 at these positions — any non-zero value is structural noise.
    private static bool IsReservedPosition(int pos) =>
        pos is 1 or 8 or 14 or 18 or 24 or 27 or 28 or 34 or 42 or 43 or 44 or 45 or 46 or 47 or 48;

    /// <summary>
    /// Builds a snapshot of the current 60-bit frame state and fires the visualization
    /// callback. Positions beyond _bitIndex are shown as Empty; received positions show
    /// their classification state (Confident / Erased / GapFilled / Corrected).
    /// Called from every path that mutates a bit (StoreBit, gap-fill, CheckFrameCorrupted,
    /// anchor setup, reset) so the UI grid tracks the decoder in real time.
    /// </summary>
    private void PublishFrameVisualization()
    {
        if (_onFrameUpdate == null) return;
        var cells = new FrameCell[60];
        for (int i = 0; i < 60; i++)
        {
            if (i >= _bitIndex)
            {
                cells[i] = new FrameCell(0, FrameCellState.Empty);
            }
            else
            {
                FrameCellState state;
                if      (_bitGapFilled[i]) state = FrameCellState.GapFilled;
                else if (_bitCorrected[i]) state = FrameCellState.Corrected;
                else if (_bitConfident[i]) state = FrameCellState.Confident;
                else                       state = FrameCellState.Erased;
                cells[i] = new FrameCell(_bits[i], state);
            }
        }
        _onFrameUpdate(cells);
    }

    /// <summary>
    /// Pre-fills the persistent bit store with the day-of-year and year derived from
    /// the supplied UTC date.  The operator calls this when they know today's UTC date
    /// but the signal is too weak to receive those bits reliably.
    ///
    /// Only DOY (positions 22–34) and year (positions 45–53) are written.  Minutes and
    /// hours are intentionally excluded — those change every minute and must come from
    /// the live signal.  DUT1, DST, and leap bits are also excluded; if they matter the
    /// operator should wait for a good frame decode to populate them.
    ///
    /// The values are overwritten by the first successfully validated frame decode, so
    /// entering the wrong date does not permanently corrupt the decoder.
    /// </summary>
    public void SetKnownDate(DateTime utcDate)
    {
        int year = utcDate.Year % 100;   // WWV encodes a 2-digit year (00–99)
        int doy  = utcDate.DayOfYear;

        EncodeIntoPersistentBits(year, [4, 5, 6, 7, 51, 52, 53, 54],
                                       [1, 2, 4, 8, 10, 20, 40, 80]);
        EncodeIntoPersistentBits(doy,  [30, 31, 32, 33, 35, 36, 37, 38, 40, 41],
                                       [1, 2, 4, 8, 10, 20, 40, 80, 100, 200]);

        _knownDateUtc = utcDate.Date;

        // Pre-seed the accumulator so the persistent store is immediately authoritative.
        // Without this, the accumulator starts at 0.0 after a date entry and any single
        // incoming measurement (weight ~0.9 → alpha 0.10 → shift +0.09) stays below the
        // 0.15 threshold — but only barely. Seeding to ±0.4 gives a comfortable margin so
        // the live signal must accumulate several consistent frames before overriding the hint.
        foreach (int pos in (int[])[4, 5, 6, 7, 51, 52, 53, 54,             // year
                                    30, 31, 32, 33, 35, 36, 37, 38, 40, 41])  // DOY
            _bitAccumulator[pos] = _persistentBits[pos] == 1 ? 0.4 : -0.4;

        _onLog?.Invoke($"Operator date applied: {utcDate:yyyy-MM-dd} UTC  " +
                       $"(year={year:D2}, DOY={doy:D3}) — year+DOY bits pre-filled, accumulators seeded");
    }

    /// <summary>
    /// Removes the operator-supplied date hint from the persistent store.
    /// Positions revert to -1 (unknown) until the next successful frame decode.
    /// </summary>
    public void ClearKnownDate()
    {
        int[] datePositions = [30, 31, 32, 33, 35, 36, 37, 38, 40, 41,   // DOY
                                4, 5, 6, 7, 51, 52, 53, 54];              // year
        foreach (int pos in datePositions)
            _persistentBits[pos] = -1;
        _knownDateUtc = null;
        _onLog?.Invoke("Operator date cleared — year+DOY bits reset to unknown");
    }

    /// <summary>
    /// Encodes <paramref name="value"/> using BCD weights into <c>_persistentBits</c>.
    /// The encoding is greedy from the largest weight down — identical to how BcdDecoder
    /// sums weighted bits to recover the original value.
    /// </summary>
    private void EncodeIntoPersistentBits(int value, int[] positions, int[] weights)
    {
        int remaining = value;
        for (int i = positions.Length - 1; i >= 0; i--)
        {
            if (remaining >= weights[i])
            {
                _persistentBits[positions[i]] = 1;
                remaining -= weights[i];
            }
            else
            {
                _persistentBits[positions[i]] = 0;
            }
        }
    }

    // Reads the per-bit accumulator to produce a voted 60-bit array.
    // Tier 1: |accumulator| ≥ 0.15 → sign determines the vote (1 or 0).
    // Tier 2: accumulator weak or absent + persistent slow-bit store known → use store.
    // Tier 3: structural default (markers → Marker, data/reserved → 0).
    // Returns diagnostics for logging:
    //   persistFallbacks — bits decided by persistent store, not accumulator
    //   structFallbacks  — bits decided by structural default (no evidence anywhere)
    //   minMargin        — |accumulator| value at the weakest contested data bit
    //   minMarginPos     — bit position with the weakest accumulator magnitude
    private (int[] voted, int persistFallbacks, int structFallbacks, double minMargin, int minMarginPos) VoteBits()
    {
        const double AccThreshold = 0.15; // minimum |acc| to override fallbacks

        var voted = new int[60];
        int persistFallbacks = 0, structFallbacks = 0;
        double minMargin = double.MaxValue;
        int minMarginPos = -1;

        for (int i = 0; i < 60; i++)
        {
            // Marker and reserved positions are structurally determined — never voted.
            if (IsExpectedMarkerPosition(i)) { voted[i] = 2; continue; }
            if (IsReservedPosition(i))       { voted[i] = 0; continue; }

            double acc = _bitAccumulator[i];

            if (Math.Abs(acc) >= AccThreshold)
            {
                // Tier 1: accumulator has sufficient evidence.
                voted[i] = acc > 0 ? 1 : 0;

                // Track weakest accumulator magnitude among decided data bits.
                if (Math.Abs(acc) < minMargin)
                {
                    minMargin    = Math.Abs(acc);
                    minMarginPos = i;
                }
            }
            else if (_persistentBits[i] >= 0)
            {
                // Tier 2: accumulator is weak/absent but persistent store has a known value.
                voted[i] = _persistentBits[i] == 1 ? 1 : 0;
                persistFallbacks++;
            }
            else
            {
                // Tier 3: no evidence at all — structural default is 0.
                voted[i] = 0;
                structFallbacks++;
            }
        }

        if (minMargin == double.MaxValue) minMargin = 1.0;
        return (voted, persistFallbacks, structFallbacks, minMargin, minMarginPos);
    }

    /// <summary>
    /// Clamps each BCD digit group in the voted bit array to its valid decimal range.
    /// WWV BCD values are multi-digit decimal numbers where each decimal digit must be 0–9
    /// (and some groups have tighter limits: minutes tens 0–5, hours tens 0–2, etc.).
    /// A single high-weight bit error can push a digit out of range — this method corrects
    /// such errors by clearing the highest-weight set bit that causes the overflow.
    /// Returns the number of bits corrected.
    /// </summary>
    private static int ApplyBcdConstraints(int[] bits)
    {
        int corrections = 0;

        // Each entry: (positions[], weights[], maxValue)
        // maxValue is the maximum valid SUM from that group's bits.
        (int[] positions, int[] weights, int max)[] groups =
        [
            ([10, 11, 12, 13], [1,  2,  4, 8 ],  9),  // minutes units  0–9
            ([15, 16, 17     ], [10, 20, 40    ], 50),  // minutes tens   0–50 (digit 0–5)
            ([20, 21, 22, 23], [1,  2,  4, 8 ],  9),  // hours units    0–9
            ([25, 26         ], [10, 20        ], 20),  // hours tens     0–20 (digit 0–2)
            ([30, 31, 32, 33], [1,  2,  4, 8 ],  9),  // DOY units      0–9
            ([35, 36, 37, 38], [10, 20, 40, 80], 90),  // DOY tens       0–90 (digit 0–9)
            ([4,  5,  6,  7 ], [1,  2,  4, 8 ],  9),  // year units     0–9
            ([51, 52, 53, 54], [10, 20, 40, 80], 90),  // year tens      0–90 (digit 0–9)
        ];

        foreach (var (positions, weights, max) in groups)
        {
            int value = 0;
            for (int i = 0; i < positions.Length; i++)
                if (bits[positions[i]] == 1) value += weights[i];

            if (value <= max) continue;

            // Clear the highest-weight set bit(s) until the digit is within range.
            for (int i = positions.Length - 1; i >= 0 && value > max; i--)
            {
                if (bits[positions[i]] == 1)
                {
                    bits[positions[i]] = 0;
                    value -= weights[i];
                    corrections++;
                }
            }
        }

        return corrections;
    }

    private static double TicksToSeconds(long ticks) =>
        (double)ticks / Stopwatch.Frequency;

    private static long SecondsToTicks(double seconds) =>
        (long)(seconds * Stopwatch.Frequency);
}
