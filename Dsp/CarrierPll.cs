namespace WwvDecoder.Dsp;

/// <summary>
/// Costas-loop Phase-Locked Loop that tracks the actual 100 Hz carrier frequency.
///
/// Problem:
///   SDR receivers have local oscillator error of 0.5–5 Hz. With the SynchronousDetector's
///   8 Hz lowpass, up to ±4 Hz of offset is tolerated before envelope attenuation occurs.
///   Beyond that, the envelope ripples and the pulse boundaries become indistinct.
///
/// Solution — two-stage benefit:
///   1. Frequency tracking: cross-product discriminator measures the angular rotation
///      rate of the IQ vector between audio blocks and drives it to zero.
///   2. Narrowed lowpass after lock: once the PLL has nulled the frequency error,
///      the SynchronousDetector's lowpass can safely be narrowed from 8 Hz to 2 Hz.
///      Narrower bandwidth → less noise in the envelope → 12 dB SNR improvement.
///
/// Frequency discriminator (cross-product method):
///   Let (I₀, Q₀) and (I₁, Q₁) be the filtered IQ at the end of consecutive blocks.
///   cross = I₀·Q₁ − Q₀·I₁  ≈ A²·sin(Δφ) ≈ A²·Δφ for small Δφ
///   dot   = I₀·I₁ + Q₀·Q₁  ≈ A²·cos(Δφ) ≈ A²
///   Δφ = atan2(cross, dot) → frequency error = Δφ / (2π · block_duration)
///
///   The division by amplitude is implicit in the atan2 — it is phase-invariant.
///   An amplitude check gates the update during pulse LOW periods (I≈Q≈0 → unreliable).
///
/// PI loop filter:
///   proportional term: tracks fast transients
///   integral term    : eliminates steady-state frequency offset
///
/// Lock detection:
///   "Locked" when |freq_error| &lt; 0.5 Hz for 5 consecutive block updates.
///   On lock, tells SynchronousDetector to switch to 2 Hz lowpass for best SNR.
///   On loss of lock, immediately reverts to 8 Hz lowpass (acquisition bandwidth).
/// </summary>
public class CarrierPll
{
    private readonly SynchronousDetector _detector;
    private readonly int _sampleRate;
    private readonly double _kp;        // proportional gain (Hz per radian)
    private readonly double _ki;        // integral gain (Hz per radian·second)
    private readonly double _wideLpHz;
    private readonly double _narrowLpHz;
    private readonly double _mediumLpHz;  // re-acquisition bandwidth after a fade (between wide and narrow)
    private readonly Action<string>? _onLog;

    private double _integrator;
    private double _prevI, _prevQ;
    private bool _hasPrev;
    private int _lockedCount;
    private int _lostCount;
    private bool _isLocked;
    // Once the PLL has locked at least once we know the approximate carrier frequency.
    // On subsequent lock losses (e.g. ionospheric fades) use medium bandwidth for re-
    // acquisition: lower noise than wide LP, yet still fast enough to re-converge on the
    // same frequency that was correct before the fade.  Wide LP is reserved for cold start.
    private bool _hasEverLocked;

    private const double LockThresholdHz = 0.5;   // freq error below this → locked
    private const int    LockHoldBlocks  = 5;      // blocks at lock threshold before declaring lock
    // Hysteresis: require this many consecutive above-threshold blocks before losing lock.
    // Without hysteresis the PLL re-locks during each HIGH period then immediately loses lock
    // at the next pulse transition, leaving the SynchronousDetector LP at 8 Hz almost
    // permanently. 15 blocks × ~50 ms = ~750 ms hold — spans the longest LOW period (0.8 s
    // Marker) so a single bad measurement during a pulse transition cannot unlock the PLL.
    private const int    LostHoldBlocks  = 15;
    private const double MaxOffsetHz     = 10.0;   // clamp correction to ±10 Hz

    public bool IsLocked => _isLocked;

    /// <summary>Current frequency offset being applied to the SynchronousDetector (Hz).</summary>
    public double FrequencyErrorHz => _detector.FrequencyOffsetHz;

    public CarrierPll(SynchronousDetector detector, int sampleRate,
                      double kp = 0.5, double ki = 0.05,
                      double wideLpHz = 8.0, double narrowLpHz = 2.0, double mediumLpHz = 4.0,
                      Action<string>? onLog = null)
    {
        _detector   = detector;
        _sampleRate = sampleRate;
        _kp         = kp;
        _ki         = ki;
        _wideLpHz   = wideLpHz;
        _narrowLpHz = narrowLpHz;
        _mediumLpHz = mediumLpHz;
        _onLog      = onLog;
    }

    public void Reset()
    {
        _integrator  = 0;
        _hasPrev     = false;
        _lockedCount = 0;
        _lostCount   = 0;
        _hasEverLocked = false;
        if (_isLocked)
        {
            _isLocked = false;
            _detector.LowpassHz        = _wideLpHz;
            _detector.FrequencyOffsetHz = 0;
            _onLog?.Invoke($"[PLL] Reset — lock cleared, LP widened to {_wideLpHz} Hz");
        }
    }

    /// <summary>
    /// Call once per audio block, AFTER SynchronousDetector.ProcessBlock() has run.
    /// Reads the current IQ vector, computes frequency error, and adjusts the
    /// SynchronousDetector's reference oscillator accordingly.
    /// </summary>
    /// <param name="blockSamples">Number of samples in the block just processed.</param>
    public void Update(int blockSamples)
    {
        double i1 = _detector.IFiltered;
        double q1 = _detector.QFiltered;

        // Gate update when amplitude is too small (pulse LOW period or no signal)
        double amplitude = Math.Sqrt(i1 * i1 + q1 * q1);
        if (amplitude < _detector.NoiseFloor * 2.0)
        {
            // Keep the previous IQ so we don't get a spurious phase jump after
            // the LOW period ends and amplitude recovers.
            _hasPrev = false;
            // Slowly decay the integrator during blackouts so a stale frequency
            // correction from before a long HF fade doesn't persist indefinitely.
            // τ = 120 s → retains ~78 % through a 30 s fade (negligible normal effect).
            _integrator *= Math.Exp(-(double)blockSamples / (120.0 * _sampleRate));
            return;
        }

        if (!_hasPrev)
        {
            _prevI   = i1;
            _prevQ   = q1;
            _hasPrev = true;
            return;
        }

        // Cross-product frequency discriminator (rotation-rate of the IQ vector)
        double cross = _prevI * q1 - _prevQ * i1;
        double dot   = _prevI * i1 + _prevQ * q1;
        double dPhase = Math.Atan2(cross, dot); // radians turned since last block

        _prevI = i1;
        _prevQ = q1;

        // NOTE: sign convention — IQ phase decreases when carrier is above reference.
        // See SynchronousDetector comment: Q_filtered ≈ -A/2 × sin(Δω·t)
        // so atan2(cross,dot) = -Δφ.  Negate to get the true frequency error direction.
        double blockDur = (double)blockSamples / _sampleRate;
        double freqError = -dPhase / (2.0 * Math.PI * blockDur); // Hz

        // PI loop filter
        _integrator += _ki * freqError * blockDur;
        double correction = _kp * freqError + _integrator;
        _detector.FrequencyOffsetHz = Math.Clamp(correction, -MaxOffsetHz, MaxOffsetHz);

        // Lock detection with hysteresis.
        // Acquire: LockHoldBlocks consecutive measurements below threshold → lock.
        // Release: LostHoldBlocks consecutive measurements above threshold → unlock.
        // Hysteresis prevents the PLL from toggling on every pulse LOW period:
        // the gated update already skips most LOW-period measurements, but residual
        // phase noise just after LOW→HIGH transitions still spikes the error briefly.
        if (Math.Abs(freqError) < LockThresholdHz)
        {
            _lockedCount++;
            _lostCount = 0;
            if (_lockedCount >= LockHoldBlocks && !_isLocked)
            {
                _isLocked      = true;
                _hasEverLocked = true;
                _detector.LowpassHz = _narrowLpHz;  // narrow bandwidth for best SNR
                _onLog?.Invoke($"[PLL] Locked — offset={_detector.FrequencyOffsetHz:+0.0;-0.0;+0.0} Hz  LP→{_narrowLpHz} Hz");
            }
        }
        else
        {
            _lockedCount = 0;
            _lostCount++;
            if (_isLocked && _lostCount >= LostHoldBlocks)
            {
                _isLocked  = false;
                _lostCount = 0;
                // After a confirmed prior lock the carrier frequency is approximately
                // known — use medium bandwidth (4 Hz) for re-acquisition so noise is
                // lower than with the full 8 Hz acquisition sweep.  Wide LP is only
                // used on a cold start (before the first lock).
                double recacqLp = _hasEverLocked ? _mediumLpHz : _wideLpHz;
                _detector.LowpassHz = recacqLp;
                _onLog?.Invoke($"[PLL] Lost lock (error={freqError:+0.0;-0.0;+0.0} Hz, {LostHoldBlocks} consec)  LP→{recacqLp} Hz");
            }
        }
    }
}
