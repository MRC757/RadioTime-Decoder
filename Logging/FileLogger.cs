using System.IO;

namespace WwvDecoder.Logging;

/// <summary>
/// Thread-safe file logger that writes timestamped entries to disk.
/// Log files are stored in %APPDATA%\WwvDecoder\ with one file per day (log-yyyy-MM-dd.txt).
/// </summary>
public sealed class FileLogger : IDisposable
{
    private readonly StreamWriter _writer;
    private readonly object _lock = new();

    public FileLogger()
    {
        // Create %APPDATA%\WwvDecoder\ directory if it doesn't exist
        var appDataDir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var logDir = Path.Combine(appDataDir, "WwvDecoder");
        Directory.CreateDirectory(logDir);

        // One log file per day
        var logPath = Path.Combine(logDir, $"log-{DateTime.Now:yyyy-MM-dd}.txt");

        // Open the log file in append mode with UTF-8 encoding; auto-flush each write
        _writer = new StreamWriter(logPath, append: true, System.Text.Encoding.UTF8)
        {
            AutoFlush = true
        };

        // Write a session-start banner
        WriteLine($"--- Session started {DateTime.Now:yyyy-MM-dd HH:mm:ss} ---");
    }

    /// <summary>
    /// Write a timestamped message to the log file. Thread-safe.
    /// Swallows IO exceptions to prevent logging failures from crashing the app.
    /// </summary>
    public void WriteLine(string message)
    {
        lock (_lock)
        {
            try
            {
                _writer.WriteLine(message);
            }
            catch
            {
                // Silently ignore any IO errors; logging must never crash the application.
            }
        }
    }

    /// <summary>
    /// Close the log file. Safe to call multiple times.
    /// </summary>
    public void Dispose()
    {
        lock (_lock)
        {
            try
            {
                _writer?.Dispose();
            }
            catch
            {
                // Ignore disposal errors
            }
        }
    }
}
