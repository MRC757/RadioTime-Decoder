namespace WwvDecoder.Dsp;

/// <summary>
/// Second-order IIR Butterworth highpass filter (biquad) for removing DC offset and
/// low-frequency noise from SDR/receiver audio before the 100 Hz bandpass filter.
///
/// A 20 Hz cutoff eliminates:
///   - DC offset (common in SDR software audio pipelines)
///   - Low-frequency hum and electrical interference
///   - Audio rumble below the 100 Hz subcarrier region
///
/// The 100 Hz subcarrier passes through with less than 0.1 dB attenuation.
/// Uses direct-form II transposed structure for numerical stability.
/// </summary>
public class HighpassFilter
{
    private readonly double _b0, _b1, _b2;
    private readonly double _a1, _a2;
    private double _w1, _w2;

    public HighpassFilter(int sampleRate, double cutoffHz = 20.0)
    {
        double w0    = 2.0 * Math.PI * cutoffHz / sampleRate;
        double cosW0 = Math.Cos(w0);
        double alpha = Math.Sin(w0) / (2.0 * Math.Sqrt(2.0)); // Butterworth Q = 1/√2
        double a0    = 1.0 + alpha;

        _b0 =  (1.0 + cosW0) / (2.0 * a0);
        _b1 = -(1.0 + cosW0) / a0;
        _b2 =  (1.0 + cosW0) / (2.0 * a0);
        _a1 = -2.0 * cosW0 / a0;
        _a2 =  (1.0 - alpha) / a0;
    }

    public void Reset() => _w1 = _w2 = 0;

    public double Process(double x)
    {
        double w = x - _a1 * _w1 - _a2 * _w2;
        double y = _b0 * w + _b1 * _w1 + _b2 * _w2;
        _w2 = _w1;
        _w1 = w;
        return y;
    }

    public float[] ProcessBlock(float[] input)
    {
        var output = new float[input.Length];
        for (int i = 0; i < input.Length; i++)
            output[i] = (float)Process(input[i]);
        return output;
    }
}
