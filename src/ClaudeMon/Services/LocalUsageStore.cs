namespace ClaudeMon.Services;

using System.Globalization;
using System.Text;
using System.Text.Json;
using ClaudeMon.Models;

/// <summary>
/// Incrementally scans Claude Code's local transcripts
/// (~/.claude/projects/**/*.jsonl) and keeps small per-day usage/cost
/// aggregates for the flyout's "Today" line. Each poll tails known files from
/// their persisted byte offset and picks up new files; unchanged files are
/// skipped without opening, so steady-state scans cost a directory walk plus
/// whatever bytes were appended since the last tick.
///
/// Memory is strictly bounded: per-day totals for the retention window, a
/// ~48-hour dedupe-key map, one offset record per file, and an hour of
/// burn-rate samples. Raw entries are never retained, and nothing beyond the
/// usage numbers, model id, ids, and timestamp is materialized from a line.
///
/// Thread-safe: scans run on a timer thread while the UI thread takes
/// snapshots. When the transcript directory doesn't exist the store degrades
/// silently — <see cref="Snapshot"/> returns null and the UI omits the line.
/// </summary>
public sealed class LocalUsageStore
{
    /// <summary>How far back aggregates are kept (and old files fast-forwarded past).</summary>
    internal static readonly TimeSpan AggregateRetention = TimeSpan.FromDays(7);

    // Dedupe keys are only needed while their entries might be re-read (a
    // truncated file, a restart) — keeping ~2 days of ids protects everything
    // that can affect the displayed "today" while bounding the map.
    internal static readonly TimeSpan DedupeKeyRetention = TimeSpan.FromHours(48);

    // Matches TrayApplication.BurnRateWindow: the recent window the $/hr figure
    // is computed over. Samples are retained for twice that so a paused scan
    // doesn't silently narrow the window.
    internal static readonly TimeSpan BurnRateWindow = TimeSpan.FromMinutes(30);
    internal static readonly TimeSpan RecentCostRetention = TimeSpan.FromMinutes(60);

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private readonly string _projectsDir;
    private readonly string _cachePath;
    private readonly PricingTable _pricing;
    private readonly Logger? _logger;
    private readonly Func<DateTimeOffset> _clock;
    private readonly object _lock = new();
    private bool _scanning;
    private bool _available;

    private readonly Dictionary<string, FileScanState> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, LocalDayTotals> _days = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _recentKeys = new(StringComparer.Ordinal);
    private readonly List<RecentCostSample> _recentCosts = new();
    private readonly HashSet<string> _loggedUnknownModels = new(StringComparer.OrdinalIgnoreCase);

    public LocalUsageStore(
        string? projectsDir = null,
        string? cachePath = null,
        PricingTable? pricing = null,
        Logger? logger = null,
        Func<DateTimeOffset>? clock = null)
    {
        _projectsDir = projectsDir ?? GetDefaultProjectsDir();
        _cachePath = cachePath ?? GetDefaultCachePath();
        _pricing = pricing ?? new PricingTable(new Dictionary<string, ModelPricing>());
        _logger = logger;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _available = Directory.Exists(_projectsDir);
    }

    /// <summary>Whether the transcript directory existed last time anyone looked.</summary>
    public bool IsAvailable { get { lock (_lock) { return _available; } } }

    /// <summary>Loads the persisted cache (tolerating a missing/corrupt file).</summary>
    public void Load()
    {
        lock (_lock)
        {
            try
            {
                if (!File.Exists(_cachePath))
                    return;

                var cache = JsonSerializer.Deserialize<LocalUsageCacheFile>(
                    File.ReadAllText(_cachePath), JsonOptions);
                if (cache is null)
                    return;

                foreach (var (path, state) in cache.Files) _files[path] = state;
                foreach (var (day, totals) in cache.Days) _days[day] = totals;
                foreach (var (key, ts) in cache.RecentDedupeKeys) _recentKeys[key] = ts;
                _recentCosts.AddRange(cache.RecentCosts);
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                // Corrupt or unreadable cache is non-critical — start fresh and
                // rebuild the retention window from the transcripts themselves.
                _files.Clear();
                _days.Clear();
                _recentKeys.Clear();
                _recentCosts.Clear();
            }
        }
    }

    /// <summary>
    /// One incremental scan pass. Safe to call from a timer thread; overlapping
    /// calls no-op. All IO failures are per-file and non-fatal — a locked or
    /// vanished file is simply retried on the next tick.
    /// </summary>
    public void ScanOnce()
    {
        lock (_lock)
        {
            if (_scanning) return;
            _scanning = true;
        }

        try
        {
            ScanCore();
        }
        finally
        {
            lock (_lock) { _scanning = false; }
        }
    }

    /// <summary>
    /// Today's totals and burn rate for the UI, or null when the feature is
    /// absent (no transcript directory) or there is no usage today.
    /// </summary>
    public LocalUsageSnapshot? Snapshot()
    {
        lock (_lock)
        {
            if (!_available)
                return null;

            var now = _clock();
            if (!_days.TryGetValue(DayKey(now), out var day) || day.TotalTokens == 0)
                return null;

            // Deliberately divides by the full window even when activity only
            // covers part of it, so a session's first minutes read low and ramp
            // up as the window fills — smoother than extrapolating one burst,
            // and consistent with how the API-side burn rate behaves.
            double? burnRate = null;
            var cutoff = now - BurnRateWindow;
            var sum = 0.0;
            var any = false;
            foreach (var sample in _recentCosts)
            {
                if (sample.Timestamp >= cutoff)
                {
                    sum += sample.CostUsd;
                    any = true;
                }
            }
            if (any)
                burnRate = sum / BurnRateWindow.TotalHours;

            return new LocalUsageSnapshot(
                DateOnly.FromDateTime(now.ToLocalTime().DateTime),
                day.CostUsd,
                day.HasUnpricedModels,
                day.TotalTokens,
                day.CacheWriteTokens,
                day.CacheReadTokens,
                burnRate);
        }
    }

    private void ScanCore()
    {
        var now = _clock();

        var available = Directory.Exists(_projectsDir);
        lock (_lock) { _available = available; }
        if (!available)
            return;

        List<string> files;
        try
        {
            // IgnoreInaccessible: one unreadable subdirectory must not abort
            // the whole tick for every other project's transcripts.
            files = Directory
                .EnumerateFiles(_projectsDir, "*.jsonl", new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                })
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            return;
        }

        var changed = false;
        foreach (var path in files)
        {
            try
            {
                changed |= ScanFile(path, now);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Locked or deleted mid-scan — skip this tick, retry on the next.
            }
        }

        lock (_lock)
        {
            // Forget offsets for files that no longer exist so the map can't
            // grow without bound across deleted sessions.
            var seen = new HashSet<string>(files, StringComparer.OrdinalIgnoreCase);
            var gone = _files.Keys.Where(p => !seen.Contains(p)).ToList();
            foreach (var path in gone)
            {
                _files.Remove(path);
                changed = true;
            }

            changed |= PruneLocked(now);

            if (changed)
                Save();
        }
    }

    // Reads whatever the file gained since the last scan (parsing outside the
    // lock, state mutation under it). Returns true when any state changed.
    private bool ScanFile(string path, DateTimeOffset now)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
            return false;

        var length = info.Length;
        var mtime = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);

        FileScanState? state;
        lock (_lock) { _files.TryGetValue(path, out state); }

        long start;
        if (state is null)
        {
            // Newly discovered. Anything untouched for the whole retention
            // window can't affect the aggregates — fast-forward without reading,
            // which is what keeps the very first scan of a large old history cheap.
            if (mtime < now - AggregateRetention)
            {
                lock (_lock) { _files[path] = new FileScanState(length, mtime); }
                return true;
            }
            start = 0;
        }
        else if (length < state.Offset)
        {
            // Truncated or replaced — re-read; the dedupe keys absorb any
            // still-present entries so "today" can't double count.
            start = 0;
        }
        else if (length == state.Offset)
        {
            if (state.LastWriteUtc != mtime)
            {
                lock (_lock) { _files[path] = state with { LastWriteUtc = mtime }; }
                return true;
            }
            return false; // Unchanged — skipped without opening.
        }
        else
        {
            start = state.Offset;
        }

        var entries = new List<LocalUsageEntry>();
        long consumed;
        using (var fs = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            consumed = ReadCompleteLines(fs, start, line =>
            {
                var entry = JsonlUsageParser.ParseLine(line);
                if (entry is not null)
                    entries.Add(entry);
            });
        }

        lock (_lock)
        {
            foreach (var entry in entries)
                Ingest(entry, now);

            // Only report a change when one happened. A file whose tail is a
            // permanently unterminated line is re-tailed every tick (cheap),
            // but must not force a cache rewrite every tick. Ingested entries
            // imply consumed advanced, so this covers them too.
            var changed = state is null || consumed != state.Offset || mtime != state.LastWriteUtc;
            if (changed)
                _files[path] = new FileScanState(consumed, mtime);
            return changed;
        }
    }

    /// <summary>
    /// Feeds each complete line from <paramref name="start"/> onward to
    /// <paramref name="onLine"/> and returns the byte offset just past the last
    /// '\n'. A partial trailing line (the writer mid-append) is deliberately
    /// left unconsumed so the next scan parses it whole instead of half-written.
    /// Offsets are byte positions, so reads chunk over the raw stream rather
    /// than going through a StreamReader (whose counts are chars, not bytes).
    /// </summary>
    internal static long ReadCompleteLines(Stream stream, long start, Action<string> onLine)
    {
        stream.Seek(start, SeekOrigin.Begin);

        var buffer = new byte[64 * 1024];
        using var pending = new MemoryStream();
        var consumed = start;
        var bufferBase = start;

        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            var lineStart = 0;
            for (var i = 0; i < read; i++)
            {
                if (buffer[i] != (byte)'\n')
                    continue;

                pending.Write(buffer, lineStart, i - lineStart);
                var line = Encoding.UTF8
                    .GetString(pending.GetBuffer(), 0, (int)pending.Length)
                    .TrimEnd('\r');
                pending.SetLength(0);
                lineStart = i + 1;
                consumed = bufferBase + i + 1;

                if (line.Length > 0)
                    onLine(line);
            }

            pending.Write(buffer, lineStart, read - lineStart);
            bufferBase += read;
        }

        return consumed;
    }

    // Caller holds _lock. Dedupes, prices, and folds one entry into the
    // aggregates. Entries outside the retention window are ignored entirely.
    private void Ingest(LocalUsageEntry entry, DateTimeOffset now)
    {
        if (entry.Timestamp < now - AggregateRetention)
            return;

        if (entry.DedupeKey is not null && !_recentKeys.TryAdd(entry.DedupeKey, entry.Timestamp))
            return;

        var pricing = _pricing.Resolve(entry.Model);
        var cost = pricing?.CostUsd(entry) ?? 0.0;
        if (pricing is null && _loggedUnknownModels.Add(entry.Model))
            _logger?.Info($"Local usage: no pricing for model '{entry.Model}' — tokens counted, cost shown as unavailable.");

        var dayKey = DayKey(entry.Timestamp);
        _days.TryGetValue(dayKey, out var day);
        day ??= new LocalDayTotals();
        _days[dayKey] = day with
        {
            InputTokens = day.InputTokens + entry.InputTokens,
            OutputTokens = day.OutputTokens + entry.OutputTokens,
            CacheWriteTokens = day.CacheWriteTokens + entry.CacheWrite5mTokens + entry.CacheWrite1hTokens,
            CacheReadTokens = day.CacheReadTokens + entry.CacheReadTokens,
            CostUsd = day.CostUsd + cost,
            HasUnpricedModels = day.HasUnpricedModels || pricing is null,
        };

        if (entry.Timestamp >= now - RecentCostRetention)
            _recentCosts.Add(new RecentCostSample(entry.Timestamp, cost, entry.TotalTokens));
    }

    // Caller holds _lock. Drops everything outside its retention window;
    // returns true when anything was removed.
    private bool PruneLocked(DateTimeOffset now)
    {
        var changed = false;

        var minDayKey = DayKey(now - AggregateRetention);
        var oldDays = _days.Keys
            .Where(k => string.CompareOrdinal(k, minDayKey) < 0)
            .ToList();
        foreach (var key in oldDays)
        {
            _days.Remove(key);
            changed = true;
        }

        var keyCutoff = now - DedupeKeyRetention;
        var oldKeys = _recentKeys
            .Where(kv => kv.Value < keyCutoff)
            .Select(kv => kv.Key)
            .ToList();
        foreach (var key in oldKeys)
        {
            _recentKeys.Remove(key);
            changed = true;
        }

        var costCutoff = now - RecentCostRetention;
        changed |= _recentCosts.RemoveAll(s => s.Timestamp < costCutoff) > 0;

        return changed;
    }

    // Caller holds _lock. Best-effort persistence, temp-file + atomic move —
    // the same contract as UsageHistoryStore: a write failure must never
    // disrupt scanning, and a crash mid-write can't corrupt the next load.
    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_cachePath);
            if (dir is not null && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var cache = new LocalUsageCacheFile
            {
                Files = new Dictionary<string, FileScanState>(_files),
                Days = new Dictionary<string, LocalDayTotals>(_days),
                RecentDedupeKeys = new Dictionary<string, DateTimeOffset>(_recentKeys),
                RecentCosts = new List<RecentCostSample>(_recentCosts),
            };

            var tmp = _cachePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(cache, JsonOptions));
            File.Move(tmp, _cachePath, overwrite: true);
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or NotSupportedException or JsonException)
        {
            // Ignore — the cache is an optimization; the transcripts remain the
            // source of truth and the window rebuilds on the next cold start.
        }
    }

    // Aggregates are keyed by the user's local calendar date so "today" matches
    // the wall clock, not UTC.
    private static string DayKey(DateTimeOffset timestamp) =>
        timestamp.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string GetDefaultProjectsDir() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".claude",
            "projects");

    private static string GetDefaultCachePath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ClaudeMon",
            "local-usage.json");
}
