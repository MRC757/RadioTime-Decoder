namespace WwvDecoder.Dsp;

/// <summary>
/// Adaptive Line Enhancer (ALE) using Normalized Least Mean Squares (NLMS).
///
/// Extracts the periodic 100 Hz subcarrier from broadband interference (voice, tones,
/// static) by exploiting temporal correlation. A delayed copy of the input predicts
/// the current sample via an adaptive FIR filter. For a periodic signal, the delayed
/// version is highly correlated; for broadband noise it is not — so the filter output
/// converges to the periodic component while noise is suppressed.
///
/// Why 5-sample delay:
///   The 100 Hz subcarrier has a period of 220 samples at 22050 Hz. A 5-sample delay
///   gives a correlation of cos(2π×5/220) = 0.99 — nearly perfect.
///   For broadband noise, correlation at 5-sample lag is near zero.
///   This makes the ALE converge to the dominant periodic signal in the audio.
///
/// NLMS vs LMS:
///   The normalized step size μ_eff = μ / (ε + ||x||²) gives stable convergence
///   regardless of input signal level — important since SDR audio levels vary widely.
///
/// Expected SNR improvement: 5–15 dB on broadband noise, depending on signal content.
/// </summary>
public class AdaptiveLineEnhancer
{
    private readonly int _delay;
    private readonly int _taps;
    private readonly double _mu;       // NLMS step size (0 < μ < 2; 0.5 is typical)
    private readonly double[] _weights;
    private readonly double[] _buf;    // circular delay-line buffer
    private readonly int _bufLen;
    private int _writeIdx;

    /// <param name="delay">Decorrelation delay in samples (default 5 ≈ 0.23 ms).</param>
    /// <param name="taps">Adaptive FIR filter length. Longer = sharper frequency
    /// selectivity but slower convergence and higher CPU cost.</param>
    /// <param name="mu">NLMS step size. 0.5 is a robust default.</param>
    public AdaptiveLineEnhancer(int delay = 5, int taps = 64, double mu = 0.5)
    {
        _delay   = delay;
        _taps    = taps;
        _mu      = mu;
        _weights = new double[taps];
        _bufLen  = delay + taps + 1;
        _buf     = new double[_bufLen];
    }

    /// <summary>
    /// L2 norm of the adaptive filter weights. Starts at 0 and grows as the filter
    /// converges toward the periodic carrier. A stable, non-zero value means the ALE
    /// has found a predictable signal; near-zero means it has not yet converged.
    /// </summary>
    public double WeightNorm
    {
        get
        {
            double sum = 0;
            foreach (double w in _weights) sum += w * w;
            return Math.Sqrt(sum);
        }
    }

    public void Reset()
    {
        Array.Clear(_weights, 0, _weights.Length);
        Array.Clear(_buf,     0, _buf.Length);
        _writeIdx = 0;
    }

    public float[] ProcessBlock(float[] input)
    {
        var output = new float[input.Length];
        for (int i = 0; i < input.Length; i++)
        {
            // Write current sample into circular buffer
            _buf[_writeIdx % _bufLen] = input[i];

            // Compute filter output y[n] = w^T · x[n-Δ .. n-Δ-L+1]
            double y     = 0.0;
            double power = 1e-10; // regularisation prevents divide-by-zero
            for (int k = 0; k < _taps; k++)
            {
                double xk = _buf[(_writeIdx - _delay - k + _bufLen * 4) % _bufLen];
                y     += _weights[k] * xk;
                power += xk * xk;
            }

            // Prediction error: how much the filter missed the current sample
            double error = input[i] - y;

            // NLMS weight update: step size normalised by input power
            double stepSize = _mu / power;
            for (int k = 0; k < _taps; k++)
            {
                double xk = _buf[(_writeIdx - _delay - k + _bufLen * 4) % _bufLen];
                _weights[k] += stepSize * error * xk;
            }

            _writeIdx++;
            output[i] = (float)y; // enhanced periodic component
        }
        return output;
    }
}
