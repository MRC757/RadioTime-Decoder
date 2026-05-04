namespace WwvDecoder.Dsp;

/// <summary>
/// Synthesizes a WWV-like AM signal at 100 Hz subcarrier for simulation and loopback testing.
///
/// Signal model (same as real WWV):
///   - 100 Hz sinusoidal subcarrier, amplitude = 1.0 (HIGH carrier level)
///   - After a 30 ms suppression gap, the carrier rises to HIGH and stays there until it is
///     reduced to <see cref="LowAmplitude"/> at a time that encodes the bit value:
///       Zero   → reduced at 200 ms past the second (170 ms HIGH net)
///       One    → reduced at 500 ms past the second (470 ms HIGH net)
///       Marker → reduced at 800 ms past the second (770 ms HIGH net)
///   - Position markers (bits 9,19,29,39,49,59 = P1–P5 and P0) are always Marker pulses.
///     Bit 0 is the frame-reference hole (Pr): 100 Hz carrier stays LOW for the entire second.
///   - 1000 Hz second ticks mixed into the output:
///       Second 0:        800 ms 1 kHz tone (minute marker — same duration as P0)
///       Seconds 1–28, 30–58: 5 ms 1 kHz tone (second ticks)
///       Seconds 29, 59: no 1 kHz tone (omitted per NIST)
///   - Configurable Gaussian noise allows SNR testing.
/// </summary>
public class WwvSignalGenerator
{
    private readonly int _sampleRate;
    private readonly Random _rng;

    // 100 Hz subcarrier phase accumulator
    private double _carrierPhase;
    // 1000 Hz tick oscillator phase accumulator
    private double _tickPhase;

    // Current playback position within a 60-second frame
    private int _secondIndex;       // bit index (0..59)
    private int _sampleInSecond;    // sample offset within the current second

    // The BCD frame being played (60 ints: 0=zero, 1=one, 2=marker)
    private readonly int[] _frame = new int[60];

    // 100 Hz BCD pulse state (NIST IRIG-H positive-pulse model):
    // Each second (1–59) suppresses the carrier for 30ms, then goes HIGH for the bit
    // duration, then falls LOW for the remainder.  Second 0 is the frame-reference
    // "hole" (Pr) — carrier stays LOW for the entire second.
    private bool _inHigh;                 // true = currently in the HIGH bit period
    private int  _highSamplesRemaining;   // samples left in the HIGH period
    private int  _highStartDelaySamples;  // 30ms suppression countdown before HIGH begins

    // 1 kHz tick state
    private bool _tickActive;
    private int _tickSamplesRemaining;

    /// <summary>Carrier amplitude during the HIGH (normal) period.</summary>
    public double HighAmplitude { get; set; } = 1.0;

    /// <summary>Carrier amplitude during the LOW (pulse) period. Nominal WWV = 0.316 (−10 dB).</summary>
    public double LowAmplitude { get; set; } = 0.316;

    /// <summary>1 kHz tick amplitude. Nominal WWV ≈ same order as HighAmplitude.</summary>
    public double TickAmplitude { get; set; } = 0.5;

    /// <summary>Gaussian noise sigma relative to HighAmplitude. 0 = perfect signal.</summary>
    public double NoiseSigma { get; set; } = 0.0;

    public WwvSignalGenerator(int sampleRate, int? seed = null)
    {
        _sampleRate = sampleRate;
        _rng = seed.HasValue ? new Random(seed.Value) : new Random();
        SetTime(DateTime.UtcNow);
    }

    /// <summary>
    /// Encode the given UTC time into the 60-bit WWV frame and reset playback to the
    /// start of that minute. Playback begins at second 0 (the frame-reference hole).
    /// </summary>
    public void SetTime(DateTime utcTime)
    {
        EncodeTime(utcTime);
        _secondIndex            = 0;
        _sampleInSecond         = 0;
        _inHigh                 = false;
        _highSamplesRemaining   = 0;
        _highStartDelaySamples  = 0;
        _tickActive             = false;
        _tickSamplesRemaining   = 0;
    }

    /// <summary>
    /// Generate <paramref name="count"/> audio samples at the current playback position.
    /// Call repeatedly (e.g. once per 50 ms block) to feed the decoder pipeline.
    /// </summary>
    public float[] GenerateBlock(int count)
    {
        var output = new float[count];
        double carrierOmega = 2.0 * Math.PI * 100.0  / _sampleRate;
        double tickOmega    = 2.0 * Math.PI * 1000.0 / _sampleRate;

        for (int i = 0; i < count; i++)
        {
            // Advance to next second if needed
            if (_sampleInSecond == 0)
                StartSecond(_secondIndex);

            // 100 Hz BCD carrier amplitude.
            // Per NIST: 30ms suppression gap before HIGH begins (seconds 1–59).
            // Second 0 (hole): _highStartDelaySamples and _highSamplesRemaining both 0.
            double amplitude;
            if (_highStartDelaySamples > 0)
            {
                _highStartDelaySamples--;
                if (_highStartDelaySamples == 0 && _highSamplesRemaining > 0)
                    _inHigh = true;
                amplitude = LowAmplitude;
            }
            else if (_inHigh && _highSamplesRemaining > 0)
            {
                amplitude = HighAmplitude;
                _highSamplesRemaining--;
                if (_highSamplesRemaining == 0) _inHigh = false;
            }
            else
            {
                amplitude = LowAmplitude;
            }

            // 1 kHz tick component
            double tickSample = 0.0;
            if (_tickActive && _tickSamplesRemaining > 0)
            {
                tickSample = TickAmplitude * Math.Sin(_tickPhase);
                _tickSamplesRemaining--;
                if (_tickSamplesRemaining == 0) _tickActive = false;
            }

            // Mix 100 Hz carrier + 1 kHz tick + optional noise
            double noise = NoiseSigma > 0 ? SampleGaussian() * NoiseSigma : 0.0;
            output[i] = (float)(amplitude * Math.Sin(_carrierPhase) + tickSample + noise);

            _carrierPhase += carrierOmega;
            if (_carrierPhase >= Math.PI * 2.0) _carrierPhase -= Math.PI * 2.0;

            _tickPhase += tickOmega;
            if (_tickPhase >= Math.PI * 2.0) _tickPhase -= Math.PI * 2.0;

            _sampleInSecond++;
            if (_sampleInSecond >= _sampleRate)
            {
                _sampleInSecond = 0;
                _secondIndex = (_secondIndex + 1) % 60;
            }
        }

        return output;
    }

    private void StartSecond(int bitIndex)
    {
        if (bitIndex == 0)
        {
            // Second 0 = frame reference "hole" (Pr): 100Hz carrier stays LOW for the
            // entire second — no positive-going edge, no BCD pulse.  The 800ms 1kHz
            // minute marker is the only signal and serves as the frame anchor.
            _inHigh                = false;
            _highSamplesRemaining  = 0;
            _highStartDelaySamples = 0;
            _tickActive            = true;
            _tickSamplesRemaining  = (int)(0.800 * _sampleRate);
            return;
        }

        // NIST Figure 2.6: 100Hz carrier is suppressed for the first 30ms of each second
        // (1–59) — 10ms before the 1kHz tick + 5ms tick + 25ms after = 30ms total gap.
        // The positive-going edge occurs at t+30ms.  Pulse durations are IRIG-H values
        // minus the 30ms deletion: Marker 770ms, One 470ms, Zero 170ms.
        _inHigh                = false;
        _highStartDelaySamples = (int)(0.030 * _sampleRate);
        _highSamplesRemaining  = _frame[bitIndex] switch
        {
            2 => (int)(0.770 * _sampleRate), // Marker
            1 => (int)(0.470 * _sampleRate), // One
            _ => (int)(0.170 * _sampleRate)  // Zero
        };

        // 1kHz ticks are omitted at seconds 29 and 59 per NIST specification.
        if (bitIndex != 29 && bitIndex != 59)
        {
            _tickActive           = true;
            _tickSamplesRemaining = (int)(0.005 * _sampleRate);
        }
        else
        {
            _tickActive           = false;
            _tickSamplesRemaining = 0;
        }
    }

    private void EncodeTime(DateTime utc)
    {
        Array.Clear(_frame, 0, 60);

        // Six position markers: P1–P5 at seconds 9,19,29,39,49 and P0 at second 59.
        // Second 0 (bit 0) is the frame-reference hole (Pr) — NOT a position marker.
        foreach (int m in new[] { 9, 19, 29, 39, 49, 59 })
            _frame[m] = 2;

        // Correct NIST IRIG-H positions (matches BcdDecoder.cs):
        //   Minutes units [10-13], tens [15-17]  (skip 14)
        //   Hours units   [20-23], tens [25-26]  (skip 24)
        //   DOY units     [30-33], tens [35-38], hundreds [40-41]  (skip 34; P4@39)
        //   Year units    [ 4- 7], tens [51-54]
        EncodeBcd(utc.Minute,      [10, 11, 12, 13, 15, 16, 17],
                                   [ 1,  2,  4,  8, 10, 20, 40]);
        EncodeBcd(utc.Hour,        [20, 21, 22, 23, 25, 26],
                                   [ 1,  2,  4,  8, 10, 20]);
        EncodeBcd(utc.DayOfYear,   [30, 31, 32, 33, 35, 36, 37, 38, 40, 41],
                                   [ 1,  2,  4,  8, 10, 20, 40, 80, 100, 200]);
        EncodeBcd(utc.Year % 100,  [4,  5,  6,  7,  51, 52, 53, 54],
                                   [1,  2,  4,  8,  10, 20, 40, 80]);

        // DUT1 = 0: sign positive (bit 50 = 1), magnitude bits 56-58 all zero
        _frame[50] = 1;
    }

    private void EncodeBcd(int value, int[] positions, int[] weights)
    {
        int remaining = value;
        for (int i = positions.Length - 1; i >= 0 && remaining > 0; i--)
        {
            if (remaining >= weights[i])
            {
                _frame[positions[i]] = 1;
                remaining -= weights[i];
            }
        }
    }

    // Box-Muller Gaussian sample
    private double SampleGaussian()
    {
        double u1 = 1.0 - _rng.NextDouble();
        double u2 = 1.0 - _rng.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);
    }
}
