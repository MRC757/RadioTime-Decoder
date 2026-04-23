using System.Runtime.InteropServices;

namespace WwvDecoder.Clock;

/// <summary>
/// Sets the Windows system clock via SetSystemTime().
/// Requires the process to be running as Administrator (enforced by app.manifest).
/// </summary>
public class SystemTimeSetter
{
    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEMTIME
    {
        public ushort wYear, wMonth, wDayOfWeek, wDay;
        public ushort wHour, wMinute, wSecond, wMilliseconds;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetSystemTime(ref SYSTEMTIME st);

    /// <summary>
    /// Sets the system clock to the supplied UTC time.
    /// Returns the delta (new time − old time) so the caller can log it.
    /// </summary>
    public TimeSpan SetTime(DateTime utcTime)
    {
        var before = DateTime.UtcNow;

        var st = new SYSTEMTIME
        {
            wYear         = (ushort)utcTime.Year,
            wMonth        = (ushort)utcTime.Month,
            wDay          = (ushort)utcTime.Day,
            wHour         = (ushort)utcTime.Hour,
            wMinute       = (ushort)utcTime.Minute,
            wSecond       = (ushort)utcTime.Second,
            wMilliseconds = 0
        };

        if (!SetSystemTime(ref st))
            throw new InvalidOperationException(
                $"SetSystemTime failed. Error code: {Marshal.GetLastWin32Error()}");

        return utcTime - before;
    }

    /// <summary>
    /// Aligns the system clock so its seconds field reads :00.000, without changing
    /// the date, hour, or minute. Intended for HF digital-mode software (WSPR, FT8, etc.)
    /// that requires the minute boundary to be correct but does not need full time decoding.
    ///
    /// The minute pulse fires at the END of the ~800 ms burst. We subtract the measured
    /// pulse width to estimate when the true minute boundary actually occurred, then set
    /// the clock to HH:MM:00.000 of that estimated moment.
    ///
    /// Returns the correction applied (positive = clock was running late).
    /// </summary>
    public TimeSpan SyncMinuteStart(double pulseWidthSeconds)
    {
        var before    = DateTime.UtcNow;

        // Estimate true minute-start: now minus the pulse duration we just measured.
        // The minute pulse starts at HH:MM:00.000 and lasts ~800 ms; we detect it at the end.
        var trueStart = before - TimeSpan.FromSeconds(pulseWidthSeconds);

        // Guard: if the subtraction crossed a minute boundary (seconds ≥ 30), the pulse-
        // width subtraction took us into the previous minute. Advance by one minute so we
        // align to the minute that was actually just announced.
        // This handles PC clocks that are up to ~29 seconds slow.
        if (trueStart.Second >= 30)
            trueStart = trueStart.AddMinutes(1);

        // Keep current date/hour/minute; zero seconds and milliseconds.
        var aligned = new DateTime(trueStart.Year, trueStart.Month, trueStart.Day,
                                   trueStart.Hour, trueStart.Minute, 0, 0,
                                   DateTimeKind.Utc);

        var st = new SYSTEMTIME
        {
            wYear         = (ushort)aligned.Year,
            wMonth        = (ushort)aligned.Month,
            wDay          = (ushort)aligned.Day,
            wHour         = (ushort)aligned.Hour,
            wMinute       = (ushort)aligned.Minute,
            wSecond       = 0,
            wMilliseconds = 0
        };

        if (!SetSystemTime(ref st))
            throw new InvalidOperationException(
                $"SetSystemTime failed. Error code: {Marshal.GetLastWin32Error()}");

        return aligned - before;
    }
}
