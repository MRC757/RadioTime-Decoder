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
}
