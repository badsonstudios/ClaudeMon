namespace ClaudeMon.Services;

using System.Text;

/// <summary>
/// Minimal, thread-safe file logger for diagnostics (poll results, status
/// transitions, API/auth/network errors). Writes one file per local day
/// (claudemon-YYYY-MM-DD.log) and deletes files older than the retention
/// window on startup and at date rollover; a per-file size cap remains as a
/// backstop against runaway loops. Best-effort: logging never throws into the
/// app, and it must <b>never</b> be passed secrets — callers log only
/// non-sensitive messages (no token contents).
/// </summary>
public sealed class Logger
{
    private const long DefaultMaxBytes = 1_000_000; // ~1 MB per-file backstop before rotating.
    private const int RetentionDays = 7;

    private readonly string _directory;
    private readonly long _maxBytes;
    private readonly Func<DateTime> _clock;
    private readonly object _lock = new();

    // Date of the last write; null until the first write so startup runs cleanup.
    private DateTime? _currentDate;

    /// <summary>Full path to the current day's log file (re-resolved per call).</summary>
    public string FilePath => PathForDate(_clock().Date);

    /// <summary>Directory containing the log files.</summary>
    public string DirectoryPath => _directory;

    /// <summary>
    /// Today's file if it exists, otherwise the newest log file on disk, otherwise
    /// null. Lets "View logs" show the most recent diagnostics even when nothing
    /// has been logged yet today (steady state logs are deduplicated, so a quiet
    /// day may write nothing for hours).
    /// </summary>
    public string? LatestExistingFilePath
    {
        get
        {
            try
            {
                var today = FilePath;
                if (File.Exists(today))
                    return today;

                return Directory.Exists(_directory)
                    ? Directory.EnumerateFiles(_directory, "claudemon*.log")
                        .Where(f => f.EndsWith(".log", StringComparison.OrdinalIgnoreCase))
                        .MaxBy(File.GetLastWriteTime)
                    : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }
    }

    public Logger(string? directory = null, long maxBytes = DefaultMaxBytes)
        : this(directory, maxBytes, static () => DateTime.Now)
    {
    }

    internal Logger(string? directory, long maxBytes, Func<DateTime> clock)
    {
        _directory = directory ?? GetDefaultLogDirectory();
        _maxBytes = maxBytes > 0 ? maxBytes : DefaultMaxBytes;
        _clock = clock;
    }

    public void Info(string message) => Write("INFO", message);

    public void Warn(string message) => Write("WARN", message);

    public void Error(string message) => Write("ERROR", message);

    private void Write(string level, string message)
    {
        var now = _clock();
        // Single-line entries keep the log greppable; collapse any embedded newlines.
        var line = $"{now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message.Replace("\r", " ").Replace("\n", " ")}";

        lock (_lock)
        {
            try
            {
                if (!Directory.Exists(_directory))
                    Directory.CreateDirectory(_directory);

                if (_currentDate != now.Date)
                {
                    CleanupOldLogs(now);
                    _currentDate = now.Date;
                }

                var path = PathForDate(now.Date);
                RotateIfNeeded(path);
                File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Diagnostics must never take down the app — drop the line silently.
            }
        }
    }

    // Caller holds _lock. When the file reaches the cap, move it to "<name>.1"
    // (overwriting any previous backup) so a single day stays bounded to ~2x cap.
    private void RotateIfNeeded(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length < _maxBytes)
            return;

        var backup = path + ".1";
        try
        {
            if (File.Exists(backup))
                File.Delete(backup);
            File.Move(path, backup);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // If rotation fails, fall back to truncating so the file can't grow without bound.
            try { File.WriteAllText(path, string.Empty); }
            catch (Exception inner) when (inner is IOException or UnauthorizedAccessException) { }
        }
    }

    // Caller holds _lock. Delete log files whose last write is older than the
    // retention window. Matching on "claudemon*.log*" covers daily files, their
    // ".1" rotation backups, and the legacy claudemon.log / claudemon.log.1 from
    // pre-daily versions, so old installs converge without a migration step.
    private void CleanupOldLogs(DateTime now)
    {
        var cutoff = now.AddDays(-RetentionDays);
        try
        {
            foreach (var file in Directory.EnumerateFiles(_directory, "claudemon*.log*"))
            {
                try
                {
                    if (File.GetLastWriteTime(file) < cutoff)
                        File.Delete(file);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Skip files we can't inspect or delete; try the rest.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Cleanup is best-effort — never let it break logging.
        }
    }

    private string PathForDate(DateTime date) =>
        Path.Combine(_directory, $"claudemon-{date:yyyy-MM-dd}.log");

    private static string GetDefaultLogDirectory() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ClaudeMon",
            "logs");
}
