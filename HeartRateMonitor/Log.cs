using System.Text;

namespace HeartRateMonitor;

/// <summary>
/// Plain-text debug log in %AppData%\HeartRateMonitor\logs, for sending along with bug reports.
/// Does nothing until <see cref="Init"/> is called, so --snapshot runs leave no trace.
/// </summary>
public static class Log
{
    private const long MaxBytes = 2 * 1024 * 1024;

    private static readonly object Gate = new();
    private static StreamWriter? _writer;

    public static string Folder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HeartRateMonitor", "logs");

    public static string FilePath { get; } = Path.Combine(Folder, "HeartRateMonitor.log");

    /// <summary>The log from before the last rollover.</summary>
    public static string PreviousFilePath { get; } = Path.Combine(Folder, "HeartRateMonitor.previous.log");

    public static void Init()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            if (File.Exists(FilePath) && new FileInfo(FilePath).Length > MaxBytes)
                File.Move(FilePath, PreviousFilePath, overwrite: true);

            var stream = new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            lock (Gate)
                _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
        }
        catch (Exception)
        {
            // No log is better than no app.
        }
    }

    public static void Info(string message) => Write("INFO ", message);

    public static void Warn(string message) => Write("WARN ", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message}: {ex}");

    private static void Write(string level, string message)
    {
        lock (Gate)
        {
            if (_writer is null)
                return;
            try
            {
                _writer.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} {message}");
            }
            catch (Exception)
            {
            }
        }
    }

    public static void Close()
    {
        lock (Gate)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }
}
