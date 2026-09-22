namespace ClaudeMon.Services;

using System.Globalization;
using System.Text.Json;
using ClaudeMon.Models;

/// <summary>
/// ClaudeMon's long-term home for daily usage aggregates (issue #126), under
/// %LocalAppData%\ClaudeMon\usage-warehouse\: one file per calendar month
/// (usage-YYYY-MM.json) holding the same per-(day, project, model) cells the
/// scanner keeps, for days that have rolled past its 30-day retention.
///
/// Why it exists: Claude Code purges transcripts after about 30 days and
/// <see cref="LocalUsageStore"/> mirrors that, so cost history silently
/// truncated. The warehouse is written from the scanner's own finalized cells
/// and is never rebuilt from the transcripts — once a day is past the transcript
/// horizon this is the only copy, so nothing here may be discarded casually: a
/// month whose schema version doesn't match is skipped on read and left alone on
/// disk, and a day is only ever removed by explicit retention pruning.
///
/// Writes are whole-day replacements, so a re-scan that amends a recent day
/// (a truncated file, a restart, a dedupe correction) converges instead of
/// double-counting. Per-month files keep each write small no matter how many
/// years accumulate, and reads open only the months a range intersects.
///
/// Thread-safe and best-effort: every IO failure is swallowed, since losing a
/// warehouse write must never disrupt a scan — the same contract as
/// <see cref="UsageHistoryStore"/> and <see cref="LimitLogStore"/>.
/// </summary>
public sealed class UsageWarehouse
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private readonly string _dir;
    private readonly Func<int> _retentionDays;
    private readonly Logger? _logger;
    private readonly object _lock = new();
    // "yyyy-MM" → the month's parsed contents. Populated on first touch and kept
    // in step with what this process writes, so a roll-in doesn't re-read the
    // month it just wrote. Bounded by the months actually touched in a session.
    private readonly Dictionary<string, UsageWarehouseMonth> _months = new(StringComparer.Ordinal);
    // Warnings already emitted, as "condition:month". A month that is locked by
    // something else stays locked, and the scan retries it every minute — the
    // first line is worth having, 1,440 a day would churn the log's history out.
    private readonly HashSet<string> _loggedProblems = new(StringComparer.Ordinal);

    /// <param name="retentionDays">
    /// How many days back to keep, evaluated on each prune so a settings change
    /// applies without a restart. Zero or negative means unlimited — the default,
    /// because these aggregates are tiny and the history can't be recovered later.
    /// </param>
    public UsageWarehouse(string? dir = null, Func<int>? retentionDays = null, Logger? logger = null)
    {
        _dir = dir ?? GetDefaultDir();
        _retentionDays = retentionDays ?? (() => 0);
        _logger = logger;
    }

    /// <summary>
    /// Records finalized days, replacing each day's cells wholesale — an amended
    /// day converges on the newer figures rather than accumulating them. The
    /// live cells win on a re-bank by design: within the transcript window they
    /// are the fresher reading, and past it nothing re-banks a day at all.
    /// <paramref name="projectPaths"/> supplies display paths; only the projects
    /// a month actually contains are stored in that month's file, and a path
    /// already on disk survives a scan that no longer knows it.
    ///
    /// Returns the day keys that actually landed on disk. Per-month, not
    /// all-or-nothing: one unwritable month must not strand the days belonging
    /// to a healthy one, or a single damaged file would have the caller retry
    /// its whole back catalogue on every scan, forever.
    /// </summary>
    public IReadOnlyCollection<string> UpsertDays(
        IReadOnlyDictionary<string, Dictionary<string, LocalDayTotals>> days,
        IReadOnlyDictionary<string, string> projectPaths)
    {
        if (days.Count == 0)
            return [];

        lock (_lock)
        {
            var touched = new Dictionary<string, UsageWarehouseMonth>(StringComparer.Ordinal);
            var daysByMonth = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var (dayKey, cells) in days)
            {
                var monthKey = MonthKeyOf(dayKey);
                if (monthKey is null)
                {
                    _logger?.Warn($"Usage warehouse: ignoring malformed day key '{dayKey}'.");
                    continue;
                }

                var month = LoadMonthLocked(monthKey);
                // A month this process can't write (foreign version, unreadable,
                // or briefly unavailable) is left entirely alone, cache included:
                // the days stay unbanked and the caller retries them.
                if (month.Version != UsageWarehouseMonth.CurrentVersion)
                    continue;

                // Staged on a copy and adopted only once the write lands (the
                // same invariant Prune keeps), so the cache never advertises a
                // day the file doesn't have.
                if (!touched.TryGetValue(monthKey, out var staged))
                {
                    touched[monthKey] = staged = month with
                    {
                        Days = new Dictionary<string, Dictionary<string, LocalDayTotals>>(month.Days, StringComparer.Ordinal),
                        ProjectPaths = new Dictionary<string, string>(month.ProjectPaths, StringComparer.OrdinalIgnoreCase),
                    };
                }

                staged.Days[dayKey] = new Dictionary<string, LocalDayTotals>(cells, StringComparer.Ordinal);
                if (!daysByMonth.TryGetValue(monthKey, out var monthDays))
                    daysByMonth[monthKey] = monthDays = [];
                monthDays.Add(dayKey);
            }

            var banked = new List<string>();
            foreach (var (monthKey, month) in touched)
            {
                MergeProjectPathsLocked(month, projectPaths);
                if (!SaveMonthLocked(monthKey, month))
                    continue;

                _months[monthKey] = month;
                banked.AddRange(daysByMonth[monthKey]);
            }

            return banked;
        }
    }

    /// <summary>
    /// The warehoused cells for a local date range, plus the project paths that
    /// go with them. Only the months the range intersects are opened; missing,
    /// unreadable, and foreign-version months contribute nothing rather than
    /// failing the read.
    /// </summary>
    public UsageWarehouseRange ReadRange(DateOnly from, DateOnly to)
    {
        if (to < from)
            return UsageWarehouseRange.Empty;

        lock (_lock)
        {
            var days = new Dictionary<string, IReadOnlyDictionary<string, LocalDayTotals>>(StringComparer.Ordinal);
            var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            var fromKey = DayKeyOf(from);
            var toKey = DayKeyOf(to);
            var lastMonth = new DateOnly(to.Year, to.Month, 1);
            for (var month = new DateOnly(from.Year, from.Month, 1);
                 month <= lastMonth;
                 month = month == lastMonth ? DateOnly.MaxValue : month.AddMonths(1))
            {
                var loaded = LoadMonthLocked(MonthKeyOf(month));
                foreach (var (dayKey, cells) in loaded.Days)
                {
                    // String comparison is exact for "yyyy-MM-dd" and spares the
                    // parse; the boundary months are the only ones that need it.
                    if (string.CompareOrdinal(dayKey, fromKey) < 0 ||
                        string.CompareOrdinal(dayKey, toKey) > 0)
                    {
                        continue;
                    }

                    days[dayKey] = new Dictionary<string, LocalDayTotals>(cells, StringComparer.Ordinal);
                }

                foreach (var (project, path) in loaded.ProjectPaths)
                    paths.TryAdd(project, path);
            }

            return days.Count == 0 && paths.Count == 0
                ? UsageWarehouseRange.Empty
                : new UsageWarehouseRange(days, paths);
        }
    }

    /// <summary>
    /// Drops days older than the configured retention: whole month files whose
    /// every day is past the cutoff are deleted, and the month the cutoff falls
    /// in is rewritten without its old days. A no-op under unlimited retention
    /// (the default), which is also the only case that costs nothing — pruning
    /// enumerates the directory, so callers run it on roll-in, not per poll.
    /// </summary>
    public void Prune(DateTimeOffset now)
    {
        var retention = _retentionDays();
        if (retention <= 0)
            return;

        var cutoff = DateOnly.FromDateTime(now.ToLocalTime().DateTime).AddDays(-retention);
        var cutoffDayKey = DayKeyOf(cutoff);
        var cutoffMonthKey = MonthKeyOf(cutoff);

        lock (_lock)
        {
            List<string> files;
            try
            {
                if (!Directory.Exists(_dir))
                    return;

                files = Directory.EnumerateFiles(_dir, "usage-*.json").ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return;
            }

            foreach (var path in files)
            {
                var monthKey = MonthKeyOfFile(path);
                if (monthKey is null || string.CompareOrdinal(monthKey, cutoffMonthKey) > 0)
                    continue;

                // Deliberately opened rather than judged by filename: a month
                // written by another schema version (or one this process can't
                // read) is skipped, never deleted — the read path goes to real
                // lengths not to clobber those, and pruning must not undo that.
                // It also means a foreign file whose days aren't where its name
                // implies can't be destroyed on the strength of the name alone.
                var month = LoadMonthLocked(monthKey);
                if (month.Version != UsageWarehouseMonth.CurrentVersion)
                    continue;

                var expired = month.Days.Keys
                    .Where(d => string.CompareOrdinal(d, cutoffDayKey) < 0)
                    .ToList();
                if (expired.Count == 0)
                    continue;

                if (expired.Count == month.Days.Count)
                {
                    // Nothing left to keep — the whole file goes.
                    try
                    {
                        File.Delete(path);
                        _months.Remove(monthKey);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // Retried on the next roll-in; a stale month costs disk, not correctness.
                    }
                    continue;
                }

                // Trim a copy and only adopt it once the write lands, so a failed
                // save can't leave the cache thinner than the file it mirrors.
                var kept = month.Days
                    .Where(kv => string.CompareOrdinal(kv.Key, cutoffDayKey) >= 0)
                    .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
                var trimmed = month with
                {
                    Days = kept,
                    ProjectPaths = new Dictionary<string, string>(month.ProjectPaths, StringComparer.OrdinalIgnoreCase),
                };
                PruneProjectPathsLocked(trimmed);

                if (SaveMonthLocked(monthKey, trimmed))
                    _months[monthKey] = trimmed;
            }
        }
    }

    /// <summary>
    /// The oldest local day held on disk, or null when the warehouse is empty —
    /// how a future long-range view knows how far back it can ask (issue #68).
    /// </summary>
    public DateOnly? OldestDay()
    {
        lock (_lock)
        {
            List<string> files;
            try
            {
                if (!Directory.Exists(_dir))
                    return null;

                files = Directory.EnumerateFiles(_dir, "usage-*.json").ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }

            // Month keys sort as strings exactly as they sort as dates, so the
            // oldest non-empty month is found without opening every file.
            var monthKeys = files
                .Select(MonthKeyOfFile)
                .Where(k => k is not null)
                .Select(k => k!)
                .OrderBy(k => k, StringComparer.Ordinal);

            foreach (var monthKey in monthKeys)
            {
                var month = LoadMonthLocked(monthKey);
                if (month.Days.Count == 0)
                    continue;

                var oldest = month.Days.Keys.Min(StringComparer.Ordinal)!;
                if (DateOnly.TryParseExact(
                        oldest, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
                {
                    return day;
                }
            }

            return null;
        }
    }

    // Caller holds _lock. Reads a month from disk once and memoizes it. A file
    // that is missing, unreadable, corrupt, or written by another schema version
    // reads as an empty month — which is why writes always merge into what was
    // loaded rather than assuming they own the file.
    private UsageWarehouseMonth LoadMonthLocked(string monthKey)
    {
        if (_months.TryGetValue(monthKey, out var cached))
            return cached;

        var month = ReadMonth(monthKey);
        if (month is null)
        {
            // A transient read failure is deliberately not memoized: version 0
            // blocks this pass from overwriting a file it couldn't read, and the
            // next pass tries again rather than being stuck until a restart.
            return NewMonth() with { Version = 0 };
        }

        _months[monthKey] = month;
        return month;
    }

    // Null means the month couldn't be read this time (and might be readable
    // next time); every other outcome — missing file, foreign version — is a
    // stable answer the caller can cache.
    private UsageWarehouseMonth? ReadMonth(string monthKey)
    {
        var path = MonthPath(monthKey);
        try
        {
            if (!File.Exists(path))
                return NewMonth();

            var parsed = JsonSerializer.Deserialize<UsageWarehouseMonth>(File.ReadAllText(path), JsonOptions);
            if (parsed is null)
                return NewMonth();

            if (parsed.Version != UsageWarehouseMonth.CurrentVersion)
            {
                // Deliberately not deleted or overwritten: a file from another
                // version may hold days no transcript can rebuild. Skipping it
                // costs a gap in the view; clobbering it would cost the data.
                _logger?.Warn(
                    $"Usage warehouse {Path.GetFileName(path)} is format v{parsed.Version} " +
                    $"(current v{UsageWarehouseMonth.CurrentVersion}) — left untouched and skipped.");
                return NewMonth() with { Version = parsed.Version };
            }

            // Null-tolerant throughout: System.Text.Json happily overwrites the
            // property initializers with nulls from a hand-edited file, and this
            // runs on a timer against the only surviving copy of the history — an
            // unhandled NullReferenceException here would abort the scan pass.
            var days = new Dictionary<string, Dictionary<string, LocalDayTotals>>(StringComparer.Ordinal);
            foreach (var (dayKey, cells) in parsed.Days ?? [])
            {
                days[dayKey] = cells is null
                    ? new Dictionary<string, LocalDayTotals>(StringComparer.Ordinal)
                    : new Dictionary<string, LocalDayTotals>(cells, StringComparer.Ordinal);
            }

            // Indexer rather than a copy-construct: two project keys differing
            // only in case are legal JSON but would throw on an
            // OrdinalIgnoreCase copy. Last one wins, like everywhere else here.
            var projects = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (project, cwd) in parsed.ProjectPaths ?? [])
                projects[project] = cwd;

            _loggedProblems.Remove($"open:{monthKey}");
            return new UsageWarehouseMonth
            {
                Version = parsed.Version,
                Days = days,
                ProjectPaths = projects,
            };
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            // Unparseable content is a stable answer, so it is cached (no
            // per-scan re-read) — but like a foreign version it blocks writes:
            // a damaged file may still hold recoverable days, and this is the
            // only copy of anything past the transcript horizon.
            _logger?.Warn($"Usage warehouse {Path.GetFileName(path)} is unreadable — left untouched and skipped: {ex.Message}");
            return NewMonth() with { Version = 0 };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Locked or briefly unavailable — might well work next pass.
            WarnOnceLocked("open", monthKey, $"Usage warehouse {Path.GetFileName(path)} could not be opened: {ex.Message}");
            return null;
        }
    }

    private static UsageWarehouseMonth NewMonth() => new()
    {
        Version = UsageWarehouseMonth.CurrentVersion,
        Days = new Dictionary<string, Dictionary<string, LocalDayTotals>>(StringComparer.Ordinal),
        ProjectPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
    };

    // Caller holds _lock. Temp file + atomic move, like every other store here.
    // Returns false when the month was left as it was, so the caller keeps the
    // days pending instead of treating them as banked.
    private bool SaveMonthLocked(string monthKey, UsageWarehouseMonth month)
    {
        if (month.Version != UsageWarehouseMonth.CurrentVersion)
            return false; // Foreign or unreadable — see ReadMonth.

        var path = MonthPath(monthKey);
        try
        {
            if (!Directory.Exists(_dir))
                Directory.CreateDirectory(_dir);

            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(month, JsonOptions));
            File.Move(tmp, path, overwrite: true);
            _loggedProblems.Remove($"write:{monthKey}");
            return true;
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or NotSupportedException or JsonException)
        {
            WarnOnceLocked("write", monthKey, $"Usage warehouse {Path.GetFileName(path)} could not be written: {ex.Message}");
            return false;
        }
    }

    // Caller holds _lock. One line per condition per month, so a persistently
    // locked file doesn't drown the log; a month that recovers and fails again
    // re-arms, since success removes the marker.
    private void WarnOnceLocked(string condition, string monthKey, string message)
    {
        if (_loggedProblems.Add($"{condition}:{monthKey}"))
            _logger?.Warn(message);
    }

    // Caller holds _lock. Learns display paths for the projects this month
    // contains. New knowledge wins (a project moved), but a path already stored
    // is never dropped just because the current scan no longer knows it — the
    // scanner forgets a path as soon as its last live day ages out.
    private static void MergeProjectPathsLocked(
        UsageWarehouseMonth month, IReadOnlyDictionary<string, string> projectPaths)
    {
        if (projectPaths.Count == 0)
            return;

        foreach (var project in ProjectsIn(month))
        {
            if (projectPaths.TryGetValue(project, out var path) && path.Length > 0)
                month.ProjectPaths[project] = path;
        }
    }

    // Caller holds _lock. Drops paths for projects no longer present in the
    // month, so pruning a day can't leave its project behind forever.
    private static void PruneProjectPathsLocked(UsageWarehouseMonth month)
    {
        var live = ProjectsIn(month);
        foreach (var project in month.ProjectPaths.Keys.Where(p => !live.Contains(p)).ToList())
            month.ProjectPaths.Remove(project);
    }

    private static HashSet<string> ProjectsIn(UsageWarehouseMonth month)
    {
        var projects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var cells in month.Days.Values)
            foreach (var cellKey in cells.Keys)
            {
                var sep = cellKey.IndexOf('|');
                projects.Add(sep < 0 ? LocalUsageStore.UnknownProject : cellKey[..sep]);
            }

        return projects;
    }

    private string MonthPath(string monthKey) => Path.Combine(_dir, $"usage-{monthKey}.json");

    // "yyyy-MM-dd" → "yyyy-MM". Day keys are written by the scanner in exactly
    // that invariant shape, so this is a slice rather than a parse; anything
    // else is refused rather than being filed under a wrong month.
    private static string? MonthKeyOf(string dayKey) =>
        dayKey.Length == 10 && dayKey[4] == '-' && dayKey[7] == '-' ? dayKey[..7] : null;

    private static string MonthKeyOf(DateOnly date) =>
        date.ToString("yyyy-MM", CultureInfo.InvariantCulture);

    private static string DayKeyOf(DateOnly date) =>
        date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    // "…/usage-2026-07.json" → "2026-07", or null for a file whose name doesn't
    // parse as a month (a stray file must never be deleted by pruning).
    private static string? MonthKeyOfFile(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        const string prefix = "usage-";
        if (!name.StartsWith(prefix, StringComparison.Ordinal))
            return null;

        var key = name[prefix.Length..];
        return DateTime.TryParseExact(
            key, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
            ? key
            : null;
    }

    private static string GetDefaultDir() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ClaudeMon",
            "usage-warehouse");
}
