namespace WwvDecoder.Dsp;

/// <summary>
/// Detects the amplitude envelope of the filtered 100 Hz signal.
/// The envelope extraction process:
///   1. Rectification: take absolute value of the 100 Hz signal
///   2. Lowpass filtering: smooth the result with a single-pole IIR filter
///   3. Noise floor tracking: estimate background noise level to enable adaptive thresholding
///
/// The WWV time-code pulses are 0.2 s (bit 0), 0.5 s (bit 1), or 0.8 s (marker) in duration.
/// An 8 Hz lowpass cutoff allows the envelope to rise/fall in ~125 ms, fast enough to track
/// the ~200 ms pulse edges while smoothing out the 100 Hz ripple.
/// </summary>
public class EnvelopeDetector
{
    private readonly double _alpha;   // Single-pole lowpass filter coefficient (0 to 1)
    private double _envelope;         // Current smoothed envelope estimate
    private double _noiseFloor;       // Estimated background noise level (updated every 100 ms)
    private int _noiseUpdateCounter;
    private const int NoiseUpdateInterval = 2205; // Update noise floor every 2205 samples = ~100 ms at 22050 Hz

    /// <param name="sampleRate">Audio sample rate in Hz (e.g., 22050)</param>
    /// <param name="cutoffHz">Envelope lowpass cutoff frequency in Hz.
    /// Default 8 Hz gives a time constant of ~125 ms (one-pole 10%-to-90% rise time),
    /// which is fast enough to resolve 200 ms pulse edges but slow enough to smooth the 100 Hz carrier.</param>
    public EnvelopeDetector(int sampleRate, double cutoffHz = 8.0)
    {
        // Calculate the single-pole RC time constant and sample period
        double rc = 1.0 / (2.0 * Math.PI * cutoffHz);
        double dt = 1.0 / sampleRate;

        // Compute the smoothing coefficient alpha (between 0 and 1)
        // Larger alpha → faster response, smaller alpha → more smoothing
        _alpha = dt / (rc + dt);
    }

    public void Reset()
    {
        _envelope = 0;
        _noiseFloor = 0.001;
        _noiseUpdateCounter = 0;
    }

    public double NoiseFloor => _noiseFloor;

    /// <summary>
    /// Process one sample: rectify, smooth, and update noise floor estimate.
    /// </summary>
    public double Process(double x)
    {
        // Rectify: take absolute value of the 100 Hz signal
        double rectified = Math.Abs(x);

        // Single-pole IIR lowpass: exponential moving average
        // envelope(n) = envelope(n-1) + alpha * (input(n) - envelope(n-1))
        _envelope += _alpha * (rectified - _envelope);

        // Periodically (every ~100 ms) update the noise floor estimate.
        // Noise floor rises when the signal is quiet, falls when active pulses appear.
        // This adaptive baseline enables robust pulse detection across varying SNR.
        _noiseUpdateCounter++;
        if (_noiseUpdateCounter >= NoiseUpdateInterval)
        {
            // Asymmetric noise floor tracking:
            //   Fall quickly when envelope is below floor (tracking true between-pulse noise).
            //   Rise very slowly when envelope is above floor (pulse amplitude must not inflate
            //   the floor — a 0.8 s marker pulse at 8 updates × 5% would push the floor to
            //   34% of signal, making threshold = 3× floor exceed the signal entirely).
            if (_envelope < _noiseFloor)
                _noiseFloor = _noiseFloor * 0.90 + _envelope * 0.10; // fast fall
            else
                _noiseFloor = _noiseFloor * 0.9995 + _envelope * 0.0005; // very slow rise

            // Enforce minimum noise floor to avoid division by zero in pulse detector
            if (_noiseFloor < 1e-6) _noiseFloor = 1e-6;

            _noiseUpdateCounter = 0;
        }

        return _envelope;
    }

    public float[] ProcessBlock(float[] input)
    {
        var output = new float[input.Length];
        for (int i = 0; i < input.Length; i++)
            output[i] = (float)Process(input[i]);
        return output;
    }
}
