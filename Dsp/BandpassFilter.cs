namespace WwvDecoder.Dsp;

/// <summary>
/// Second-order IIR bandpass filter (biquad) centered on the WWV 100 Hz time-code subcarrier.
/// Implemented as a direct-form II transposed biquad for numerical stability.
///
/// WWV/WWVB broadcasts a 100 Hz amplitude-modulated subcarrier that encodes the time code.
/// This filter isolates that 100 Hz signal from the radio receiver's audio output, rejecting
/// other frequencies and narrowband noise. A 10 Hz bandwidth (±5 Hz around 100 Hz) is tight
/// enough to suppress interference from power-line hum (60 Hz) and other nearby signals,
/// while remaining fast enough to track the pulse edges accurately.
///
/// The biquad coefficients are computed using standard EQ formulas based on the sample rate,
/// center frequency, and bandwidth, then normalized to ensure numerical stability.
/// </summary>
public class BandpassFilter
{
    // Biquad coefficients in direct-form II structure (computed once at construction)
    // These normalize the filter transfer function H(z) = (b0 + b1*z^-1 + b2*z^-2) / (1 + a1*z^-1 + a2*z^-2)
    private readonly double _b0, _b1, _b2;
    private readonly double _a1, _a2;

    // State variables for two cascaded biquad stages.
    // Stage 1 and Stage 2 share the same coefficients; cascading doubles the filter
    // order (2nd → 4th), giving 24 dB/oct roll-off and ~40 dB rejection at 60 Hz
    // vs. ~20 dB from the single-stage design. The in-band response at 100 Hz
    // remains unity; the -3 dB bandwidth narrows from 10 Hz to ~6.4 Hz.
    private double _w1, _w2;   // stage 1 delay-line state
    private double _w3, _w4;   // stage 2 delay-line state

    /// <summary>
    /// Creates a bandpass filter with the given center frequency and bandwidth.
    /// </summary>
    /// <param name="sampleRate">Audio sample rate in Hz (e.g. 22050 Hz for 22.05 kHz audio)</param>
    /// <param name="centerHz">Centre frequency in Hz. Default 100.0 for WWV subcarrier.</param>
    /// <param name="bandwidthHz">Bandwidth at -3 dB (half-power points).
    /// Default 10.0 Hz gives a pass band from 95 Hz to 105 Hz, which is narrow enough to
    /// reject 60 Hz line hum and other interference while tracking pulse edges.</param>
    public BandpassFilter(int sampleRate, double centerHz = 100.0, double bandwidthHz = 10.0)
    {
        // Compute the normalized angular frequency (radians per sample)
        double w0 = 2.0 * Math.PI * centerHz / sampleRate;

        // Compute Q-factor bandwidth parameter (alpha) from the desired -3dB bandwidth.
        // Wider bandwidth → larger alpha → higher Q-factor → narrower filter.
        double alpha = Math.Sin(w0) * Math.Sinh(Math.Log(2) / 2.0 * bandwidthHz / centerHz * w0 / Math.Sin(w0));

        // Normalize the coefficients by the a0 term
        double a0 = 1.0 + alpha;
        _b0 =  alpha / a0;
        _b1 =  0.0;                    // Middle term is zero for bandpass design
        _b2 = -alpha / a0;
        _a1 = -2.0 * Math.Cos(w0) / a0;
        _a2 = (1.0 - alpha) / a0;
    }

    public void Reset() => _w1 = _w2 = _w3 = _w4 = 0;

    /// <summary>
    /// Process a single sample through the cascaded 4th-order bandpass filter.
    /// Two identical biquad stages in series: stage 1 output feeds stage 2 input.
    /// Uses direct-form II transposed structure for numerical stability.
    /// </summary>
    public double Process(double x)
    {
        // Stage 1
        double w1 = x - _a1 * _w1 - _a2 * _w2;
        double y1 = _b0 * w1 + _b1 * _w1 + _b2 * _w2;
        _w2 = _w1;
        _w1 = w1;

        // Stage 2 (feeds on stage 1 output)
        double w2 = y1 - _a1 * _w3 - _a2 * _w4;
        double y2 = _b0 * w2 + _b1 * _w3 + _b2 * _w4;
        _w4 = _w3;
        _w3 = w2;

        return y2;
    }

    /// <summary>Filter an array of samples in-place, returning a new array.</summary>
    public float[] ProcessBlock(float[] input)
    {
        var output = new float[input.Length];
        for (int i = 0; i < input.Length; i++)
            output[i] = (float)Process(input[i]);
        return output;
    }
}
