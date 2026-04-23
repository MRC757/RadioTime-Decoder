using System.Diagnostics;

namespace WwvDecoder.Dsp;

public sealed class SyncQualityScorer
{
    private readonly int _sampleRate;

    private double _carrierScore;
    private double _cadenceScore;
    private long _lastCadenceTick;

    // Accumulate 500 ms of samples before each Goertzel analysis.
    // At 22050 Hz and 50 ms blocks, the per-block DFT resolution is 1/0.05 = 20 Hz,
    // meaning every bin from 95–105 Hz falls inside the same spectral main lobe and
    // all bins receive nearly equal power — dominance and prominence are near zero
    // even with a strong carrier.  With a 500 ms window, resolution = 1/0.5 = 2 Hz,
    // so 100 Hz is cleanly resolved from its neighbours.
    private readonly float[] _accumBuffer;
    private int _accumCount;

    public double BestFrequencyHz { get; private set; } = 100.0;

    public double SyncScorePercent =>
        Math.Clamp((0.65 * _carrierScore + 0.35 * _cadenceScore) * 100.0, 0.0, 100.0);

    public SyncQualityScorer(int sampleRate)
    {
        _sampleRate  = sampleRate;
        _accumBuffer = new float[(int)(sampleRate * 0.5)]; // 500 ms
    }

    public void Reset()
    {
        _carrierScore = 0;
        _cadenceScore = 0;
        _lastCadenceTick = 0;
        _accumCount = 0;
        BestFrequencyHz = 100.0;
    }

    public void ProcessBlock(float[] input)
    {
        // Fill the accumulator; run Goertzel analysis each time it fills.
        int src = 0;
        while (src < input.Length)
        {
            int space = _accumBuffer.Length - _accumCount;
            int copy  = Math.Min(space, input.Length - src);
            Array.Copy(input, src, _accumBuffer, _accumCount, copy);
            _accumCount += copy;
            src         += copy;

            if (_accumCount >= _accumBuffer.Length)
            {
                RunGoertzelAnalysis(_accumBuffer);
                _accumCount = 0;
            }
        }

        // Cadence decay runs every block (every 50 ms) regardless of the Goertzel cadence.
        if (_lastCadenceTick != 0)
        {
            double secondsSinceCadence = (double)(Stopwatch.GetTimestamp() - _lastCadenceTick) / Stopwatch.Frequency;
            if (secondsSinceCadence > 2.5)
                _cadenceScore *= 0.995;
        }
    }

    public void ObservePulse(long timestamp)
    {
        if (_lastCadenceTick != 0)
        {
            double dt = (double)(timestamp - _lastCadenceTick) / Stopwatch.Frequency;
            if (dt >= 0.5 && dt <= 1.5)
            {
                double error = Math.Abs(dt - 1.0);
                double sampleScore = Math.Clamp(1.0 - error / 0.20, 0.0, 1.0);
                _cadenceScore += 0.20 * (sampleScore - _cadenceScore);
            }
            else if (dt > 1.5 && dt < 10.0)
            {
                _cadenceScore *= 0.90;
            }
        }

        _lastCadenceTick = timestamp;
    }

    public void ObserveSecondTick(long timestamp) => ObservePulse(timestamp);

    private void RunGoertzelAnalysis(float[] samples)
    {
        const int minHz = 95;
        const int maxHz = 105;

        double bestPower   = 0;
        double secondPower = 0;
        double sumPower    = 0;
        int    bestHz      = 100;

        for (int hz = minHz; hz <= maxHz; hz++)
        {
            double power = GoertzelPower(samples, hz);
            sumPower += power;

            if (power > bestPower)
            {
                secondPower = bestPower;
                bestPower   = power;
                bestHz      = hz;
            }
            else if (power > secondPower)
            {
                secondPower = power;
            }
        }

        double meanOther = (sumPower - bestPower) / Math.Max(1, maxHz - minHz);
        double dominance = bestPower > 1e-12 ? (bestPower - secondPower) / bestPower : 0.0;
        double prominence = bestPower / (meanOther + 1e-12);

        // Threshold recalibrated for HF signals: prominence of 3× average neighbour
        // → score 1.0 (previously required 5×, which was unreachable under HF noise
        // and the short 50 ms window).  With the 500 ms window the 100 Hz carrier is
        // now spectrally resolved, so prominence of 3–10× is realistic.
        double prominenceScore   = Math.Clamp((prominence - 1.0) / 2.0, 0.0, 1.0);
        double blockCarrierScore = Math.Clamp(0.55 * dominance + 0.45 * prominenceScore, 0.0, 1.0);

        _carrierScore   += 0.15 * (blockCarrierScore - _carrierScore);
        BestFrequencyHz += 0.15 * (bestHz - BestFrequencyHz);
    }

    private double GoertzelPower(float[] input, double targetHz)
    {
        double omega = 2.0 * Math.PI * targetHz / _sampleRate;
        double coeff = 2.0 * Math.Cos(omega);
        double q0 = 0.0, q1 = 0.0, q2 = 0.0;

        for (int i = 0; i < input.Length; i++)
        {
            q0 = coeff * q1 - q2 + input[i];
            q2 = q1;
            q1 = q0;
        }

        return q1 * q1 + q2 * q2 - coeff * q1 * q2;
    }
}
