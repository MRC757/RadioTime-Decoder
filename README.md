
# RadioTime Decoder

> [!NOTE]
> Decoding requires a minimum of **3 minutes** from a cold start (one 60-second frame to establish the anchor, two more to verify it). Actual time to first confirmed decode depends heavily on HF propagation conditions and signal quality — ionospheric fading can corrupt 50–90% of each 60-bit frame, and decoded times may be absent or significantly delayed during poor propagation. Do not use it as a time reference for anything safety-critical.

> [!NOTE]
> This application has only been tested with **WWV**. WWVH, BPM, and other stations that share the 100 Hz pulse-width BCD format are implemented but have not been verified against a live signal.

<img width="1027" height="1324" alt="WWV Time decoder Screenshot" src="https://github.com/user-attachments/assets/6055d409-b556-4ed5-9cf5-0c3428f28fe4" />

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
- **Per-bit accumulation voting** (NTP driver 36 §3.2) — each of the 60 bit positions carries a signed evidence score (positive = One, negative = Zero) updated with an exponential moving average each minute; confident measurements push the score toward ±1; erasures apply a slow 15/16 decay (NIST d=16 comb filter rate) so clean-frame evidence persists through several faded minutes; the vote threshold is ±0.15, below which the persistent store or structure default wins
- **Soft BCD constraint scoring** — before hard-threshold voting is applied, every structurally valid integer value for each BCD field (minutes, hours, DOY, year) is scored against the raw per-bit accumulators; the highest-scoring valid value wins; this resolves marginal bits toward the nearest valid time field rather than producing an outright rejection when one bit straddles the threshold
- **Three-point bipolar discriminator** — after each 1000 Hz second tick the 100 Hz envelope is sampled at ~350 ms and ~650 ms; both LOW → Zero (carrier dropped at 200 ms); HIGH then LOW → One (carrier dropped at 500 ms); both HIGH → Marker erasure; provides a second independent measurement from carrier timing alone, useful during partial fades where threshold-crossing detection fails
- **Cross-frame hours/minutes accumulator seeding** — after each Markov-verified frame the expected next-minute hours and minutes are pre-seeded into the accumulator at ±0.40; only positions with no current strong opinion are seeded, giving fast-changing fields the same head start that the persistent store gives to slow fields
- **Persistent slow-bit carry-over** — day-of-year, year, DUT1, DST, and leap-second positions (25 out of 60) are retained from the last successfully validated frame and used to fill those positions in subsequent partial frames, since they change at most once per day; minutes and hours (which change every minute) use cross-frame seeding instead
- **Per-field independent confidence display** — date, DOY, DUT1, DST, and leap-second update immediately as soon as any BCD-valid frame is received, even before the Markov clock check passes for hours/minutes; the time display (HH:mm:ss) is gated separately; partial frames that pass BCD validation but fail the Markov clock check still update the date panel
- **Operator UTC date hint** — the operator can enter today's UTC date (yyyy-MM-dd) before or during listening; the decoder immediately pre-fills the 18 DOY and year bit positions, reducing the number of bits that must be received from 60 to ~13 under poor propagation; the hint is overwritten automatically by the first successful frame decode
- **P0→P1 gap confirmation** — when only the 100 Hz channel is available, validates the unique 9-second gap between P0 and P1 before anchoring, preventing the reset loop caused by marker-length noise during deep fades
- **Marker saturation gate** — detects deep ionospheric fades (>60% Marker rate in the last 20 pulses) and pauses anchor attempts until the signal recovers
- **Wall-clock Markov clock validation** — after the first successful decode, establishes a wall-clock anchor (`decoded_time`, `DateTime.UtcNow`); each subsequent frame is compared to `decoded_anchor + round(real_elapsed_minutes)`; drift >30 s rejects the frame; using real elapsed time rather than a per-frame counter prevents drift escalation during propagation outages where many frames are missed entirely
- **Gap filling** — when the signal drops for 2–30 seconds, estimates skipped bit positions from wall-clock time rather than resetting, so short ionospheric blackouts don't restart the 60-second collection window
- **Reserved-bit validation** — rejects frames where WWV's reserved positions are non-zero (indicates wrong alignment or heavy corruption)
- **Minute-boundary level recovery** — when the 800 ms minute pulse ends, three coordinated resets fire: the TickDetector's amplitude reference is zeroed (so the first 5 ms second tick at ~144 ms after the pulse is not suppressed by the 4 s decay), the AGC level is restored to a pre-pulse snapshot (captured at the second-59 tick) rather than resetting to 1× gain, and the PulseDetector's 30-entry percentile window is cleared so the classification reference relearns from post-pulse signal amplitudes; together these prevent bits 1–4 from being gap-filled at every minute boundary
- **Input saturation alert** — when the AGC gain falls below −6 dB (signal more than twice the AGC target), an amber banner warns the user to reduce audio input volume; prevents the pulse-width corruption caused by receiver or sound card clipping
- **Signal strength meter** with dB readout and adaptive noise-floor tracking
- **100 Hz level meter** showing the strength of the filtered subcarrier specifically
- **Lock quality indicator** showing decoder synchronization progress (Searching → Syncing → Locked)
- **Frame countdown** — 60-second timer showing seconds remaining until the next decode attempt
- **Live seconds display** — after the first confirmed decode, the UTC time display increments every second using a wall-clock anchor tied to the minute-start tone; the `MinutePulseDetected` event carries a timestamp captured on the audio callback thread (not the UI thread) so dispatcher-queue delay cannot corrupt the back-projection; the anchor is refined each minute and running seconds stay aligned to actual WWV second boundaries
- **Decoded time display** — UTC time, day-of-year, DUT1 offset, DST status, leap-second warning
- **DST-adjusted local time** — when the DST bit in the frame is active, the local time display automatically adds one hour to the user's selected standard-time UTC offset and appends "DST" to the timezone label (e.g. "UTC-5  EST" → "UTC-4  EDT DST")
- **Confidence tracking** — hours and minutes are withheld from the display until 2 consecutive Markov-verified increments are observed; date, DUT1, and DST display immediately from the first BCD-valid frame; "Set Clock" requires the same 2-frame threshold
- **System clock synchronization** — sets Windows system time to the live decoded time including current seconds (requires Administrator); the correction is gated on **wall-anchor freshness** rather than delta size — if no minute pulse has arrived in the last 90 s the set is skipped and logged, ensuring correctness whether the clock is 200 ms or several years off (dead CMOS battery)
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

| Setting | Value | Why |
|---------|-------|-----|
| **Mode** | **AM** | USB/LSB/CW strip the carrier; no carrier = no 100 Hz subcarrier |
| **Frequency** | 10.000 MHz (recommended) | Most reliable propagation day and night; also try 15 MHz (day) or 5 MHz (night) |
| **IF bandwidth** | 3–8 kHz | Wide enough to pass voice sidebands; the 100 Hz subcarrier is well within any normal AM filter |
| **Audio low-cut** | Off, or ≤ 80 Hz | **Critical:** must pass 100 Hz. Many receivers have a 300 Hz voice filter that removes the entire subcarrier |
| **Noise reduction / NR** | **Off** | Digital noise reduction processes audio at 100–300 Hz and distorts or removes the subcarrier |
| **AGC** | On | Holds audio level steady through HF ionospheric fading |
| **Audio output level** | Moderate | Signal clearly audible but not clipping; the app's AGC will handle level differences |

> **Common pitfall:** Many receivers and transceivers apply an audio low-cut (high-pass) filter to reduce hiss and power-line hum. If your receiver has a "filter," "tone," or "bass" control, disable it or set it to pass 100 Hz. A filter set to "300 Hz" is the most common reason the 100 Hz Level meter stays at zero even with a strong signal.

### Option B: Online or local SDR with virtual audio cable

Software-defined radios (hardware or web-based) work well, but require careful setup.

**SDR settings — critical:**

| Setting | Value | Why |
|---------|-------|-----|
| **Mode** | **AM** | **Most important setting.** USB, LSB, FM, and CW do not pass the 100 Hz subcarrier; the decoder will see no signal regardless of signal strength |
| **Frequency** | Exact station frequency (e.g., **10.000000 MHz**) | LO offset does not matter — AM demodulation puts the 100 Hz subcarrier at exactly 100 Hz regardless of small tuning errors |
| **IF / audio bandwidth** | **3–8 kHz** | Passes the full AM audio including voice (300–3000 Hz) and the 100 Hz subcarrier; no benefit to wider than 10 kHz |
| **Audio low-cut / high-pass** | **Off, or set to ≤ 80 Hz** | Most SDR software defaults to a 300 Hz high-pass for SSB use — this must be disabled or lowered to pass 100 Hz |
| **Squelch** | **Off** | Squelch mutes the subcarrier during the LOW carrier periods that encode Zero bits and will corrupt decoding |
| **Noise reduction (NR)** | **Off** | Spectral or Wiener NR filters distort or remove the 100 Hz subcarrier |
| **Notch filter** | Off (unless specifically targeting 100 Hz interference) | Auto-notch features may lock on to the 100 Hz subcarrier and null it out |
| **AGC** | On, or fixed gain keeping audio level moderate | Holds level through HF fading |
| **Sample rate** | 8000 Hz minimum; 22050 or 44100 Hz recommended | The 100 Hz subcarrier requires no more than ~500 Hz of bandwidth; any standard rate works |

> **If your SDR does not offer AM mode:** tune USB to exactly **100 Hz below** the station carrier (e.g., 9.999900 MHz for WWV on 10 MHz). This places the 100 Hz subcarrier at 100 Hz within the SSB passband. AM mode is strongly preferred because USB/LSB are sensitive to tuning accuracy and LO drift; AM is immune to both.

**Input level:** Keep the audio input level moderate. If the AGC Gain display shows a large negative dB value and an amber **"Input level too high"** banner appears, reduce the volume at the receiver or SDR. Over-driven audio clips pulse edges and corrupts the matched-filter's HIGH-duration measurement.

**Routing audio from an SDR to RadioTime Decoder:**
1. Install a virtual audio cable such as [VB-Audio Virtual Cable](https://vb-audio.com/Cable/) (free)
2. Set the SDR's audio output device to the virtual cable input
3. In RadioTime Decoder, select the virtual cable output as the audio input device

**Popular online SDR resources:**
- [WebSDR.org](http://websdr.org) — network of publicly accessible receivers worldwide
- [KiwiSDR.com](http://kiwisdr.com) — distributed SDR network with AM mode support
- [OpenWebRX](https://www.openwebrx.de/) — self-hosted option

**Checklist for SDR audio before starting the decoder:**

- [ ] Mode is set to **AM** (not USB, LSB, FM, CW, or NFM)
- [ ] Low-frequency cutoff is **80 Hz or lower** (not the 300 Hz voice default)
- [ ] Squelch is **off**
- [ ] Noise reduction is **off**
- [ ] Audio output routed to virtual cable or system loopback

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
6. Date, DOY, DUT1, DST, and leap-second appear as soon as the first BCD-valid frame is received. Once the decoder has seen 2 consecutive Markov-verified time increments (Confidence 2/2), hours and minutes appear and the **"Set Clock"** button becomes active

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

- **Carrier score (65% weight):** Goertzel spectral analysis of the post-notch audio across a 500 ms window, measuring how prominently the 100 Hz tone stands above its spectral neighbors. High values indicate a clean, strong subcarrier.
- **Cadence score (35% weight):** how regularly the 100 Hz pulses arrive at approximately 1-second intervals. Degrades when ionospheric fading causes missed or overflowed pulses.

Typical values under real HF propagation are 30–70%. Values consistently above 60% indicate a strong, stable signal with good propagation. The status log reports the score every 5 seconds as `sync=N% @100.0 Hz`.

#### 100 Hz Lock
Shows how well the decoder is aligned to the station's frame structure. Rises as valid position markers are confirmed and falls when frames fail to validate.

The text to the right shows both a **frame countdown** and the **lock state**:
- `42s SYNCING` — syncing, 42 seconds until the next decode attempt
- `15s LOCKED` — locked, collecting the next frame with 15 seconds remaining
- `SEARCHING` — no countdown yet; waiting for the first anchor pulse

#### AGC / Trim
Displays the current gain applied by the input AGC (or the manual trim value if AGC is disabled). A large negative dB value means the input signal is too hot — the AGC is having to cut the level significantly to prevent clipping. If an **amber banner** appears reading "Input level too high — reduce audio input volume", reduce the receiver or SDR output volume until the gain returns to a small positive or near-zero value.

#### Confidence
Number of consecutive Markov-verified time increments. Each count represents one observed +1-minute transition that matched the predicted timeline. The "Set Clock" button and the hours/minutes display activate at 2/2. Each frame takes 60 seconds, so reaching 2/2 from cold start takes approximately 3 minutes (first frame establishes the anchor; two more verify it). Date, DUT1, DST, and leap-second display immediately from the first BCD-valid frame regardless of confidence level.

### Reading the Decoded Time

- **UTC time and date** — time-of-day and day-of-year encoded in the frame; date updates immediately from first BCD-valid decode; time requires Confidence 2/2. Once confirmed, the seconds digit counts up in real time, anchored to the minute-start tone
- **Local time** — UTC time adjusted by the selected UTC offset. Select your **standard-time** offset from the dropdown (e.g. UTC-5 for Eastern); when the DST bit is active the display automatically shifts to the DST equivalent (UTC-4) and appends "DST" to the label
- **DUT1** — difference between UTC and UT1 (Earth rotation time), in ±0.1 s steps
- **DST** — whether US daylight saving time is currently active (WWV/WWVH only)
- **Leap Second** — whether a leap second is scheduled at end of the current month

### Station Reference

Click **"Station Reference Table"** to open the full database of worldwide HF time-signal stations with frequencies, coordinates, and operating status.

---

## How It Works

### The WWV Signal

**WWV** is operated by the US National Institute of Standards and Technology (NIST) and transmits continuously from Fort Collins, Colorado on 2.5, 5, 10, 15, 20, and 25 MHz. **WWVH** transmits from Kekaha, Hawaii on 2.5, 5, 10, and 15 MHz. Both stations serve as free, worldwide references for UTC time and frequency.

The transmitted signal is standard **double-sideband AM** — a shortwave carrier fully amplitude-modulated by audio content. Any AM-mode receiver or SDR demodulates it directly; no special hardware is required beyond a basic HF antenna and receiver.

#### Audio content on the received AM signal

After AM demodulation the audio contains several simultaneous components:

| Component | Frequency | Description |
|-----------|-----------|-------------|
| **100 Hz subcarrier** | 100 Hz | BCD time code — the signal this decoder reads |
| **1000 Hz ticks (WWV)** | 1000 Hz | Second ticks (5 ms) and minute pulse (800 ms) |
| **1200 Hz ticks (WWVH)** | 1200 Hz | Same timing function as WWV, different frequency |
| **Voice announcements** | ~300–3000 Hz | "At the tone, N hours N minutes Coordinated Universal Time" — spoken once per minute |
| **Propagation tones** | 440–600 Hz | Occasional reference tones for propagation research |

The decoder reads both the **100 Hz subcarrier** (BCD time code) and the **1000 Hz tick channel** (second/minute timing). The voice and propagation tones do not interfere — the synchronous detector at 100 Hz rejects them strongly.

#### 100 Hz subcarrier modulation levels

The 100 Hz subcarrier is always present but switches between two amplitude levels to encode data:

- **HIGH (−15 dBc, ~18% modulation depth):** the subcarrier is at full encoding power — yellow bars in the NIST timing diagram
- **LOW (−30 dBc, ~3% modulation depth):** the subcarrier drops to a reduced baseline — red bars

The **duration** of the HIGH period within each second encodes the bit value (NIST IRIG-H positive-pulse format). The carrier is suppressed for the first 30 ms of each second (reserved for the 1 kHz second tick), then rises to HIGH:

| Bit value | HIGH ends at | HIGH duration (net) | Meaning |
|-----------|-------------|---------------------|---------|
| **0 (Zero)** | 200 ms | ~170 ms | Binary 0 |
| **1 (One)** | 500 ms | ~470 ms | Binary 1 |
| **Marker** | 800 ms | ~770 ms | Frame position marker |

#### 1000 Hz tone channel

WWV also broadcasts **1000 Hz tone bursts** in the first 30 ms of each second:

- **Seconds 1–28 and 30–58:** 5 ms tone burst (second tick)
- **Second 0:** 800 ms tone burst (the P0 minute marker — the frame anchor)
- **Seconds 29 and 59:** no tone (omitted per NIST specification)

WWVH uses 1200 Hz for the same purpose. The decoder currently processes the 1000 Hz channel only.

#### BCD frame structure

A complete frame spans 60 seconds (one bit per second, one frame per minute) and encodes:

- Hours and minutes in BCD
- Day of year (1–366) in BCD
- Two-digit year in BCD
- DUT1 correction (UT1 − UTC, in ±0.1 s steps, up to ±0.7 s)
- DST status (US daylight saving time)
- Leap-second pending flag

Six **position markers** (P1–P5 and P0) appear at seconds 9, 19, 29, 39, 49, and 59 to delimit the time code fields. Second 0 is the **frame-reference hole (Pr)** — the 100 Hz carrier stays at LOW for the entire second while the 800 ms 1 kHz minute pulse plays. Seconds 1, 8, 14, 18, 24, 27, 28, 34, and 42–48 are unused and always transmitted as 0.

#### Frequency selection and propagation

HF propagation is ionospheric — signals refract off the ionosphere and reach receivers hundreds to thousands of miles away. Which frequencies propagate depends on solar activity, time of day, and season:

| Frequency | Typical best reception |
|-----------|----------------------|
| 2.5 MHz | Night, short range (<1000 km) |
| 5 MHz | Night and twilight, medium range |
| **10 MHz** | **Most reliable — propagates day and night at mid-latitudes** |
| 15 MHz | Daytime, medium-to-long range |
| 20 MHz | Daytime, long range; less reliable |
| 25 MHz | Daytime, long range; least reliable |

**Recommended starting frequency: 10 MHz.** If 10 MHz is noisy or absent, try 15 MHz during daytime or 5 MHz at night. The decoder works equally well on any frequency — the BCD time code is identical on all of them.

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
    │  Saturation alert: fires when gain < −6 dB (input more than 2× target)
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
    │    Erasures apply ×15/16 decay (NIST d=16 rate) — clean-frame evidence
    │    persists across fades (half-life ≈ 11 min).
    │    Slow bits (DOY, year, DUT1, DST, leap) with a known persistent-store value
    │    use α ≤ 0.10 — requires several consistent frames to override the store.
    │
    │  After each Markov-verified frame, hours and minutes accumulators are pre-seeded
    │  with the expected next-minute value at ±0.40 (only positions below vote threshold).
    │
    │  Vote threshold |acc| ≥ 0.15; below that, three-tier fallback:
    │    1. Persistent store — value from the last successfully validated frame
    │       (covers 27 slow-changing positions: DOY, year, DUT1, DST, leap)
    │    2. Structure default — known marker positions → Marker, data positions → 0
    │
    │  Log shows lowercase characters for erased positions and hits=N/M per frame.
    │
    ▼
[11] Soft BCD Scoring
    │  For each BCD field (minutes, hours, DOY, year), enumerates all structurally
    │  valid integer values and scores each against raw accumulator values.
    │  Score = Σ acc[i] × (bitSet ? +1 : −1) over each field bit position.
    │  Highest-scoring valid value replaces the threshold-voted bit pattern.
    │  Handles the common marginal case where one bit straddles the threshold and
    │  the hard vote would produce an invalid BCD digit (e.g., minutes-tens = 6).
    │
    ▼
[12] BCD Decoder + Validation
    │  Checks 6 position markers at seconds 9,19,29,39,49,59 (P1–P5 and P0)
    │  Second 0 (frame-reference hole) validated as reserved zero, not a marker
    │  Rejects frames with >11 total markers (>5 spurious) — indicates heavy corruption
    │  Validates 16 unused/reserved bit positions (always 0 in a clean transmission)
    │  Decodes BCD fields: minutes, hours, day-of-year, year, DUT1 sign/magnitude
    │  Sanity checks: minutes ≤59, hours ≤23, doy 1–366, year ≤99, DUT1 magnitude ≤0.7 s
    │
    │  Per-field confidence flags:
    │    SlowFieldsConfident — BCD decode + date gate passed; date panel updates now
    │    HoursMinutesConfident — Markov clock check also passed; time display eligible
    │
    │  Wall-clock Markov validation: compares decoded time to wall-clock-anchored expected.
    │   Drift >30 s rejects the frame for time display but still publishes date fields.
    │
    ▼
UI Display + Optional Clock Set
    │  Date/DOY/DUT1/DST/leap: update on any SlowFieldsConfident frame
    │  HH:mm:ss: updates only after HoursMinutesConfident + Confidence ≥ 2/2
```

---

### Filter Details

#### Input AGC
A peak-following automatic gain control normalizes audio level before the DSP chain. The slow time constants (3 s attack, 5 s decay) are designed for HF fading: the 3 s attack causes only ~7% gain change during a 200 ms Zero pulse LOW period and ~23% during an 800 ms Marker — well within the PulseDetector's adaptive threshold range. Without AGC, disabled SDR AGC or deep HF fades cause `levelHigh` to undertrack by 2–3×, pushing the exit threshold down to the WWV LOW carrier level and making every ionospheric flicker look like a pulse. Gain is clamped to a maximum of 500× to prevent amplifying pure noise into apparent signal.

When the AGC gain falls below −6 dB (gain < 0.5×, meaning the raw input is more than twice the target level), an amber banner appears in the Signal panel warning the user to reduce input volume. Over-driven audio clips pulse edges and corrupts the matched-filter's HIGH-duration measurement, typically causing an abnormally high Marker rate and preventing lock.

**Minute-boundary gain recovery:** The 800 ms minute pulse (1000 Hz) raises the overall audio peak, causing the AGC to partially suppress gain during the pulse. With a 5 s decay τ, that suppression would persist for 15–20 s post-pulse — long enough for the gain to be still ~40% below normal when seconds 1–4 tick, making those ticks too weak for reliable detection. To prevent this, `SnapshotLevel()` is called on every SecondTick (capturing the running `_level` before the pulse arrives), and `BeginFastRecovery()` is called the moment the minute pulse event fires. BeginFastRecovery restores `_level` to the pre-pulse snapshot, returning gain to exactly its normal running value immediately. A simple snap to the AGC target (gain = 1×) would over-correct in the opposite direction — at 1× the ticks would be weaker than at the natural post-pulse gain of ~3×, which is itself lower than the running pre-pulse gain of ~5×. Restoring the snapshot is the only approach that avoids both the 5 s suppression and the 1× under-correction.

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
- ≥ 700 ms → **MinutePulse** (nominal 800 ms — the P0 minute marker)
- Other durations are discarded (no valid WWV tone has an intermediate length)

**Tick-index safety clamp:** The bit index derived from each second tick is `round(elapsed_since_anchor) % 60`. C# `%` preserves sign for negative operands, so a future-skewed anchor (anchor set slightly ahead of the actual P0 boundary) could produce negative indices that would cause every subsequent 100 Hz pulse to be discarded by the tick-snap alignment check. The decoder clamps any negative result to `tickBitRaw + 60` and logs a warning, allowing the frame to continue collecting rather than stalling silently.

**Post-minute-pulse threshold reset:** The 800 ms minute pulse drives `_levelHigh` to full 1000 Hz amplitude. With the 3 s decay τ, the enter threshold (50% of `_levelHigh`) stays above the 5 ms tick amplitude for approximately 4 s post-pulse — long enough for seconds 1–4 to be missed entirely. When the MinutePulse event fires, `_levelHigh` is immediately zeroed. This forces the enter threshold to fall back to `8 × noiseFloor`, where a genuine 5 ms tick at any reasonable signal level easily crosses it. The very first second tick (~144 ms after the minute pulse ends) retrains `_levelHigh` to the correct 5 ms tick amplitude for all subsequent ticks in that minute.

#### Adaptive Lowpass
The synchronous detector's lowpass defaults to **2 Hz** — a narrow bandwidth that maximizes noise rejection for stable signals. When the pulse detector's `IsAmplitudeUnstable` flag fires (rapid envelope swings indicating HF ionospheric multipath), the lowpass widens to **8 Hz** so the detector can track the faster envelope transitions during fading. It returns to 2 Hz once conditions stabilize.

No carrier PLL is used. The 100 Hz subcarrier is derived directly from the NIST atomic clock standard and is amplitude-keyed (on/off) — it is not frequency-modulated. The 100 Hz frequency in AM-demodulated baseband audio is exact by definition: the SDR local-oscillator offset shifts the HF carrier but the 100 Hz subcarrier is generated by dividing the station's on-site atomic standard, so it remains at exactly 100 Hz after AM demodulation regardless of receiver tuning error. A frequency-tracking PLL would be solving a problem that does not exist.

#### Pulse Detector
Converts the amplitude envelope into discrete pulse events by measuring the duration of the positive-pulse HIGH period following each second tick. Detection is tick-anchored: each 1 kHz `NotifyTick()` call closes any open pulse from the previous second and arms a 200 ms rising-edge window for the next one.

`levelHigh` is tracked by two mechanisms with different purposes:

**Real-time IIR** (100 ms attack, 30 ms fast-recovery attack, 3 s decay): drives the per-sample `enterThreshold` and `exitThreshold`. Attack is gated to HIGH-only periods so noise during a LOW period cannot inflate the reference and lock the detector in the pulse state indefinitely. The fast-recovery branch (30 ms τ) snaps the tracker back after a deep fade where the IIR has decayed to ~69% of the true carrier.

**75th-percentile of recent inter-pulse peaks**: each time a pulse starts, the peak envelope from the preceding HIGH period is pushed into a 30-entry circular window. The 75th percentile of this window is used as the reference for pulse classification (see Matched Filter below). This separates the two concerns: the IIR reacts fast enough for threshold detection; the percentile provides a stable reference that is resistant to both multipath constructive spikes (brief high outliers) and HF-fade-depressed HIGH periods (low outliers).

After the minute pulse ends, `ClearLevelReference()` discards the entire 30-entry percentile window. The window was populated before the AGC gain change that BeginFastRecovery applies, so its percentile value reflects the old gain level. Clearing it forces the reference to relearn from post-pulse signal amplitudes starting with the first tick of the new minute, preventing the stale pre-pulse percentile from producing a midpoint threshold that is too high for the changed gain level.

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

Erased positions (gap-filled, classifiers disagreed, tick-fade-zeroed) apply a slow **×15/16 decay** each minute instead of a targeted update. Clean-frame evidence at ±0.5 survives approximately 11 minutes of faded frames (half-life) before falling below the 0.15 vote threshold.

This is the key insight from Mills' NTP driver 36: *ionospheric fades produce erasures, not wrong votes.* A gap-filled estimate carries no directional evidence and decays passively; it cannot flip an accumulator position that was pushed by a prior clean frame.

The vote rule: if `|acc[i]| ≥ 0.15`, the sign determines the voted bit. Otherwise the three-tier fallback applies:

1. **Persistent slow-bit store** — 27 positions covering day-of-year, year, DUT1, DST, and leap-second warning are retained from the last successfully BCD-validated frame. Since the day changes at most once every 24 hours and the year once per year, these values are almost always correct on subsequent frames. Minutes and hours are deliberately excluded — they change every minute and use cross-frame seeding instead. The operator UTC date hint (see [UTC Date Hint](#utc-date-hint-optional)) seeds this store at startup and also pre-seeds the accumulator to ±0.4, so the hint is immediately authoritative even before the first frame decode.

2. **Structure-aware default** — if the persistent store has no value (cold start) or the position is not a slow-changing field, known WWV structure is used: expected marker positions (9, 19, 29, 39, 49, 59) default to Marker; all other positions (including position 0, the frame-reference hole) default to 0.

The accumulator **persists across re-anchors** (it is not cleared on P0 detection) so evidence from prior clean frames survives minute-boundary fades that force a re-anchor. It is only cleared by a full user-initiated decoder reset.

#### Cross-Frame Hours/Minutes Seeding
After each Markov-verified frame, the hours and minutes accumulator positions are pre-seeded with the expected value for the **next** frame (+1 minute). Only positions where `|acc| < 0.15` (below the vote threshold — no strong existing evidence) are written; positions already carrying strong evidence from the current frame are left untouched.

This mirrors the persistent-store seeding that slow fields receive: even if the next frame has poor propagation and many erased bits, the hours/minutes accumulators already start near ±0.4 in the correct direction rather than at zero. A single confirming measurement in the next frame is then sufficient to cross the vote threshold.

#### Soft BCD Constraint Scoring
After the per-bit accumulator is read and voted bits are assembled, but before calling the BCD decoder, each BCD field is re-evaluated using soft scoring:

For each field (minutes, hours, DOY, year), every structurally valid integer value is scored against the raw accumulator:

```
score(v) = Σ  acc[pos[i]] × (bit_i_of_v ? +1 : −1)
```

The score rewards accumulator values that agree with the candidate's bit pattern and penalizes contradictions. The highest-scoring valid value is encoded back into the voted bits, replacing the hard threshold-voted pattern.

This handles the common marginal case where one bit has an accumulator value just below the ±0.15 threshold, causing the hard vote to fall through to the structural default (zero) and produce an invalid BCD digit. The soft score sees that the accumulator marginally favors One for that bit and selects the valid BCD value that best fits the full evidence, rather than rejecting the frame outright.

Valid value enumerations used:
- **Minutes:** tens 0–5 × units 0–9 = 60 values
- **Hours:** tens 0–2 × units 0–9, capped at 23 = 24 values
- **DOY:** hundreds 0–3 × tens 0–9 × units 0–9, 1–366 = 366 values
- **Year:** tens 0–9 × units 0–9 = 100 values

#### Three-Point Bipolar Discriminator
After each 1000 Hz second tick at position N, the 100 Hz envelope is sampled at two offsets independently of the PulseDetector's threshold-crossing measurement:

- **Sample A @ ~350 ms** — between the Zero (200 ms) and One (500 ms) LOW-period ends
- **Sample B @ ~650 ms** — between the One (500 ms) and Marker (800 ms) LOW-period ends

Classification using the tracked carrier level as a 50% threshold:
- Both **LOW** → **Zero** (HIGH period ended before 350 ms — carrier dropped at 200 ms)
- A **HIGH**, B **LOW** → **One** (HIGH period ended between 350 ms and 650 ms — carrier dropped at 500 ms)
- Both **HIGH** → Marker erasure (carrier still HIGH at 650 ms — drops at 800 ms; the 100 Hz channel classifies Markers)
- A **LOW**, B **HIGH** → erasure (multipath or measurement artefact)

This provides a second independent measurement that directly updates the accumulator with `α = 0.50`. It is especially valuable during partial fades that extend past the Zero LOW period but not the One LOW period — conditions where the threshold-crossing detector would misclassify the bit, but the discriminator correctly identifies it. It does not help during full broadband fades where both channels are dark simultaneously, but those frames generate erasures rather than wrong votes regardless.

The frame log shows erased positions as lowercase letters (`m`, `0`, `1`) and a `hits=N/M` count per frame where N is confidently-classified pulses and M is total pulses.

#### Frame Integrity Checks
Two structural invariants are checked after each bit is stored, bailing early rather than collecting 60 bits and failing at decode time:

1. **Consecutive Markers** — no valid WWV frame ever has two adjacent Marker bits (minimum marker separation is 9 seconds). A run of consecutive Markers is the signature of HF fades being misclassified. Triggers immediate return to Searching.
2. **Progressive marker check** — at every 10-second boundary (bit positions 10, 20, 30, 40, 50), the preceding position must contain a Marker. This catches misalignment within 10 seconds instead of waiting 60 seconds.

#### BCD Decoder Validation
The decoder applies four validation layers in order:

1. **Marker positions** — all 6 position markers (P1–P5 at seconds 9, 19, 29, 39, 49 and P0 at second 59) must be present. Second 0 is the frame-reference hole (Pr) and is not a position marker — it is validated as a reserved zero instead.
2. **Spurious marker count** — more than 12 total markers (7 expected + 5 spurious) indicates heavy signal corruption and the frame is rejected rather than decoded to a wrong time.
3. **Unused bits** — WWV always transmits 0 at positions 1, 8, 14, 18, 24, 27, 28, 34, and 42–48. A non-zero value at these positions means the frame is misaligned or corrupted.
4. **BCD range checks** — decoded values are checked: minutes ≤ 59, hours ≤ 23, day-of-year 1–366, year ≤ 99, DUT1 magnitude ≤ 0.7 s.

All four must pass for a frame to produce a `TimeFrame` result.

#### Per-Field Confidence and Display Gating

Frames that pass BCD validation are split into two confidence classes before the UI is updated:

**`SlowFieldsConfident`** — set whenever the BCD decode passes all structural checks and the operator date gate (within 14 days of the known date). Date, DOY, DUT1, DST, and leap-second fields update immediately, even if the Markov clock check subsequently rejects the frame's hours/minutes. This means date information is visible from the very first structurally valid decode, typically within 1–2 minutes of locking on.

**`HoursMinutesConfident`** — set only when the Markov clock check also passes (decoded time within 30 s of the wall-clock-anchored expectation). The time display (HH:mm:ss) and the confidence bar only update when this flag is set. The log reports partial frames as `Partial frame: date=YYYY-MM-DD DOY=NNN — time pending Markov verification`.

#### Wall-Clock Markov Clock Validation
After the first successful decode, a wall-clock anchor is established: the decoded UTC time and `DateTime.UtcNow` are recorded together. Each subsequent frame computes the expected time as:

```
expected = decoded_anchor + round(DateTime.UtcNow − wall_anchor_time)
```

Using real elapsed time rather than a per-frame counter prevents drift escalation during propagation outages where many frames are missed entirely (no `TryDecode` fires for several minutes). The per-frame counter would advance only when a frame actually decodes; the wall-clock formula correctly handles any gap.

- **Drift ≤ 30 s** — the frame is accepted, `_clockVerifiedCount` is incremented, and both anchor values are updated to the current decode. The log shows `Verified #N: HH:MM (drift +0.4s from expected)`.
- **Drift > 30 s** — the frame is **rejected for time display** (`HoursMinutesConfident = false`), but `SlowFieldsConfident` remains set so date fields still update. The log shows `Clock mismatch: expected HH:MM got HH:MM (drift ±Ns) — rejected`.
  - **Soft decay:** if the anchor is already well-established (≥3 verifications) and the drift is small (≤90 s), `_clockVerifiedCount` decreases by 1 rather than resetting to 0 — a single noisy frame cannot erase accumulated confidence.
  - **UTC offset fast-path:** if the drift is within 90 s of a whole number of hours (±1h, ±2h, …) and enough time bits were directly observed, the decoder recognises a probable local-time/UTC confusion. It requires **2 consecutive frames** to agree on the same hour offset before re-anchoring — a single noise-corrupted hour field can produce exactly ±1h apparent drift and would otherwise cause an oscillation loop where every re-anchor is immediately reversed by the next frame. The first frame logs `UTC offset pending (+1 h) — waiting for confirmation frame`; the second triggers `UTC offset confirmed (+1 h, 2 frames) — re-anchoring`.
  - **Self-correction:** if the anchor itself is wrong (e.g., seeded from a corrupt first frame), 3 consecutive Markov-failing frames whose decoded times form a consistent +1-minute sequence trigger a re-anchor to the most recent candidate. The candidate queue is checked for freshness: if the most recent entry is older than 90 s it predates a propagation gap and the whole queue is discarded rather than used as the re-anchor source.

**Hours and minutes are only displayed after `_clockVerifiedCount` reaches 2** — two back-to-back Markov-passing frames after the initial anchor. Before that threshold the display shows `--:--:--`. Date, DUT1, and DST are visible immediately from the first BCD-valid frame regardless.

**Known limitation:** the Markov check compares successive decoded times, so it detects a fixed wrong-hours offset only at the moment of transition (when a good frame is followed by a wrong one). If the very first decoded frame has wrong hours *and* subsequent frames decode consistently to the same wrong time, the +1-minute increments will still verify. The two-frame threshold reduces the probability that a noise event produces two plausible-looking consecutive decodes, but does not fully eliminate it. An external time reference (NTP, operator-supplied time hint) would be required to catch this case definitively.

#### Gap Filling
When the signal drops for 2–30 seconds (the cadence guard detects the inter-pulse gap exceeds 2 s), the decoder estimates how many bits were missed using `round(gap) − 1` and fills those positions with default values: known marker positions receive value 2, all other positions receive 0. Filled positions are tagged as erased (not confident) and do not participate in the majority vote against confirmed bits from prior frames. If filling completes a 60-bit frame, decode is attempted immediately. Gaps longer than 30 seconds trigger a full reset to Searching rather than filling — too many unknowns to fill reliably.

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
│   ├── BandpassFilter.cs           # Pre-filter used by TickDetector to isolate 1000 Hz channel
│   ├── SynchronousDetector.cs      # Coherent IQ lock-in detector for 100 Hz subcarrier
│   ├── PulseDetector.cs            # Tick-anchored positive-pulse detection with gated HIGH tracking
│   ├── MatchedFilter.cs            # HIGH-duration matched filter for pulse classification
│   └── TickDetector.cs             # 1000 Hz IQ demodulator; second ticks and minute pulse
│
├── Decoder/
│   ├── DecoderPipeline.cs          # Wires DSP chain → frame decoder; both 100 Hz and 1000 Hz
│   ├── DecoderRuntimeSettingsSnapshot.cs  # User-selectable options snapshot (AGC, adaptive LP, trim)
│   ├── FrameDecoder.cs             # Searching/Syncing/Locked state machine; accumulator voting;
│   │                               #   soft BCD scoring; cross-frame time seeding; per-field confidence
│   ├── FrameCell.cs                # Per-bit display state (Confident/Erased/GapFilled/Corrected)
│   ├── BcdDecoder.cs               # 60-bit BCD frame parser with reserved-bit validation
│   ├── TimeFrame.cs                # Decoded time data (UTC, DUT1, DST, leap, per-field confidence flags)
│   └── SignalStatus.cs             # Signal/lock/subcarrier/saturation reporting
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
    ├── MainViewModel.cs            # MVVM application logic; per-field display gating
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
7. Date and DUT1 appear as soon as the first BCD-valid frame is decoded (~1–2 min)
8. Wait for Confidence 2/2 (hours and minutes confirmed by 2 consecutive Markov-verified increments, ~3 min total)
9. Click "Set Clock" to synchronize Windows time to UTC
```

The log shows the exact time set and the delta applied (e.g., `Clock set to 14:37:22 UTC. Delta was +342.0 ms`).

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
8. If an amber "Input level too high" banner appears, reduce the SDR output volume
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
5. After achieving lock (Confidence 2/2), use "Set Clock" to correct drift
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
   - How date fields appear immediately while hours/minutes wait for Markov verification
```

---

## Troubleshooting

| Symptom | Likely Cause | Fix |
|---------|-------------|-----|
| Signal Level > 0 but 100 Hz Level = 0 | Receiver not in AM mode | Switch SDR/receiver to AM mode |
| Both meters at 0 | No audio reaching the app | Check device selection and audio routing; check SDR volume |
| Amber "Input level too high" banner | Audio input is over-driven; AGC gain < −6 dB | Reduce receiver or SDR output volume until banner clears |
| Stuck on "Searching" | No anchor pulses detected | Signal too weak, wrong frequency, or not AM mode |
| Log shows "Signal too faded" | >60% of pulses are Marker-length | Deep ionospheric fade; try a different frequency or wait for propagation to improve |
| Log shows "Bad gap N.Ns" repeatedly | Marker noise between frames | Expected during fade; decoder is correctly rejecting non-9-second gaps |
| Stuck on "Syncing" — countdown resets every ~10 s | Progressive marker check failing | Signal misaligned; decoder bails within 10 s and retries |
| Countdown runs to 0 but no lock | Reserved bits or markers failing | Signal too noisy for reliable frame alignment; let it run or try a different frequency |
| Date shows but time shows `--:--:--` | Markov verification not yet reached | Normal — date appears immediately; time requires Confidence 2/2 (~3 min from cold start) |
| Log shows "Partial frame: date confirmed, time pending" | BCD valid but Markov clock check rejected the time | Expected on first decode or after signal gap; wait for next frame to verify |
| Decodes but time is wrong | Recording from a different date | Expected for old recordings — the encoded time is when it was recorded |
| Log shows "Clock mismatch … rejected" | Decoded time inconsistent with prior frame | Decoder rejected the frame and will re-verify; corrects automatically within 1–2 frames if the signal is stable |
| "Set Clock" button grayed out | Confidence below 2/2 | Wait for Confidence to reach 2/2 — hours/minutes must be Markov-verified before clock set is enabled |
| Log shows "Clock set skipped: wall anchor is N s old" | No minute pulse received in the last 90 s — anchor is stale | Wait for the next minute pulse (watch for the minute dot to flash), then retry Set Clock |
| App requires Administrator | Needed for SetSystemTime() | Right-click → Run as Administrator |
| Crash on start | Missing .NET 9 runtime | Use the self-contained published build, or install .NET 9 |

---

## License

This project is not currently licensed. All rights reserved.
