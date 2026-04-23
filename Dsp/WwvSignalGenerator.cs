namespace WwvDecoder.Dsp;

/// <summary>
/// Synthesizes a WWV-like AM signal at 100 Hz subcarrier for simulation and loopback testing.
///
/// Signal model (same as real WWV):
///   - 100 Hz sinusoidal subcarrier, amplitude = 1.0 (HIGH carrier level)
///   - At the start of each second the carrier is reduced to <see cref="LowAmplitude"/> for
///     a duration determined by the BCD pulse type:
///       Zero   = 0.200 s
///       One    = 0.500 s
///       Marker = 0.800 s
///   - Position markers (bits 0,9,19,29,39,49,59) are always Marker pulses.
///   - Configurable Gaussian noise allows SNR testing.
///
/// Usage:
///   var gen = new WwvSignalGenerator(sampleRate: 22050);
///   gen.SetTime(DateTime.UtcNow);
///   float[] block = gen.GenerateBlock(blockSize);
///   // Feed 'block' into DecoderPipeline.ProcessSamples()
/// </summary>
public class WwvSignalGenerator
{
    private readonly int _sampleRate;
    private readonly Random _rng;

    // Carrier phase accumulator (0..2π)
    private double _carrierPhase;

    // Current playback position within a 60-second frame
    private int _framePosition;     // sample index within current frame (0..60×sampleRate-1)
    private int _secondIndex;       // bit index (0..59)
    private int _sampleInSecond;    // sample offset within the current second

    // The BCD frame being played (60 ints: 0=zero, 1=one, 2=marker)
    private readonly int[] _frame = new int[60];

    // Whether we are currently in a LOW period
    private bool _inLow;
    private int _lowSamplesRemaining;

    /// <summary>Carrier amplitude during the HIGH (normal) period.</summary>
    public double HighAmplitude { get; set; } = 1.0;

    /// <summary>Carrier amplitude during the LOW (pulse) period. Nominal WWV = 0.316 (−10 dB).</summary>
    public double LowAmplitude { get; set; } = 0.316;

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
    /// start of that minute. The frame begins playing at the P0 marker (second 0).
    /// </summary>
    public void SetTime(DateTime utcTime)
    {
        EncodeTime(utcTime);
        _secondIndex      = 0;
        _sampleInSecond   = 0;
        _inLow            = false;
        _lowSamplesRemaining = 0;
    }

    /// <summary>
    /// Generate <paramref name="count"/> audio samples at the current playback position.
    /// Call repeatedly (e.g. once per 50 ms block) to feed the decoder pipeline.
    /// </summary>
    public float[] GenerateBlock(int count)
    {
        var output = new float[count];
        double omega = 2.0 * Math.PI * 100.0 / _sampleRate;

        for (int i = 0; i < count; i++)
        {
            // Advance to next second if needed
            if (_sampleInSecond == 0)
                StartSecond(_secondIndex);

            // Determine current amplitude (HIGH or LOW)
            double amplitude;
            if (_inLow && _lowSamplesRemaining > 0)
            {
                amplitude = LowAmplitude;
                _lowSamplesRemaining--;
                if (_lowSamplesRemaining == 0) _inLow = false;
            }
            else
            {
                amplitude = HighAmplitude;
            }

            // Generate carrier sample with optional noise
            double noise = NoiseSigma > 0 ? SampleGaussian() * NoiseSigma : 0.0;
            output[i] = (float)(amplitude * Math.Sin(_carrierPhase) + noise);

            _carrierPhase += omega;
            if (_carrierPhase >= Math.PI * 2.0) _carrierPhase -= Math.PI * 2.0;

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
        _inLow = true;
        int bit = _frame[bitIndex];
        double lowDuration = bit switch
        {
            2 => 0.770, // Marker (position identifier, per NIST spec)
            1 => 0.470, // One    (weighted code digit, per NIST spec)
            _ => 0.170  // Zero   (index marker / unweighted code, per NIST spec)
        };
        _lowSamplesRemaining = (int)(lowDuration * _sampleRate);
    }

    private void EncodeTime(DateTime utc)
    {
        Array.Clear(_frame, 0, 60);

        // Position markers
        foreach (int m in new[] { 0, 9, 19, 29, 39, 49, 59 })
            _frame[m] = 2;

        int minutes = utc.Minute;
        int hours   = utc.Hour;
        int doy     = utc.DayOfYear;
        int year    = utc.Year % 100;

        // Minutes: pos 1-4 (units), 6-8 (tens)
        EncodeBcd(minutes, [1, 2, 3, 4, 6, 7, 8], [1, 2, 4, 8, 10, 20, 40]);
        // Hours: pos 12-15 (units), 17-18 (tens)
        EncodeBcd(hours,   [12, 13, 14, 15, 17, 18], [1, 2, 4, 8, 10, 20]);
        // DOY: pos 22-25, 27-28, 30-31, 33-34
        EncodeBcd(doy,     [22, 23, 24, 25, 27, 28, 30, 31, 33, 34], [1, 2, 4, 8, 10, 20, 40, 80, 100, 200]);
        // Year: pos 45-48 (units), 50-53 (tens)
        EncodeBcd(year,    [45, 46, 47, 48, 50, 51, 52, 53], [1, 2, 4, 8, 10, 20, 40, 80]);

        // DUT1: set positive sign only (simplification — DUT1 = 0)
        _frame[36] = 0;
        _frame[37] = 0;
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
