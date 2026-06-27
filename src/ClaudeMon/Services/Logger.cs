namespace ClaudeMon.Services;

using System.Text;

/// <summary>
/// Minimal, thread-safe, size-bounded file logger for diagnostics (poll results,
/// status transitions, API/auth/network errors). Best-effort: logging never throws
/// into the app, and it must <b>never</b> be passed secrets — callers log only
/// non-sensitive messages (no token contents).
/// </summary>
public sealed class Logger
{
    private const long DefaultMaxBytes = 1_000_000; // ~1 MB before rotating.

    private readonly string _path;
    private readonly long _maxBytes;
    private readonly object _lock = new();

    /// <summary>Full path to the current log file.</summary>
    public string FilePath => _path;

    /// <summary>Directory containing the log file (and its rotated backup).</summary>
    public string DirectoryPath => Path.GetDirectoryName(_path) ?? _path;

    public Logger(string? path = null, long maxBytes = DefaultMaxBytes)
    {
        _path = path ?? GetDefaultLogPath();
        _maxBytes = maxBytes > 0 ? maxBytes : DefaultMaxBytes;
    }

    public void Info(string message) => Write("INFO", message);

    public void Warn(string message) => Write("WARN", message);

    public void Error(string message) => Write("ERROR", message);

    private void Write(string level, string message)
    {
        // Single-line entries keep the log greppable; collapse any embedded newlines.
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message.Replace("\r", " ").Replace("\n", " ")}";

        lock (_lock)
        {
            try
            {
                var dir = Path.GetDirectoryName(_path);
                if (dir is not null && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                RotateIfNeeded();
                File.AppendAllText(_path, line + Environment.NewLine, Encoding.UTF8);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Diagnostics must never take down the app — drop the line silently.
            }
        }
    }

    // Caller holds _lock. When the file reaches the cap, move it to "<name>.1"
    // (overwriting any previous backup) so on-disk size stays bounded to ~2x cap.
    private void RotateIfNeeded()
    {
        var info = new FileInfo(_path);
        if (!info.Exists || info.Length < _maxBytes)
            return;

        var backup = _path + ".1";
        try
        {
            if (File.Exists(backup))
                File.Delete(backup);
            File.Move(_path, backup);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // If rotation fails, fall back to truncating so the file can't grow without bound.
            try { File.WriteAllText(_path, string.Empty); }
            catch (Exception inner) when (inner is IOException or UnauthorizedAccessException) { }
        }
    }

    private static string GetDefaultLogPath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ClaudeMon",
            "logs",
            "claudemon.log");
}
