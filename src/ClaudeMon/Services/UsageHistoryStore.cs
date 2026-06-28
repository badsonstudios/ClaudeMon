namespace ClaudeMon.Services;

using System.Text.Json;
using ClaudeMon.Models;

/// <summary>
/// A small, persistent, rolling store of usage samples. Each recorded sample is
/// pruned to a bounded window (by age and count) and written to a JSON file under
/// LocalAppData, so the trend survives restarts without growing unbounded.
///
/// Thread-safe: the poll loop records on a background thread while the UI reads
/// recent samples to draw the flyout sparkline.
/// </summary>
public sealed class UsageHistoryStore
{
    private readonly string _path;
    private readonly TimeSpan _maxAge;
    private readonly int _maxCount;
    private readonly object _lock = new();
    private readonly List<UsageSample> _samples = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
    };

    public UsageHistoryStore(string? path = null, TimeSpan? maxAge = null, int maxCount = 500)
    {
        _path = path ?? GetDefaultPath();
        _maxAge = maxAge ?? TimeSpan.FromHours(6);
        _maxCount = maxCount > 0 ? maxCount : 500;
    }

    /// <summary>Loads persisted samples from disk (tolerating a missing/corrupt file).</summary>
    public void Load()
    {
        lock (_lock)
        {
            _samples.Clear();
            try
            {
                if (!File.Exists(_path))
                    return;

                var json = File.ReadAllText(_path);
                var loaded = JsonSerializer.Deserialize<List<UsageSample>>(json, JsonOptions);
                if (loaded is not null)
                {
                    _samples.AddRange(loaded);
                    Prune(DateTimeOffset.UtcNow);
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                // Corrupt or unreadable history is non-critical — start fresh.
                _samples.Clear();
            }
        }
    }

    /// <summary>Appends a sample, prunes to the retention window, and persists.</summary>
    public void Record(UsageSample sample)
    {
        lock (_lock)
        {
            _samples.Add(sample);
            // Prune relative to the new sample's own timestamp (normally UtcNow),
            // so the retention window tracks the latest data point.
            Prune(sample.Timestamp);
            Save();
        }
    }

    /// <summary>A snapshot of all retained samples, oldest first.</summary>
    public IReadOnlyList<UsageSample> Samples
    {
        get { lock (_lock) { return _samples.ToList(); } }
    }

    /// <summary>The retained samples no older than <paramref name="window"/>, oldest first.</summary>
    public IReadOnlyList<UsageSample> Recent(TimeSpan window)
    {
        var cutoff = DateTimeOffset.UtcNow - window;
        lock (_lock)
        {
            return _samples.Where(s => s.Timestamp >= cutoff).ToList();
        }
    }

    // Caller holds _lock. Drops samples older than the window, then trims the
    // oldest down to the count cap so the file can't grow without bound.
    private void Prune(DateTimeOffset now)
    {
        var cutoff = now - _maxAge;
        _samples.RemoveAll(s => s.Timestamp < cutoff);

        if (_samples.Count > _maxCount)
            _samples.RemoveRange(0, _samples.Count - _maxCount);
    }

    // Caller holds _lock. Best-effort persistence — a write failure must not
    // disrupt monitoring. Writes to a temp file then atomically renames, so a
    // crash mid-write can't leave a half-written history.json for the next load.
    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (dir is not null && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_samples, JsonOptions));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or NotSupportedException or JsonException)
        {
            // Ignore — history is a convenience, not critical state.
        }
    }

    private static string GetDefaultPath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ClaudeMon",
            "history.json");
}
