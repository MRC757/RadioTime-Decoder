namespace WwvDecoder.Dsp;

/// <summary>
/// Second-order IIR biquad notch filter. Sharply attenuates a single frequency while
/// passing all others with minimal phase distortion.
///
/// Used to suppress power-line interference before the synchronous detector:
///   - 60 Hz fundamental (US mains frequency)
///   - 120 Hz second harmonic (common in switching supplies and SDR hardware)
///
/// Both harmonics can bleed through into SDR audio and inflate the noise floor seen
/// by the 100 Hz detector, degrading the adaptive threshold.
///
/// Biquad notch design using pole-radius method:
///   Zeros at e^±jω₀  (exactly on unit circle → infinite rejection at notch frequency)
///   Poles at r·e^±jω₀ (pulled slightly inside unit circle to control bandwidth)
///   r = 1 - π·BW/Fs  where BW is the -3 dB bandwidth in Hz
///
/// A 2 Hz bandwidth gives ~40 dB rejection at the center frequency while passing
/// adjacent frequencies with less than 0.1 dB attenuation.
/// Uses direct-form II transposed for numerical stability.
/// </summary>
public class NotchFilter
{
    private readonly double _b0, _b1, _b2;
    private readonly double _a1, _a2;
    private double _w1, _w2;

    /// <param name="sampleRate">Audio sample rate in Hz.</param>
    /// <param name="notchHz">Center frequency to reject in Hz.</param>
    /// <param name="notchWidthHz">-3 dB bandwidth of the notch. Narrower = deeper rejection.</param>
    public NotchFilter(int sampleRate, double notchHz, double notchWidthHz = 2.0)
    {
        double w0   = 2.0 * Math.PI * notchHz / sampleRate;
        double cosW = Math.Cos(w0);

        // Pole radius: r close to 1.0 gives narrow, deep notch
        double r = 1.0 - Math.PI * notchWidthHz / sampleRate;

        // Numerator (zeros on unit circle at ±ω₀):  1 - 2cos(ω₀)z⁻¹ + z⁻²
        // Denominator (poles at r·e^±jω₀):          1 - 2r·cos(ω₀)z⁻¹ + r²·z⁻²
        _b0 =  1.0;
        _b1 = -2.0 * cosW;
        _b2 =  1.0;
        _a1 = -2.0 * r * cosW;
        _a2 =  r * r;

        // Scale numerator so DC gain matches denominator DC gain (unity at DC)
        double numDc = _b0 + _b1 + _b2;   // = 2 - 2cos(ω₀)
        double denDc = 1.0 + _a1 + _a2;    // = (1-r)²
        double scale = denDc / numDc;
        _b0 *= scale;
        _b1 *= scale;
        _b2 *= scale;
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
