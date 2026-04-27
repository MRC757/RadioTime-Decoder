namespace WwvDecoder.Dsp;

/// <summary>
/// Synchronous (lock-in) detector for the WWV 100 Hz time-code subcarrier.
///
/// Replaces the bandpass filter + rectifier + envelope lowpass chain with a coherent
/// I/Q demodulator. SNR improvement over simple rectification is typically 15–25 dB
/// because this technique integrates energy at exactly 100 Hz while rejecting all
/// other frequencies proportionally to the integration time.
///
/// How it works:
///   1. Generate a local reference pair: cos(2π·100·t) and sin(2π·100·t)
///   2. Multiply the input by each reference → I (in-phase) and Q (quadrature) products
///   3. Lowpass filter I and Q with a 2nd-order Butterworth biquad
///      - After mixing, 100 Hz signal → near-DC (baseband)
///      - All other frequencies shift away from DC and are removed by the lowpass
///      - The narrower the lowpass, the better the SNR, at the cost of slower response
///   4. Envelope = 2 × √(I² + Q²)
///      - The factor of 2 restores amplitude (mixing halves the signal level)
///      - √(I² + Q²) is phase-independent — no need to synchronize to the carrier phase
///
/// Lowpass design — 2nd-order Butterworth biquad (maximally flat):
///   Q = 1/√2 ≈ 0.707.  At fc = 3 Hz (locked): group delay ≈ Q/(2π·fc) ≈ 37 ms,
///   rising only slowly toward Dc so the 170 ms Zero pulse edge is resolved cleanly.
///   At fc = 8 Hz (acquiring): group delay ≈ 14 ms — negligible timing impact.
///   Stopband: −40 dB/decade (12 dB/oct), giving >20 dB more rejection than a
///   single-pole IIR at the same cutoff.
///
///   The biquad output is used directly for the envelope; no separate "first-pole-only"
///   state is needed because the Butterworth group delay is short enough at every
///   operating frequency that One-pulse durations stay well within the 350–650 ms window.
///
/// Noise floor tracking uses the same asymmetric algorithm as EnvelopeDetector:
///   fast fall (tracks true noise level quickly) / very slow rise (pulse amplitude
///   cannot inflate the floor during the 0.8 s marker LOW period).
/// </summary>
public class SynchronousDetector
{
    private readonly int _sampleRate;
    private readonly double _subcarrierHz;
    private double _omega;       // 2π × (subcarrierHz + FrequencyOffsetHz) / sampleRate

    private double _phase;       // current reference oscillator phase (radians, 0..2π)

    // 2nd-order Butterworth biquad lowpass state — direct-form II transposed.
    // One biquad per channel (I and Q).
    private double _iW1, _iW2;  // I-channel delay-line state
    private double _qW1, _qW2;  // Q-channel delay-line state
    private double _iOut, _qOut; // current biquad outputs (exposed to PLL and envelope)

    // Biquad coefficients (pre-normalised by a₀ = 1 + α).
    // Recomputed whenever LowpassHz changes.
    private double _lpB0, _lpB1, _lpB2; // feed-forward
    private double _lpA1, _lpA2;         // feedback

    private double _lowpassHz;

    /// <summary>Frequency offset applied to the local oscillator by the PLL (Hz).</summary>
    public double FrequencyOffsetHz
    {
        get => _freqOffsetHz;
        set
        {
            _freqOffsetHz = value;
            _omega = 2.0 * Math.PI * (_subcarrierHz + value) / _sampleRate;
        }
    }
    private double _freqOffsetHz;

    /// <summary>Lowpass cutoff frequency (Hz). Narrow after PLL locks to improve SNR.</summary>
    public double LowpassHz
    {
        get => _lowpassHz;
        set
        {
            _lowpassHz = value;
            ComputeBiquadCoefficients(value);
        }
    }

    /// <summary>Two-pole lowpass in-phase output (read by CarrierPll for frequency estimation).</summary>
    public double IFiltered => _iOut;

    /// <summary>Two-pole lowpass quadrature output (read by CarrierPll for frequency estimation).</summary>
    public double QFiltered => _qOut;

    // Noise floor tracking (identical asymmetric logic to EnvelopeDetector)
    private double _noiseFloor = 0.001;
    private int _noiseCounter;
    private readonly int _noiseInterval; // samples between noise floor updates (~100 ms)

    public double NoiseFloor => _noiseFloor;

    public SynchronousDetector(int sampleRate, double subcarrierHz = 100.0, double lowpassHz = 8.0)
    {
        _sampleRate   = sampleRate;
        _subcarrierHz = subcarrierHz;
        _omega = 2.0 * Math.PI * subcarrierHz / sampleRate;

        // Compute biquad coefficients via property so _lowpassHz is initialised.
        LowpassHz = lowpassHz;

        _noiseInterval = sampleRate / 10; // update every ~100 ms
    }

    public void Reset()
    {
        _phase  = 0;
        _iW1    = _iW2 = 0;
        _qW1    = _qW2 = 0;
        _iOut   = _qOut = 0;
        _noiseFloor   = 0.001;
        _noiseCounter = 0;
        _freqOffsetHz = 0;
        _omega = 2.0 * Math.PI * _subcarrierHz / _sampleRate;
        ComputeBiquadCoefficients(_lowpassHz);
    }

    public float[] ProcessBlock(float[] input)
    {
        var output = new float[input.Length];

        for (int i = 0; i < input.Length; i++)
        {
            double sample = input[i];

            // Step 1: mix with local reference (coherent demodulation)
            double iMixed = sample * Math.Cos(_phase);
            double qMixed = sample * Math.Sin(_phase);

            // Step 2: advance phase and wrap to [0, 2π) to prevent precision drift
            _phase += _omega;
            if (_phase >= Math.PI * 2.0) _phase -= Math.PI * 2.0;

            // Step 3: 2nd-order Butterworth biquad lowpass — direct-form II transposed.
            // H(z) = (b0 + b1·z⁻¹ + b2·z⁻²) / (1 + a1·z⁻¹ + a2·z⁻²)
            // Maximally flat passband (Q = 1/√2); 12 dB/oct stopband.
            double iNew = _lpB0 * iMixed + _iW1;
            _iW1 = _lpB1 * iMixed - _lpA1 * iNew + _iW2;
            _iW2 = _lpB2 * iMixed - _lpA2 * iNew;
            _iOut = iNew;

            double qNew = _lpB0 * qMixed + _qW1;
            _qW1 = _lpB1 * qMixed - _lpA1 * qNew + _qW2;
            _qW2 = _lpB2 * qMixed - _lpA2 * qNew;
            _qOut = qNew;

            // Step 4: compute envelope from the full biquad output.
            // The Butterworth biquad's group delay is short enough (≈14 ms at 8 Hz,
            // ≈37 ms at 3 Hz) that One-pulse edges remain well within the 350–650 ms
            // matched-filter window even at the narrowest PLL-locked setting.
            double envelope = 2.0 * Math.Sqrt(_iOut * _iOut + _qOut * _qOut);
            output[i] = (float)envelope;

            // Step 5: update noise floor estimate every ~100 ms
            _noiseCounter++;
            if (_noiseCounter >= _noiseInterval)
            {
                _noiseCounter = 0;

                // Asymmetric tracking: fall quickly on quiet, rise very slowly on signal
                // Prevents pulse amplitude from inflating the noise floor baseline.
                if (envelope < _noiseFloor)
                    _noiseFloor = _noiseFloor * 0.90 + envelope * 0.10;  // fast fall
                else
                    _noiseFloor = _noiseFloor * 0.9995 + envelope * 0.0005; // very slow rise

                if (_noiseFloor < 1e-6) _noiseFloor = 1e-6;
            }
        }

        return output;
    }

    /// <summary>
    /// Computes 2nd-order Butterworth biquad lowpass coefficients at the given cutoff.
    /// Uses the bilinear transform with Q = 1/√2 (maximally flat / Butterworth response).
    /// </summary>
    private void ComputeBiquadCoefficients(double cutoffHz)
    {
        double w0    = 2.0 * Math.PI * cutoffHz / _sampleRate;
        // α = sin(ω₀) / (2·Q) = sin(ω₀) / √2  for Butterworth (Q = 1/√2)
        double alpha = Math.Sin(w0) / Math.Sqrt(2.0);
        double cosW0 = Math.Cos(w0);
        double a0    = 1.0 + alpha;

        _lpB0 = (1.0 - cosW0) / 2.0 / a0;
        _lpB1 = (1.0 - cosW0)       / a0;
        _lpB2 = (1.0 - cosW0) / 2.0 / a0;
        _lpA1 = -2.0 * cosW0        / a0;
        _lpA2 = (1.0 - alpha)       / a0;
    }
}
