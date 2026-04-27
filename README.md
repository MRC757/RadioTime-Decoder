# RadioTime Decoder

> [!WARNING]
> **This program is under active development.**
> Decoding under real HF propagation conditions depends on signal quality — ionospheric fading can corrupt 50–90% of each 60-bit frame, and decoded times may be absent or delayed during poor propagation. The software is shared for development and experimentation purposes. Do not use it as a time reference for anything safety-critical.

<img width="787" height="1345" alt="Screenshot 2026-04-17 222022" src="https://github.com/user-attachments/assets/99a63b3c-f924-4aab-bfe7-5c516873457f" />

A Windows desktop application that decodes UTC time from HF radio time-signal stations (WWV, WWVH, BPM) by processing audio input in real time. Feed it audio from a shortwave receiver or online SDR tuned to a supported station and it will extract the BCD-encoded time, display decoded UTC, and optionally set your system clock.

Built with WPF (.NET 9) and the MVVM pattern. Dark-themed UI with real-time signal metering.

---

## Features

- **Real-time BCD time-code decoding** from the 100 Hz audio subcarrier used by WWV-family stations (NIST IRIG-H positive-pulse format)
- **1000 Hz tick detector** — detects the WWV second ticks and the 800 ms minute pulse on the separate 1000 Hz audio channel; minute pulse directly anchors P0 without waiting for a 9-second inter-marker gap; anchor is back-projected to the exact UTC second-0 epoch so tick-derived bit indices are accurate throughout the frame
- **Coherent synchronous (lock-in) detector** — demodulates the 100 Hz subcarrier with a narrow IQ lowpass (2 Hz nominal, widening to 8 Hz during HF fading), giving 15–25 dB better SNR than a simple bandpass + rectifier
- **Matched-filter pulse classification** — classifies pulses by counting samples above the midpoint threshold (HIGH-period duration), removing systematic bias from the envelope's rise/fall time; classification reference uses a 75th-percentile carrier estimate (fade-resistant) rather than the real-time IIR tracker
- **Percentile carrier reference** — tracks the 75th percentile of the last 30 inter-pulse HIGH-period peaks; multipath constructive spikes and HF-fade-depressed HIGH periods are outliers in this window and do not distort the classification threshold
- **Ionospheric fade detection** — `IsFading` flag correctly triggers when the envelope drops below 15% of the stable carrier reference for > 200 ms; fade-corrupted pulses receive zero confidence weight so they cannot corrupt the per-bit accumulator
- **Per-bit accumulation voting** (NTP driver 36 §3.2) — each of the 60 bit positions carries a signed evidence score (positive = One, negative = Zero) updated with an exponential moving average each minute; confident measurements push the score toward ±1; erasures apply a slow 0.90 decay so clean-frame evidence persists through several faded minutes; the vote threshold is ±0.15, below which the persistent store or structure default wins
- **Three-point bipolar discriminator** (NTP driver 36 §5) — after each 1000 Hz second tick the 100 Hz envelope is sampled at ~350 ms and ~650 ms after the tick; HIGH at 350 ms → Zero; LOW then HIGH at 650 ms → One; both LOW → erasure; this classifies bits from the carrier's return timing without relying on threshold crossings, which fail during HF fades that extend past the LOW period
- **Persistent slow-bit carry-over** — day-of-year, year, DUT1, DST, and leap-second positions (27 out of 60) are retained from the last successfully validated frame and used to fill those positions in subsequent partial frames, since they change at most once per day; only minutes and hours (which change every minute) require fresh collection each frame
- **Operator UTC date hint** — the operator can enter today's UTC date (yyyy-MM-dd) before or during listening; the decoder immediately pre-fills the 18 DOY and year bit positions, reducing the number of bits that must be received from 60 to ~13 under poor propagation; the hint is overwritten automatically by the first successful frame decode
- **P0→P1 gap confirmation** — when only the 100 Hz channel is available, validates the unique 9-second gap between P0 and P1 before anchoring, preventing the reset loop caused by marker-length noise during deep fades
- **Marker saturation gate** — detects deep ionospheric fades (>60% Marker rate in the last 20 pulses) and pauses anchor attempts until the signal recovers
- **Markov clock validation** — after each successful decode, advances the expected time by one minute and compares it against the next decode; a drift >30 s causes the frame to be **rejected** and the reference to advance from the last good prediction (not from the wrong decode), preventing a single bad bootstrapping frame from locking in a wrong time indefinitely
- **Gap filling** — when the signal drops for 2–30 seconds, estimates skipped bit positions from wall-clock time rather than resetting, so short ionospheric blackouts don't restart the 60-second collection window
- **Reserved-bit validation** — rejects frames where WWV's reserved positions are non-zero (indicates wrong alignment or heavy corruption)
- **Signal strength meter** with dB readout and adaptive noise-floor tracking
- **100 Hz level meter** showing the strength of the filtered subcarrier specifically
- **Lock quality indicator** showing decoder synchronization progress (Searching → Syncing → Locked)
- **Frame countdown** — 60-second timer showing seconds remaining until the next decode attempt
- **Decoded time display** — UTC time, day-of-year, DUT1 offset, DST status, leap-second warning
- **Confidence tracking** — hours and minutes are withheld from the display until 3 consecutive Markov-verified increments are observed (four back-to-back correctly decoded frames); "Set Clock" requires the same threshold; earlier frames still update date, DUT1, and DST
- **System clock synchronization** — sets Windows system time from decoded UTC (requires Administrator)
- **Worldwide station reference database** — 11 HF time-signal stations with frequencies, coordinates, and operating status
- **Activity log** with file persistence to `%APPDATA%\WwvDecoder\`
- **Audio device selector** — works with any Windows audio input (sound card, virtual cable, USB receiver)

---

## Supported Stations

The decoder currently supports stations that broadcast the **100 Hz pulse-width BCD time code** (WWV format):

| Station | Location | Frequencies (MHz) | Status |
|---------|----------|-------------------|--------|
| **WWV** | Fort Collins, CO, USA | 2.5, 5.0, 10.0, 15.0, 20.0, 25.0 | Active |
| **WWVH** | Kekaha, HI, USA | 2.5, 5.0, 10.0, 15.0 | Active |
| **BPM** | Pucheng, China | 2.5, 5.0, 10.0, 15.0 | Active |
| **LOL** | Buenos Aires, Argentina | 5.0, 10.0, 15.0 | Uncertain |

Additional stations are listed in the built-in reference table for informational purposes. These use different modulation formats and are not yet decodable:

| Station | Format | Decoder Status |
|---------|--------|----------------|
| **CHU** (Canada) | 300 baud Bell-103 FSK | Future |
| **RWM** (Russia) | Phase-shift-keyed 100 Hz | Future |
| **YVTO**, **HLA**, **BSF**, **HD2IOA** | Ticks only (no time code) | N/A |

> **Note:** European LF stations (MSF 60 kHz, DCF77 77.5 kHz, TDF 162 kHz) are not included — they require dedicated LF receivers, not HF/shortwave radios.

---

## Requirements

- **Windows 10/11** (x64)
- **.NET 9.0 Runtime** (included in self-contained publish)
- **Audio input** carrying the station's baseband audio (see [Audio Setup](#audio-setup))
- **Administrator privileges** required only for the "Set Clock" feature

---

## Getting the Application

Pre-built binaries are attached to each [GitHub Release](../../releases). Download the latest `WwvDecoder.exe` from the Releases page — no installation or .NET runtime required.

## Building from Source

```bash
# Clone and build (requires .NET 9 SDK)
dotnet build

# Publish as a single self-contained executable (~185 MB)
dotnet publish -c Release
```

The published output is a single self-contained `.exe` in `bin/Release/net9.0-windows/win-x64/publish/`. No separate .NET runtime installation is needed to run it.

### Dependencies

| Package | Version | Purpose |
|---------|---------|---------|
| [NAudio](https://github.com/naudio/NAudio) | 2.2.1 | Audio capture and device enumeration |
| [MathNet.Numerics](https://numerics.mathdotnet.com/) | 5.0.0 | Numerical computation |

---

## Audio Setup

The decoder needs baseband audio from an HF receiver tuned to one of the supported station frequencies. There are several ways to get audio into the application.

### Option A: Direct line-in from a shortwave receiver

Connect the audio output (line out or headphone jack) of your receiver to your computer's line-in or microphone input.

**Receiver settings:**
- Mode: **AM** (not USB, LSB, or CW)
- Frequency: any supported WWV frequency (10.000 MHz or 15.000 MHz are typically strongest in North America during the day)
- Filter/bandwidth: wide enough to pass audio down to 100 Hz — most AM filters are fine
- AGC: on (helps maintain consistent audio level through fading)
- Audio output level: set so the signal is clearly audible but not clipping

### Option B: Online or local SDR with virtual audio cable

Software-defined radios (hardware or web-based) work well, but require careful setup.

**SDR settings — critical:**
- Mode: **AM** — this is the most important setting. USB, LSB, FM, and CW will **not** pass the 100 Hz subcarrier and the decoder will see no signal regardless of strength
- Frequency: tune exactly to the station frequency (e.g., **10.000000 MHz** for WWV)
- If your SDR does not offer AM mode, tune USB to **100 Hz below** the station (e.g., 9.999900 MHz) — this places the 100 Hz subcarrier within the SSB passband, but AM mode is more reliable
- Audio filter: set the low-frequency cutoff as low as possible (50–80 Hz or lower) to pass the 100 Hz tone
- Squelch: **off** — squelch will mute the subcarrier during quiet periods and break decoding
- AGC: on, or set a fixed gain that keeps the audio level steady

**Routing audio from an SDR to RadioTime Decoder:**
1. Install a virtual audio cable such as [VB-Audio Virtual Cable](https://vb-audio.com/Cable/) (free)
2. Set the SDR's audio output device to the virtual cable input
3. In RadioTime Decoder, select the virtual cable output as the audio input device

**Popular online SDR resources:**
- [WebSDR.org](http://websdr.org) — network of publicly accessible receivers worldwide
- [KiwiSDR.com](http://kiwisdr.com) — distributed SDR network with AM mode support
- [OpenWebRX](https://www.openwebrx.de/) — self-hosted option

### Option C: Audio file playback

Play a recording of a WWV broadcast through a virtual audio cable or loopback device, then select that device as input. The decoder works identically with recordings — useful for testing without a live receiver.

---

## How to Use

### Basic Operation

1. **Launch** the application (as Administrator if you plan to set the clock)
2. **Select your audio input device** from the dropdown
3. **Select a station** — defaults to WWV
4. **Click "Start Listening"**
5. Watch the decoder progress through its states:
   - **Searching** — looking for a valid anchor pulse to orient on
   - **Syncing** — found an anchor, counting down through a 60-second frame
   - **Locked** — successfully decoded a valid frame; continuing to decode
6. Once the decoder has seen 3 consecutive Markov-verified time increments (Confidence 3/3), hours and minutes appear and the **"Set Clock"** button becomes active

### UTC Date Hint (Optional)

The **UTC Date** field in the top panel accepts today's date in UTC. It is pre-filled with the current UTC date at startup.

**Why this matters:** The WWV frame encodes 18 bits for day-of-year and year across positions 22–53, spread through the middle of the frame where HF fading is often worst. Under poor propagation these positions are frequently erased. If the decoder already knows the date, those 18 bits are available from the persistent store without needing to be received, leaving only the 13 bits for hours (6 bits) and minutes (7 bits) as unknowns. That is often the difference between a successful decode and a failed one.

**To use:**
1. Verify the pre-filled date is correct (remember: it shows UTC date, not local date — they differ near midnight if your UTC offset is non-zero)
2. Click **Apply** — the log will show `Operator date applied: 2026-04-04 UTC (year=26, DOY=094)`
3. The decoder immediately uses those bits; no restart needed
4. Click **Clear** to remove the hint and revert those bit positions to unknown

The hint is automatically superseded by the first successfully validated frame decode, so entering the wrong date only delays lock by one frame rather than permanently corrupting output.

### Reading the Signal Meters

#### Signal Level
The overall audio signal strength, derived from the ratio of the 100 Hz envelope peak to the noise floor (SNR × 10%, shown in dB). This reflects how much signal the receiver is delivering to the application. A reading of 0.0 dB or "--- dB" means no usable audio is arriving.

#### 100 Hz Level
The strength of the **100 Hz subcarrier specifically**, after the filters that isolate it from voice announcements, ticks, and other audio content. This is the most important meter for decoder health.

- **High 100 Hz Level + high Signal Level** — good clean signal, fast lock expected
- **High Signal Level but low 100 Hz Level** — audio is arriving but the 100 Hz subcarrier is absent. Most common cause: **receiver is not in AM mode**. USB/LSB/CW modes strip out the carrier and the subcarrier with it.
- **Low both** — weak or no signal. Try a different frequency, check antenna, or adjust receiver volume
- **100 Hz Level reads but won't lock** — signal is present but fading or noisy; let it run longer or try a stronger frequency

#### Sync Score
A 0–100% quality indicator for the 100 Hz subcarrier derived from two sub-scores:

- **Carrier score (65% weight):** Goertzel spectral analysis of the ALE-enhanced audio across a 500 ms window, measuring how prominently the 100 Hz tone stands above its spectral neighbors. High values indicate a clean, strong subcarrier.
- **Cadence score (35% weight):** how regularly the 100 Hz pulses arrive at approximately 1-second intervals. Degrades when ionospheric fading causes missed or overflowed pulses.

Typical values under real HF propagation are 30–70%. Values consistently above 60% indicate a strong, stable signal with good propagation. The status log reports the score every 5 seconds as `sync=N% @100.0 Hz`.

#### 100 Hz Lock
Shows how well the decoder is aligned to the station's frame structure. Rises as valid position markers are confirmed and falls when frames fail to validate.

The text to the right shows both a **frame countdown** and the **lock state**:
- `42s SYNCING` — syncing, 42 seconds until the next decode attempt
- `15s LOCKED` — locked, collecting the next frame with 15 seconds remaining
- `SEARCHING` — no countdown yet; waiting for the first anchor pulse

#### Confidence
Number of consecutive Markov-verified time increments. Each count represents one observed +1-minute transition that matched the predicted timeline. The "Set Clock" button and the hours/minutes display activate at 3/3. Each frame takes 60 seconds, so reaching 3/3 from cold start takes approximately 4 minutes (first frame establishes the baseline; three more verify it).

### Reading the Decoded Time

- **UTC time and date** — time-of-day and day-of-year encoded in the frame
- **DUT1** — difference between UTC and UT1 (Earth rotation time), in ±0.1 s steps
- **DST** — whether US daylight saving time is currently active (WWV/WWVH only)
- **Leap Second** — whether a leap second is scheduled at end of the current month

### Station Reference

Click **"Station Reference Table"** to open the full database of worldwide HF time-signal stations with frequencies, coordinates, and operating status.

---

## How It Works

### WWV Time Code Format

Each second of the WWV broadcast encodes one bit via the duration of a **HIGH-power period** on the 100 Hz subcarrier. The subcarrier is normally absent or at a low baseline level; approximately 30 ms after each 1 kHz second tick it rises to full power and holds there for a duration that encodes the bit type before dropping back to baseline (NIST IRIG-H positive-pulse format).

| Bit Value | HIGH Duration | LOW Duration | Meaning |
|-----------|--------------|--------------|---------|
| 0 (Zero)  | ~0.200 s     | ~0.800 s     | Binary 0 |
| 1 (One)   | ~0.500 s     | ~0.500 s     | Binary 1 |
| Marker    | ~0.800 s     | ~0.200 s     | Frame position marker |

In addition, WWV broadcasts **1000 Hz tone bursts** at the start of each second (5 ms ticks) and the start of each minute (800 ms minute pulse). These carry no BCD data but provide precise second-epoch timing and an unambiguous P0 anchor.

A complete frame spans 60 seconds (one bit per second, one frame per minute) and encodes:
- Hours and minutes in BCD
- Day of year (1–366) in BCD
- Two-digit year in BCD
- DUT1 correction (UT1 - UTC, ±0.0 to ±0.9 s)
- DST status (US daylight saving time)
- Leap-second pending flag

Position markers appear at seconds 0, 9, 19, 29, 39, 49, and 59 to frame the time code. Specific bit positions are reserved and always transmitted as 0 by WWV.

---

### Signal Processing Pipeline

The decoder uses two parallel demodulation channels — one for the 100 Hz BCD subcarrier and one for the 1000 Hz tone channel — that merge in the frame decoder.

```
Audio In (22,050 Hz, 16-bit mono, 50 ms blocks)
    │
    ▼
[1] Input AGC
    │  Normalizes audio level to 25% full scale
    │  Attack: 3 s  — slow enough that pulse LOW periods don't pump gain
    │  Decay:  5 s  — holds gain stable through HF ionospheric fading
    │
    ▼
[2] Highpass Filter (2nd-order Butterworth, 20 Hz cutoff)
    │  Removes DC offset, electrical hum, and sub-20 Hz audio rumble
    │  <0.1 dB attenuation at 100 Hz; <0.003 dB attenuation at 1000 Hz
    │
    ▼
[3] Notch Filter (60 Hz, ±2 Hz bandwidth, ~40 dB rejection)
    │  Eliminates US mains fundamental from power-line interference
    │
    ▼
[4] Notch Filter (120 Hz, ±2 Hz bandwidth, ~40 dB rejection)
    │  Eliminates 2nd harmonic (common in switching power supplies and SDR hardware)
    │
    ├─────────────────────────────────────────────┐
    │  100 Hz BCD channel                         │  1000 Hz tone channel
    ▼                                             ▼
[5] Synchronous (Lock-In) Detector           [6] Tick Detector
    │  IQ demodulation at 100 Hz                  │  IQ demodulation at 1000 Hz
    │  Lowpass: 2 Hz (nominal, stable signal)      │  Lowpass: 150 Hz (resolves 5 ms tick)
    │  → 8 Hz when HF fading detected (adaptive)  │  Adaptive level: 2 ms attack / 3 s decay
    │  Envelope = 2·√(I²+Q²)                      │  Classifies:
    │  SNR improvement: 15–25 dB                  │    ≤50 ms  → SecondTick (5 ms tick)
    │                                             │    ≥700 ms → MinutePulse (P0 anchor)
    ▼                                             │
[7] Pulse Detector                               │
    │  Tick-anchored positive-pulse detection     │
    │  NotifyTick() arms 200 ms rising-edge window│
    │  Weak-signal guard: suppress if H < 3×noise │
    │  IsFading: 1 kHz tick amplitude IIR         │
    │                                             │
    ▼                                             │
[7a] Matched Filter                              │
    │  Counts samples > 50% HIGH (HIGH duration)  │
    │  Tick / Zero / One / Marker classification  │
    │                                             │
    └──────────────────┬──────────────────────────┘
                       ▼
[9] Frame Decoder (Searching → Syncing → Locked)
    │
    │  Anchor priority:
    │    1. MinutePulse from 1000 Hz channel — direct P0, no gap confirmation needed
    │    2. P0→P1 gap (9 s unique gap) from 100 Hz channel — used if 1000 Hz is absent
    │
    │  Saturation gate: if >60% of recent 20 pulses are Markers, pause anchor search
    │   (signature of deep HF fade where every pulse measures ~0.8 s)
    │   Self-resets after 20 s of signal absence (propagation condition changed)
    │
    │  Gap filling: blackout 2–30 s → estimate skipped bits from wall clock,
    │   fill known marker positions with 2, data positions with 0, continue collecting
    │
    │  Frame integrity checks after each bit:
    │   — Consecutive Markers (impossible in any valid frame) → Searching immediately
    │   — Missing expected marker at positions 9, 19, 29, 39, 49 → Searching within 10 s
    │
    ▼
[10] Per-Bit Accumulator + Three-Point Bipolar Discriminator
    │  (NTP driver 36 §3.2 + §5)
    │
    │  Each bit position carries a signed evidence score [-1.0 .. +1.0]:
    │    Positive: evidence for One.  Negative: evidence for Zero.
    │    Updated by 100 Hz pulse measurement each minute (EMA, α ≤ 0.60).
    │    Updated by 3-point discriminator every second from 1000 Hz ticks.
    │    Erasures apply ×0.90 decay — clean-frame evidence persists across fades.
    │    Slow bits (DOY, year, DUT1, DST, leap) with a known persistent-store value
    │    use α ≤ 0.10 — requires several consistent frames to override the store.
    │
    │  Vote threshold |acc| ≥ 0.15; below that, three-tier fallback:
    │    1. Persistent store — value from the last successfully validated frame
    │       (covers 27 slow-changing positions: DOY, year, DUT1, DST, leap)
    │    2. Structure default — known marker positions → Marker, data positions → 0
    │
    │  Log shows lowercase characters for erased positions and hits=N/M per frame.
    │
    ▼
[11] BCD Decoder + Validation
    │  Checks all 7 position markers at positions 0, 9, 19, 29, 39, 49, 59
    │  Rejects frames with >12 total markers (>5 spurious) — indicates heavy corruption
    │  Validates 13 reserved bit positions (always 0 in a clean transmission)
    │  Decodes BCD fields: minutes, hours, day-of-year, year, DUT1 sign/magnitude
    │  Sanity checks: minutes ≤59, hours ≤23, doy 1–366, year ≤99, DUT1 magnitude ≤9
    │
    │  Markov clock validation: compares decoded time to expected (prior + 1 min).
    │   Drift >30 s logged as possible frame misalignment.
    │
    ▼
UI Display + Optional Clock Set
```

---

### Filter Details

#### Input AGC
A peak-following automatic gain control normalizes audio level before the DSP chain. The slow time constants (3 s attack, 5 s decay) are designed for HF fading: the 3 s attack causes only ~7% gain change during a 200 ms Zero pulse LOW period and ~23% during an 800 ms Marker — well within the PulseDetector's adaptive threshold range. Without AGC, disabled SDR AGC or deep HF fades cause `levelHigh` to undertrack by 2–3×, pushing the exit threshold down to the WWV LOW carrier level and making every ionospheric flicker look like a pulse. Gain is clamped to a maximum of 500× to prevent amplifying pure noise into apparent signal.

> **Note:** AGC is applied before the notch/highpass filters but **not** to the synchronous detector's output. AGC on the envelope would partially restore the LOW-period power reduction that encodes the time bits, blurring the boundary the pulse detector relies on.

#### Highpass Filter (20 Hz)
A second-order Butterworth highpass in direct-form II transposed removes DC offset and sub-20 Hz content. DC offset is common in SDR software audio pipelines. The 20 Hz cutoff passes the 100 Hz subcarrier with less than 0.1 dB attenuation.

#### Notch Filters (60 Hz and 120 Hz)
Two IIR biquad notch filters reject US power-line interference. The pole-radius design places zeros exactly on the unit circle at the notch frequency (infinite theoretical rejection) and poles just inside it at radius `r = 1 − π·BW/Fₛ`. A 2 Hz bandwidth gives ~40 dB rejection while attenuating adjacent frequencies by less than 0.1 dB. Both the 60 Hz fundamental and 120 Hz harmonic are filtered because both bleed through SDR hardware and inflate the noise floor seen by the synchronous detector.

#### Synchronous (Lock-In) Detector — 100 Hz Channel
The core of the BCD demodulator. Instead of bandpass filtering and rectifying:

1. A local oscillator generates `cos(2π·100·t)` and `sin(2π·100·t)` at exactly the subcarrier frequency
2. The input is multiplied by each reference to produce I (in-phase) and Q (quadrature) products
3. A single-pole IIR lowpass filter on each channel removes everything except near-DC content — which, after mixing, is where the 100 Hz signal sits
4. Envelope = `2·√(I² + Q²)` — the factor of 2 restores amplitude lost in mixing; the magnitude is phase-independent

**Why this is better than bandpass + rectifier:** The lowpass cutoff (2 Hz nominal, widening to 8 Hz during HF fading) integrates over many cycles of the 100 Hz carrier per time constant. A narrower integration window means more noise rejection. The initial improvement over a wide bandpass is 15–25 dB. The synchronous detector also has no DC offset problem from half-wave rectification.

The noise floor is tracked with an asymmetric algorithm: fast exponential decay when the envelope falls below the current floor (quickly finds the true quiet level) and very slow rise otherwise (the carrier amplitude during HIGH periods cannot inflate the floor over 0.8-second Marker pulses).

#### Tick Detector — 1000 Hz Channel
A second synchronous IQ demodulator runs in parallel at 1000 Hz with a 150 Hz lowpass (τ ≈ 1.06 ms). The short time constant resolves the 5 ms second tick (~92% of amplitude captured within one tick duration) while rejecting the 100 Hz subcarrier (which is 900 Hz away from DC after down-mixing to baseband).

The amplitude reference uses a fast 2 ms attack during tone presence — rising to track the pulse quickly — and a slow 3 s decay between pulses, so the reference holds across the 1 s inter-tick gap (decaying to only 72% after 1 s, still well above the exit threshold). This asymmetry is opposite to the 100 Hz PulseDetector, which tracks the carrier HIGH level between pulses.

Hysteresis thresholds are adaptive: enter at 50% of `levelHigh` (or 8× noise floor before `levelHigh` is established), exit at 25% of `levelHigh` (or 4× noise floor). The 6 dB dead-band prevents chattering as the tone envelope fades after a pulse ends.

Pulse classification by duration at exit-threshold crossing:
- ≤ 50 ms → **SecondTick** (nominal 5 ms; measured ~6–8 ms after lowpass smearing)
- ≥ 500 ms → **MinutePulse** (nominal 800 ms — the P0 minute marker)
- Other durations are discarded (no valid WWV tone has an intermediate length)

#### Adaptive Lowpass
The synchronous detector's lowpass defaults to **2 Hz** — a narrow bandwidth that maximizes noise rejection for stable signals. When the pulse detector's `IsAmplitudeUnstable` flag fires (rapid envelope swings indicating HF ionospheric multipath), the lowpass widens to **8 Hz** so the detector can track the faster envelope transitions during fading. It returns to 2 Hz once conditions stabilize.

No carrier PLL is used. The 100 Hz subcarrier is derived directly from the NIST atomic clock standard and is amplitude-keyed (on/off) — it is not frequency-modulated. The 100 Hz frequency in AM-demodulated baseband audio is exact by definition: the SDR local-oscillator offset shifts the HF carrier but the 100 Hz subcarrier is generated by dividing the station's on-site atomic standard, so it remains at exactly 100 Hz after AM demodulation regardless of receiver tuning error. A frequency-tracking PLL would be solving a problem that does not exist.

#### Pulse Detector
Converts the amplitude envelope into discrete pulse events by measuring the duration of the positive-pulse HIGH period following each second tick. Detection is tick-anchored: each 1 kHz `NotifyTick()` call closes any open pulse from the previous second and arms a 200 ms rising-edge window for the next one.

`levelHigh` is tracked by two mechanisms with different purposes:

**Real-time IIR** (100 ms attack, 30 ms fast-recovery attack, 3 s decay): drives the per-sample `enterThreshold` and `exitThreshold`. Attack is gated to HIGH-only periods so noise during a LOW period cannot inflate the reference and lock the detector in the pulse state indefinitely. The fast-recovery branch (30 ms τ) snaps the tracker back after a deep fade where the IIR has decayed to ~69% of the true carrier.

**75th-percentile of recent inter-pulse peaks**: each time a pulse starts, the peak envelope from the preceding HIGH period is pushed into a 30-entry circular window. The 75th percentile of this window is used as the reference for pulse classification (see Matched Filter below). This separates the two concerns: the IIR reacts fast enough for threshold detection; the percentile provides a stable reference that is resistant to both multipath constructive spikes (brief high outliers) and HF-fade-depressed HIGH periods (low outliers).

Hysteresis prevents chattering: the detector enters a pulse at 55% of the IIR HIGH level and exits only when the envelope clears 62% of HIGH for 30 ms (the dropout tolerance). The 7% dead-band spans the envelope rise/fall time of the synchronous detector. A safety cap forces any LOW period longer than 1.1 seconds to end, preventing a stuck state during signal dropout.

A **weak-signal guard** suppresses all pulse detection while the HIGH level is less than 3× the noise floor.

**Fade detection** (`IsFading` flag): fires when the envelope has been below **15% of the stable carrier reference** for more than 200 ms. The WWV LOW carrier is ~31% of HIGH — well above the 15% threshold — so normal pulse LOW periods never trigger it. Deep HF fades drop the envelope to noise level (<5%), correctly setting `IsFading = true`. Once set, recovery requires 500 ms of continuous signal and the IIR level recovering to ≥ 60% of the running peak envelope. Pulses emitted while `IsFading` carry zero confidence weight and are treated as erasures by the multi-frame accumulator.

#### Matched Filter
At the end of each detected pulse, the matched filter classifies it by counting how many envelope samples were **above** the midpoint threshold (50% of `levelHigh`) — measuring the HIGH-period duration of the positive pulse. This binary count is equivalent to correlating the envelope against a rectangular HIGH-period template for each bit type — the optimal classifier in white Gaussian noise.

This eliminates a systematic bias from simple threshold-crossing measurement: the ~20 ms envelope rise and fall times would inflate a nominal 200 ms Zero pulse to ~240 ms. The matched filter counts only samples genuinely in the HIGH state, removing this bias. Classification boundaries calibrated from live SDR measurements: < 50 ms = Tick, 50–350 ms = Zero, 350–650 ms = One, ≥ 650 ms = Marker.

The midpoint threshold uses the **percentile-based carrier reference** captured at pulse start, not the real-time IIR value. This solves two problems simultaneously: (1) the IIR decays during an 800 ms Marker's LOW period to ~76% of the true carrier, which would drop the midpoint threshold to 38% — dangerously close to the actual LOW carrier level at 31%; (2) multipath constructive interference spikes can inflate the IIR before a pulse, raising the midpoint above the LOW carrier so the matched filter counts zero genuine-LOW samples and misclassifies everything as a Tick. The percentile reference is immune to both: spikes are high outliers in the 30-entry window and do not shift the 75th percentile; HF-faded HIGH periods are low outliers and also do not shift it.

---

### Frame Decoder Logic

#### State Machine
The decoder runs a three-state machine:

- **Searching** — watches for a valid anchor pulse and enters Syncing once found.
- **Syncing** — collects bits and validates alignment using early checks before committing to a full 60-second window.
- **Locked** — decodes a full frame every 60 seconds. Two consecutive decode failures drop back to Searching.

#### P0 Anchor Detection — Two Paths

**Path 1: 1000 Hz minute pulse (preferred)**
The minute pulse is an 800 ms burst of 1000 Hz tone at the start of second 0. When detected, P0 is immediately anchored without any waiting period. This is more reliable than the 100 Hz channel alone:
- The 1000 Hz tone is independent of BCD modulation depth — no amplitude ambiguity
- The minute pulse is the loudest and longest feature in the audio signal
- A single detection is sufficient — no second measurement needed for confirmation

The `TickDetector` fires the `MinutePulse` event when the 800 ms pulse *ends*, approximately 800 ms after the true UTC second-0 boundary. The anchor is back-projected to the exact second-0 epoch by subtracting the measured pulse width, so the `elapsed` calculation for each subsequent second tick equals the true second number N rather than N−1. Without this correction, tick-derived bit indices are consistently one position low, causing bit[01] to be discarded after every P0 anchor and all subsequent bits to land one position early.

When both channels detect P0 in the same audio block, the earlier arrival (whichever fired first) anchors while the second is treated as a confirmation and absorbed without being stored as bit 1.

**Path 2: P0→P1 gap confirmation (fallback)**
When only the 100 Hz channel is available, two consecutive Marker pulses are compared. The P0→P1 gap is uniquely **9 seconds**; all other marker-to-marker gaps are 10 seconds. A Marker is stored as a P0 candidate; the next Marker is measured:
- Gap 8.5–9.5 s: confirmed P0→P1 — anchor at P0, enter Syncing at bit 10
- Gap 9.5–10.5 s: valid P1-onward gap — update candidate, keep looking
- Other gaps: wrong gap — reset candidate to current Marker

This prevents the reset loop caused by marker-length noise during deep fades (where every pulse measures ~0.8 s regardless of true content), because two consecutive plausible-looking pulses are required to agree on a 9-second window.

#### Marker Saturation Gate
During deep ionospheric fades the 100 Hz carrier can drop for ~0.8 s during what should be 0.2 s Zero or 0.5 s One periods, causing almost all pulses to be classified as Markers. Normal WWV has 7/60 = 11.7% Markers. When more than 60% of the last 20 pulses are Markers, the gate pauses all anchor attempts — log activity stops, no more "bad gap" spam. The gate recovers below 25% Marker rate (hysteresis to prevent rapid oscillation). If signal has been entirely absent for more than 20 seconds, the gate resets immediately — the propagation window has changed and stale measurements should not block a fresh start.

#### Per-Bit Accumulator Voting
Each of the 60 bit positions carries a signed evidence score in the range [−1.0, +1.0]. A positive score is evidence for One; negative is evidence for Zero. This replaces the earlier ring-buffer majority voter.

Each minute, after a frame is assembled, every confident bit (both classifiers agreed, not fade-zeroed) updates its accumulator position via an exponential moving average:

```
acc[i] += α × (target − acc[i])
```

where `target` is +1 for a One measurement and −1 for a Zero measurement. The alpha cap is:
- **α ≤ 0.10** for slow-changing bit positions (DOY, year, DUT1, DST, leap) that have a known value in the persistent store — a single confident-wrong measurement moves the score by at most 0.09, staying below the 0.15 vote threshold so the persistent store remains authoritative until several frames consistently disagree.
- **α ≤ 0.60** for all other positions (hours, minutes) — reacts faster to genuine signal.

Erased positions (gap-filled, classifiers disagreed, tick-fade-zeroed) apply a slow **×0.90 decay** each minute instead of a targeted update. Clean-frame evidence at ±0.5 survives approximately 6 consecutive faded minutes before falling below the 0.15 vote threshold.

This is the key insight from Mills' NTP driver 36: *ionospheric fades produce erasures, not wrong votes.* A gap-filled estimate carries no directional evidence and decays passively; it cannot flip an accumulator position that was pushed by a prior clean frame.

The vote rule: if `|acc[i]| ≥ 0.15`, the sign determines the voted bit. Otherwise the three-tier fallback applies:

1. **Persistent slow-bit store** — 27 positions covering day-of-year, year, DUT1, DST, and leap-second warning are retained from the last successfully BCD-validated frame. Since the day changes at most once every 24 hours and the year once per year, these values are almost always correct on subsequent frames. Minutes and hours are deliberately excluded — they change every minute and stale values would corrupt the decode. The operator UTC date hint (see [UTC Date Hint](#utc-date-hint-optional)) seeds this store at startup and also pre-seeds the accumulator to ±0.4, so the hint is immediately authoritative even before the first frame decode.

2. **Structure-aware default** — if the persistent store has no value (cold start) or the position is not a slow-changing field, known WWV structure is used: expected marker positions (0, 9, 19, 29, 39, 49, 59) default to Marker; all other positions default to 0.

The accumulator **persists across re-anchors** (it is not cleared on P0 detection) so evidence from prior clean frames survives minute-boundary fades that force a re-anchor. It is only cleared by a full user-initiated decoder reset.

#### Three-Point Bipolar Discriminator
After each 1000 Hz second tick at position N, the 100 Hz envelope is sampled at two offsets independently of the PulseDetector's threshold-crossing measurement:

- **Sample A @ ~350 ms** — between the Zero (200 ms) and One (500 ms) LOW-period ends
- **Sample B @ ~650 ms** — between the One (500 ms) and Marker (800 ms) LOW-period ends

Classification using the tracked carrier level as a 50% threshold:
- A **HIGH** → **Zero** (carrier returned within 200 ms)
- A low, B **HIGH** → **One** (carrier returned between 350–650 ms)
- Both low → erasure (Marker, or carrier still absent — deep fade)

This provides a second independent measurement that directly updates the accumulator with `α = 0.50`. It is especially valuable during partial fades that extend past the Zero LOW period but not the One LOW period — conditions where the threshold-crossing detector would misclassify the bit, but the discriminator correctly identifies it. It does not help during full broadband fades where both channels are dark simultaneously, but those frames generate erasures rather than wrong votes regardless.

The frame log shows erased positions as lowercase letters (`m`, `0`, `1`) and a `hits=N/M` count per frame where N is confidently-classified pulses and M is total pulses.

#### Frame Integrity Checks
Two structural invariants are checked after each bit is stored, bailing early rather than collecting 60 bits and failing at decode time:

1. **Consecutive Markers** — no valid WWV frame ever has two adjacent Marker bits (minimum marker separation is 9 seconds). A run of consecutive Markers is the signature of HF fades being misclassified. Triggers immediate return to Searching.
2. **Progressive marker check** — at every 10-second boundary (bit positions 10, 20, 30, 40, 50), the preceding position must contain a Marker. This catches misalignment within 10 seconds instead of waiting 60 seconds.

#### BCD Decoder Validation
The decoder applies four validation layers in order:

1. **Marker positions** — all 7 position markers (P0, P1, P2, P3, P4, P5, P59) must be present at the expected bit positions: 0, 9, 19, 29, 39, 49, 59.
2. **Spurious marker count** — more than 12 total markers (7 expected + 5 spurious) indicates heavy signal corruption and the frame is rejected rather than decoded to a wrong time.
3. **Reserved bits** — WWV always transmits 0 at positions 5, 10, 11, 16, 20, 21, 26, 32, 35, 38, 44, 54, and 58. A non-zero reserved bit means the frame is misaligned or corrupted.
4. **BCD range checks** — decoded values are checked: minutes ≤ 59, hours ≤ 23, day-of-year 1–366, year ≤ 99, DUT1 magnitude ≤ 9.

All four must pass for a frame to produce a `TimeFrame` result.

#### Markov Clock Validation
After each successful decode, the expected time for the next minute is stored (`decoded time + 1 minute`). The next successful decode is compared to this expectation:

- **Drift ≤ 30 s** — the frame is accepted, `_clockVerifiedCount` is incremented, and the expected time advances to `decoded time + 1 minute`. The log shows `Verified #N: HH:MM (drift +0.4s from expected)`.
- **Drift > 30 s** — the frame is **rejected** (not emitted to the UI, `_clockVerifiedCount` resets to 0). The expected time advances by one minute from the **last good prediction**, not from the wrong decoded value. This prevents a single bad bootstrapping frame from corrupting the reference so that every subsequent consistent-wrong frame also passes.

A rejected frame falls into the consecutive-invalid counter; two consecutive rejections trigger a full reset to Searching.

**Hours and minutes are only displayed and eligible for clock set after `_clockVerifiedCount` reaches 3** — three back-to-back Markov-passing frames after the initial bootstrapping decode. Before that threshold the display shows `--:--:--` and date/DUT1/DST are still updated from their independently confirmed slow-bit fields.

**Known limitation:** the Markov check compares successive decoded times, so it detects a fixed wrong-hours offset only at the moment of transition (when a good frame is followed by a wrong one). If the very first decoded frame has wrong hours *and* subsequent frames decode consistently to the same wrong time, the +1-minute increments will still verify. The three-frame threshold reduces the probability that a noise event produces three plausible-looking consecutive decodes, but does not fully eliminate it. An external time reference (NTP, operator-supplied time hint) would be required to catch this case definitively.

#### Gap Filling
When the signal drops for 2–30 seconds (the cadence guard detects the inter-pulse gap exceeds 2 s), the decoder estimates how many bits were missed using `round(gap) − 1` and fills those positions with default values: known marker positions receive value 2, all other positions receive 0. Filled positions are tagged as erased (not confident) and do not participate in the majority vote against confirmed bits from prior frames. If filling completes a 60-bit frame, decode is attempted immediately. Gaps longer than 30 seconds trigger a full reset to Searching rather than filling — too many unknowns to fill reliably.

---

---

## Project Structure

```
RadioTime Decoder/
├── App.xaml                        # WPF application entry point
├── MainWindow.xaml                 # Primary UI (dark theme, signal meters, log)
├── Converters.cs                   # WPF value converters for UI bindings
├── WwvDecoder.csproj               # .NET 9 project (WPF, self-contained publish)
│
├── Audio/
│   ├── AudioInputDevice.cs         # NAudio audio capture with thread-safe callbacks
│   └── AudioDeviceInfo.cs          # Audio device enumeration
│
├── Dsp/
│   ├── InputAgc.cs                 # Input AGC (3 s attack, 5 s decay, 25% target)
│   ├── HighpassFilter.cs           # 2nd-order Butterworth highpass, 20 Hz cutoff
│   ├── NotchFilter.cs              # IIR biquad notch (60 Hz and 120 Hz instances)
│   ├── SynchronousDetector.cs      # Coherent IQ lock-in detector for 100 Hz subcarrier
│   ├── CarrierPll.cs               # Costas-loop PLL (not in active pipeline — 100 Hz is not frequency-modulated)
│   ├── PulseDetector.cs            # Tick-anchored positive-pulse detection with gated HIGH tracking
│   ├── MatchedFilter.cs            # HIGH-duration matched filter for pulse classification
│   ├── TickDetector.cs             # 1000 Hz IQ demodulator; second ticks and minute pulse
│   ├── AdaptiveLineEnhancer.cs     # NLMS adaptive line enhancer (not in active pipeline)
│   └── BandpassFilter.cs           # Legacy biquad bandpass (not in active pipeline)
│
├── Decoder/
│   ├── DecoderPipeline.cs          # Wires DSP chain → frame decoder; both 100 Hz and 1000 Hz
│   ├── FrameDecoder.cs             # Searching/Syncing/Locked state machine + erasure-aware vote
│   ├── BcdDecoder.cs               # 60-bit BCD frame parser with reserved-bit validation
│   ├── TimeFrame.cs                # Decoded time data (UTC, DUT1, DST, leap)
│   └── SignalStatus.cs             # Signal/lock/subcarrier strength reporting
│
├── Stations/
│   ├── StationsDatabase.cs         # 11 worldwide HF time-signal stations
│   ├── StationInfo.cs              # Station metadata and format classification
│   └── StationReferenceWindow.xaml # Modal reference table UI
│
├── Clock/
│   └── SystemTimeSetter.cs         # Windows SetSystemTime() via P/Invoke
│
├── Logging/
│   └── FileLogger.cs               # Thread-safe daily log files
│
└── ViewModels/
    ├── MainViewModel.cs            # MVVM application logic
    └── RelayCommand.cs             # ICommand implementation
```

---

## Use Case Examples

### 1. Setting a PC clock from WWV in a lab environment

You maintain instruments in a calibration lab with no internet access. A shortwave receiver is tuned to WWV on 10 MHz. Connect its audio output to the PC's line-in jack.

```
1. Launch WwvDecoder as Administrator
2. Set receiver: AM mode, 10.000 MHz
3. Select "Line In" as the audio device
4. Select "WWV — Fort Collins, Colorado, USA"
5. Click Start Listening
6. Watch the 100 Hz Level bar — it should show signal within seconds
7. Wait ~4 minutes for Confidence 3/3 (hours and minutes confirmed by 3 consecutive Markov-verified increments)
8. Click "Set Clock" to synchronize Windows time to UTC
```

The log shows the time delta applied (e.g., `Clock set. Delta was +342.0 ms`).

### 2. Using an online SDR (WebSDR / KiwiSDR)

You don't have a receiver, but want to decode WWV using an internet-connected online SDR.

```
1. Install VB-Audio Virtual Cable
2. Open a WebSDR or KiwiSDR site in your browser
3. Tune to 10.000 MHz, select AM mode
4. Set the SDR's audio output to "CABLE Input (VB-Audio)"
5. In WwvDecoder, select "CABLE Output (VB-Audio)" as audio device
6. Start Listening
7. Verify the 100 Hz Level bar shows signal — if it stays at 0, the SDR is not in AM mode
```

If the 100 Hz Level bar is flat but Signal Level shows activity, switch the SDR to AM mode and try again.

### 3. Verifying a WWV recording

You have a `.wav` file of a WWV broadcast and want to confirm the timestamp encoded in it.

```
1. Install a virtual audio cable
2. Play the .wav file through the virtual cable using any media player
3. In WwvDecoder, select the virtual cable as input
4. Start Listening and wait for decode
5. The decoded time display shows the UTC time from the recording
```

This is useful for timestamping recordings, verifying equipment, or educational purposes.

### 4. Monitoring BPM from East Asia

You are in the Asia-Pacific region where BPM (China) is stronger than WWV. Tune your receiver to BPM on 10 MHz or 15 MHz.

```
1. Set receiver: AM mode, 10.000 MHz or 15.000 MHz
2. Select "BPM — Pucheng, Shaanxi, China" from the station list
3. The decoder uses the same BCD format as WWV — no configuration needed
4. Start Listening — the time code is UTC despite BPM's voice being in UTC+8
```

### 5. Testing propagation conditions

You want to check which WWV frequencies are currently propagating to your location.

```
1. Start with your receiver on 10 MHz WWV, AM mode
2. Start Listening and note the 100 Hz Level and Signal Level readings
3. Stop Listening, retune to 15 MHz, repeat
4. Compare signal levels across frequencies
5. The activity log records levels with timestamps for later review
```

General propagation guide: 5/10 MHz tends to be stronger at night; 15/20 MHz tends to be stronger during the day. Conditions vary by season and solar activity.

### 6. Air-gapped time synchronization

In a secure facility with no network connectivity, system clocks drift over time. A shortwave receiver provides an independent, traceable time source.

```
1. Install a dedicated HF antenna and receiver tuned to WWV
2. Set receiver: AM mode, best-propagating frequency for your location and time of day
3. Route audio to the target PC via line-in
4. Run WwvDecoder as Administrator
5. After achieving lock (Confidence 3/3), use "Set Clock" to correct drift
6. Check the log for the applied delta to track drift rate over time
```

### 7. Educational demonstration of radio time signals

For a classroom or ham radio club demonstration of how atomic time is distributed via radio:

```
1. Connect a receiver (or online SDR via virtual cable) to a projector-connected PC
2. Launch WwvDecoder — the dark UI is readable on projectors
3. Tune through different stations to show the reference database
4. Lock onto WWV and explain each field as it decodes:
   - The 100 Hz subcarrier and how pulse widths encode binary data
   - The 1000 Hz tick channel and how the minute pulse directly anchors decoding
   - The frame countdown showing the 60-second sync cycle
   - BCD encoding of hours, minutes, day-of-year
   - DUT1 correction between atomic time and Earth rotation
   - Position markers framing the 60-second time code
   - The lock-in detector's SNR advantage over simple rectification
```

---

## Troubleshooting

| Symptom | Likely Cause | Fix |
|---------|-------------|-----|
| Signal Level > 0 but 100 Hz Level = 0 | Receiver not in AM mode | Switch SDR/receiver to AM mode |
| Both meters at 0 | No audio reaching the app | Check device selection and audio routing; check SDR volume |
| Stuck on "Searching" | No anchor pulses detected | Signal too weak, wrong frequency, or not AM mode |
| Log shows "Signal too faded" | >60% of pulses are Marker-length | Deep ionospheric fade; try a different frequency or wait for propagation to improve |
| Log shows "Bad gap N.Ns" repeatedly | Marker noise between frames | Expected during fade; decoder is correctly rejecting non-9-second gaps |
| Stuck on "Syncing" — countdown resets every ~10 s | Progressive marker check failing | Signal misaligned; decoder bails within 10 s and retries |
| Countdown runs to 0 but no lock | Reserved bits or markers failing | Signal too noisy for reliable frame alignment; let it run or try a different frequency |
| Decodes but time is wrong | Recording from a different date | Expected for old recordings — the encoded time is when it was recorded |
| Log shows "Clock mismatch … rejected" | Decoded time inconsistent with prior frame | Decoder rejected the frame and will re-verify; corrects automatically within 1–2 frames if the signal is stable |
| Hours show `--:--:--` after first decode | Markov verification threshold not yet reached | Wait for Confidence to reach 3/3 (~4 minutes from cold start) |
| "Set Clock" button grayed out | Confidence below 3/3 | Wait for Confidence to reach 3/3 — hours/minutes must be Markov-verified before clock set is enabled |
| App requires Administrator | Needed for SetSystemTime() | Right-click → Run as Administrator |
| Crash on start | Missing .NET 9 runtime | Use the self-contained published build, or install .NET 9 |

---

## License

This project is not currently licensed. All rights reserved.
