# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Overview

**RadioTime Decoder** (WwvDecoder) is a Windows desktop application (.NET 9 WPF) that decodes UTC time from HF radio time-signal stations (WWV, WWVH, BPM) by demodulating a 100 Hz BCD-encoded subcarrier and validating against 1000 Hz second ticks. The application can optionally set the Windows system clock via SetSystemTime().

The project is highly specialized: it implements signal processing (synchronous lock-in detection, pulse classification, fade detection), frame-level state machines (searching/syncing/locked), probabilistic voting (per-bit accumulators with exponential moving averages and soft BCD constraint scoring), and wall-clock Markov verification to detect and correct clock drift.

## Build and Test

```bash
# Build the project (requires .NET 9 SDK)
dotnet build

# Run all tests
dotnet test

# Run a single test file
dotnet test --filter ClassName=SimulationTests

# Run a specific test
dotnet test --filter Name=CleanSignal_DecodesCorrectTime_WithinFourMinutes

# Publish as a single self-contained .exe (~185 MB)
dotnet publish -c Release

# Output: bin/Release/net9.0-windows/win-x64/publish/WwvDecoder.exe
```

## Project Structure

### Core DSP Pipeline (Dsp/)
The signal processing chain processes audio in 50 ms blocks (22,050 Hz, 16-bit mono):

1. **InputAgc** — automatic gain control (3 s attack, 5 s decay) normalizing to 25% full scale
2. **HighpassFilter** — 2nd-order Butterworth, 20 Hz cutoff (removes DC and hum)
3. **NotchFilter** (×2) — 60 Hz and 120 Hz (eliminates US power-line interference)
4. **SynchronousDetector** — coherent IQ lock-in detector at 100 Hz subcarrier:
   - Demodulates in-phase and quadrature, lowpass-filters each to ~2 Hz
   - Envelope = 2√(I²+Q²) — phase-independent amplitude extraction
   - Adaptive: widens to 8 Hz during HF fading (tracked by IsAmplitudeUnstable flag)
   - 15–25 dB SNR improvement over bandpass + rectifier
5. **PulseDetector** — tick-anchored positive-pulse detection:
   - Tracks 75th-percentile of last 30 inter-pulse carrier peaks (rejects outliers)
   - Enters at 55% of real-time IIR HIGH level; exits at 62% (7% hysteresis)
   - Fade detection: triggers when envelope < 15% of stable reference for >200 ms
6. **MatchedFilter** — classifies pulses by HIGH-period duration:
   - Counts samples > 50% of midpoint threshold (eliminates rise/fall time bias)
   - Classifications: <50ms=Tick, 50–350ms=Zero, 350–650ms=One, ≥650ms=Marker
7. **TickDetector** — 1000 Hz IQ demodulator (parallel to 100 Hz channel):
   - Resolves 5 ms second ticks and 800 ms minute pulse
   - Classifies: ≤50ms=SecondTick, ≥700ms=MinutePulse

**Key insight:** No carrier PLL is used because the 100 Hz subcarrier is amplitude-keyed (on/off), not frequency-modulated. The frequency in AM-demodulated baseband audio is exact by definition (sourced from NIST atomic standard).

### Frame Decoder (Decoder/)

**FrameDecoder** implements a three-state machine:

1. **Searching** — watches for a valid P0 anchor (two paths):
   - **Path 1:** 1000 Hz minute pulse detected directly (preferred, ~800 ms burst at second 0)
   - **Path 2:** P0→P1 gap confirmation from 100 Hz pulses (9-second gap is unique; all other marker gaps are 10 seconds) — used if 1000 Hz unavailable
   - **Saturation gate:** pauses anchoring if >60% of last 20 pulses are Markers (deep HF fade)

2. **Syncing** — collecting bits, validating frame structure:
   - Frame integrity checks: rejects consecutive Markers (impossible), missing markers at positions 9/19/29/39/49
   - Gap filling: blackouts 2–30 s fill missing bits with defaults (markers→2, data→0)
   - Cross-frame seeding: after each Markov-verified frame, pre-seeds next minute's hours/minutes at ±0.4

3. **Locked** — decoding a full 60-second frame every minute

**Per-bit accumulator voting:**
- Each of 60 bit positions carries a signed evidence score [−1.0, +1.0]
- Updated via exponential moving average (α ≤ 0.60 for hours/minutes; α ≤ 0.10 for slow fields)
- Erasures apply ×15/16 decay (half-life ~11 minutes)
- Vote threshold |acc| ≥ 0.15; fallback: persistent store → structure default (markers/zeros)

**Soft BCD constraint scoring:**
- Before hard voting, enumerates all valid BCD values (minutes: 60 values, hours: 24, DOY: 366, year: 100)
- Scores each candidate against accumulator: reward agreements, penalize disagreements
- Selects highest-scoring valid value (resolves marginal single-bit cases)

**Three-point bipolar discriminator:**
- After each second tick, samples 100 Hz envelope at ~350ms and ~650ms independently
- Both LOW → Zero; HIGH/LOW → One; both HIGH → Marker erasure
- Provides a second measurement path during partial fades (α = 0.50)

**Wall-clock Markov verification:**
- After first decode, anchors decoded UTC time and wall-clock time
- Each subsequent frame: `expected = anchor + round(elapsed_real_time)`
- Drift ≤30s: accepted, confidence incremented
- Drift >30s: rejected for time display, but date fields still update
- UTC offset fast-path: detects ±1h local/UTC confusion (requires 2 consecutive agreeing frames before re-anchoring)
- Self-correction: 3 consecutive Markov-failing frames with consistent +1-minute sequence trigger re-anchor

### Supporting Components

- **BcdDecoder** — parses 60-bit frame into BCD fields (minutes, hours, DOY, year, DUT1, DST, leap-second)
- **TimeFrame** — holds decoded result with per-field confidence flags (SlowFieldsConfident, HoursMinutesConfident)
- **SignalStatus** — reports signal strength, lock state, saturation
- **DecoderPipeline** — wires DSP chain → FrameDecoder, invokes callbacks for signal updates, decoded frames, and logging
- **SystemTimeSetter** — P/Invoke wrapper for Windows SetSystemTime() (requires Administrator)
- **FileLogger** — thread-safe daily log files to `%APPDATA%\WwvDecoder\`

### UI (MainWindow.xaml, MainViewModel)

MVVM pattern:
- **MainViewModel** — application logic, signal metering, display gating, audio device enumeration
- **Converters** — WPF value converters (e.g. confidence bars, dB readout)
- Dark-themed UI with real-time signal meters, frame countdown, lock quality indicator

## Key Implementation Notes

### Signal Processing

- **No highpass AGC feedback:** AGC is applied before filtering but not to the synchronous detector's envelope. AGC on the envelope would restore LOW-period power reduction that encodes the time bits.
- **Minute-boundary recovery:** At minute pulse, AGC level is restored to a pre-pulse snapshot (captured at second-59 tick), and the percentile carrier-reference window is cleared. Prevents stale reference from producing incorrect threshold.
- **Tick-index safety clamp:** Negative tick indices (caused by future-skewed anchors) are clamped to `tickBitRaw + 60` to prevent subsequent pulses from being discarded.

### Frame Decoding

- **Accumulator persistence:** Not cleared on P0 re-anchor. Evidence from prior clean frames survives minute-boundary fades.
- **Persistent slow-bit store:** 27 positions (DOY, year, DUT1, DST, leap-second) retained from last BCD-valid frame; positions are only overwritten after several consistent disagreements (α ≤ 0.10).
- **Per-field display gating:** Date/DUT1/DST update immediately on first BCD-valid frame (SlowFieldsConfident). Time display requires Markov verification + Confidence ≥ 2/2.

### Testing

Tests use **xUnit** with synthetic signal generation:

- **SimulationTests** — end-to-end: generate a clean (or noisy) synthetic WWV signal from WwvSignalGenerator, feed it to DecoderPipeline, verify decoded time matches expected
- **BcdDecoderTests** — unit tests for BCD bit→integer conversion
- **MatchedFilterTests** — unit tests for pulse classification boundaries
- **DiagnosticTests** — miscellaneous decoder invariants

**Simulation clock:** Tests use a fake `getTimestamp` callback that advances proportional to samples rendered, not real time. Prevents simulated Stopwatch gaps from being milliseconds instead of 10 seconds.

## Common Workflows

### Adding a DSP filter or detector

1. Create a new class in Dsp/ (e.g., `MyDetector.cs`)
2. Follow the pattern: `Process(float[] samples)` → updates internal state, fires event callbacks if needed
3. Add to **DecoderPipeline** wiring (constructor) and **ProcessSamples** call sequence
4. If a new event callback is needed, add it to DecoderPipeline and MainViewModel

### Tuning DSP parameters

Parameters are scattered across multiple classes (InputAgc attack/decay, notch bandwidth, lowpass cutoff, thresholds, etc.). Each is documented with its purpose and typical value:

- **InputAgc** — `AttackTau`, `DecayTau`, `TargetLevel` (currently 3s/5s/0.25)
- **SynchronousDetector** — `lowpassNominal`, `lowpassFading` (currently 2 Hz / 8 Hz)
- **PulseDetector** — `EnterThreshold`, `ExitThreshold` ratios; `FadeThreshold`, `FadeMinDuration`
- **MatchedFilter** — classification boundaries (currently <50ms / 50–350ms / 350–650ms / ≥650ms)
- **TickDetector** — 1000 Hz lowpass (currently 150 Hz)

### Debugging frame decoding

The activity log is comprehensive. Key log signatures:

- `Searching` / `Syncing N s` / `Locked` — state machine progress
- `bits: ...` — frame bit display (uppercase=confident, lowercase=erased, numbers=gap-filled)
- `P0 anchor confirmed` / `P0 detected` — anchor events
- `Verified #N: HH:MM` — Markov verification passed
- `Clock mismatch: expected HH:MM got HH:MM` — Markov verification failed
- `UTC offset confirmed (+1 h)` — local/UTC confusion detection
- `Gap filled: N bits` — blackout recovery
- `Operator date applied` — user-supplied date hint used

### Adding a new station

1. Edit **StationsDatabase.cs** — add entry to the database array
2. UI automatically populates from StationsDatabase; no hardcoding needed

## Development Notes

- **Nullable types enabled** — null safety is enforced; watch for nullable warnings in AudioInputDevice callbacks
- **Implicit usings** — common namespaces (System, System.Linq, System.Collections.Generic) are auto-imported
- **WPF threading** — signal processing runs on audio callback threads; UI updates must marshal to the dispatcher (handled in MainViewModel)
- **P/Invoke (SetSystemTime)** — requires Administrator privilege; declared in SystemTimeSetter via DllImport
- **Self-contained publish** — produces a single .exe with embedded .NET 9 runtime; no separate installation needed

## Architecture Decisions

- **Why exponential moving average instead of ring buffer majority voting?** EMA with slow decay allows clean-frame evidence to persist across fades (~11 min half-life), whereas a fixed ring buffer would lose all history on buffer wraparound. NIST NTP driver 36 uses this approach.
- **Why soft BCD constraint scoring?** Resolves marginal bits that fall between the vote threshold and structural default. Hard thresholding produces invalid BCD digits in ~2% of partial frames; soft scoring recovers them.
- **Why three-point bipolar discriminator?** Provides independent carrier-timing measurement during partial fades that extend past Zero LOW but not One LOW. Threshold-crossing detector misclassifies in these cases, discriminator is correct.
- **Why wall-clock Markov validation instead of per-frame counter?** Handles propagation outages gracefully: missing several frames doesn't cause per-frame counter to stall, whereas wall-clock formula (using real elapsed time) automatically fills gaps.
- **Why 75th percentile carrier reference?** Rejects multipath spikes (high outliers) and HF-faded HIGH periods (low outliers). IIR alone would be distorted by either extreme; percentile window solves both simultaneously.
