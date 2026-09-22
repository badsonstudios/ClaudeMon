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
/// Memory is strictly bounded: per-(day, project, model) cells for the
/// retention window, a ~48-hour dedupe-key map, one offset record per file,
/// one learned path per live project, and an hour of burn-rate samples. Raw
/// entries are never retained, and nothing beyond the usage numbers, model id,
/// ids, timestamp, and the session's working-directory path (for the project
/// display name) is materialized from a line — never message content.
///
/// Thread-safe: scans run on a timer thread while the UI thread takes
/// snapshots. When the transcript directory doesn't exist the store degrades
/// silently — <see cref="Snapshot"/> returns null and the UI omits the line.
///
/// Retention is bounded here because the transcripts themselves are: Claude Code
/// purges them after about 30 days. Given a <see cref="UsageWarehouse"/>, each
/// day's finalized cells are banked there before they age out, and queries over
/// a range older than the window read back through it (issue #126).
/// </summary>
public sealed class LocalUsageStore
{
    /// <summary>
    /// How far back aggregates are kept (and old files fast-forwarded past).
    /// 30 days so the breakdown window's longest timeframe is fully covered.
    /// </summary>
    internal static readonly TimeSpan AggregateRetention = TimeSpan.FromDays(30);

    /// <summary>Project key for stray .jsonl files sitting directly in the projects root.</summary>
    internal const string UnknownProject = "(unknown)";

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
    private readonly UsageWarehouse? _warehouse;
    private readonly object _lock = new();
    private bool _scanning;
    private bool _available;

    private readonly Dictionary<string, FileScanState> _files = new(StringComparer.OrdinalIgnoreCase);
    // day "yyyy-MM-dd" (local) → cell "project|model" → totals. The flyout's
    // today line sums a day's cells; the breakdown window slices them by axis.
    private readonly Dictionary<string, Dictionary<string, LocalDayTotals>> _cells = new(StringComparer.Ordinal);
    // project dir name → real cwd path learned from the transcripts (first seen wins).
    private readonly Dictionary<string, string> _projectPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _recentKeys = new(StringComparer.Ordinal);
    private readonly List<RecentCostSample> _recentCosts = new();
    private readonly HashSet<string> _loggedUnknownModels = new(StringComparer.OrdinalIgnoreCase);
    // Days whose cells have changed since they were last banked in the warehouse
    // (issue #126). Only cleared by a successful roll-in, so a failed write is
    // retried rather than silently dropped, and a day stays pending for as long
    // as it keeps changing — which is the whole point of only banking finalized days.
    private readonly HashSet<string> _pendingWarehouseDays = new(StringComparer.Ordinal);
    // Cells for pending days that have since aged out of the live window without
    // ever being banked (a locked or damaged warehouse file). Their transcripts
    // are gone, so this is the last copy — it outlives the prune that dropped
    // them from _cells, and is persisted so a restart doesn't finish the job.
    private readonly Dictionary<string, Dictionary<string, LocalDayTotals>> _unbankedDays = new(StringComparer.Ordinal);

    /// <summary>
    /// How many aged-out unbanked days are carried at once. Only a warehouse
    /// that has been failing for months can approach this; the cap keeps a
    /// permanently broken one from growing the cache without bound.
    /// </summary>
    internal const int MaxUnbankedDays = 90;

    public LocalUsageStore(
        string? projectsDir = null,
        string? cachePath = null,
        PricingTable? pricing = null,
        Logger? logger = null,
        Func<DateTimeOffset>? clock = null,
        UsageWarehouse? warehouse = null)
    {
        _projectsDir = projectsDir ?? GetDefaultProjectsDir();
        _cachePath = cachePath ?? GetDefaultCachePath();
        _pricing = pricing ?? new PricingTable(new Dictionary<string, ModelPricing>());
        _logger = logger;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _warehouse = warehouse;
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

                // Schema mismatch (including a phase-1 cache, which has no "v"
                // field and deserializes as 0): discard and rebuild from the
                // transcripts — old flat totals can't be split into cells.
                if (cache.Version != LocalUsageCacheFile.CurrentVersion)
                {
                    _logger?.Info(
                        $"Local usage cache is format v{cache.Version} (current v{LocalUsageCacheFile.CurrentVersion}) — rescanning transcripts.");
                    return;
                }

                // Null-coalesced throughout: System.Text.Json overwrites the
                // property initializers with nulls from a hand-edited cache, and
                // this runs inline on the startup path — an NRE here would cost
                // the tray icon entirely.
                foreach (var (path, state) in cache.Files ?? []) _files[path] = state;
                foreach (var (day, cells) in cache.Cells ?? [])
                {
                    _cells[day] = new Dictionary<string, LocalDayTotals>(cells ?? [], StringComparer.Ordinal);
                    // Every cached day is treated as pending: the warehouse may
                    // never have seen it (first run after the upgrade that added
                    // it, or a run that ended before a rollover), and re-banking
                    // a day it already holds is a no-op by construction.
                    _pendingWarehouseDays.Add(day);
                }
                foreach (var (project, cwd) in cache.ProjectPaths ?? []) _projectPaths[project] = cwd;
                foreach (var (day, cells) in cache.UnbankedDays ?? [])
                {
                    _unbankedDays[day] = new Dictionary<string, LocalDayTotals>(cells ?? [], StringComparer.Ordinal);
                    _pendingWarehouseDays.Add(day);
                }
                foreach (var (key, ts) in cache.RecentDedupeKeys ?? []) _recentKeys[key] = ts;
                _recentCosts.AddRange(cache.RecentCosts ?? []);
            }
            catch (Exception ex) when (
                ex is IOException or JsonException or UnauthorizedAccessException or ArgumentException)
            {
                // Corrupt or unreadable cache is non-critical — start fresh and
                // rebuild the retention window from the transcripts themselves.
                _files.Clear();
                _cells.Clear();
                _projectPaths.Clear();
                _recentKeys.Clear();
                _recentCosts.Clear();
                _pendingWarehouseDays.Clear();
                _unbankedDays.Clear();
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
            var day = SumDayLocked(DayKey(now));
            if (day.TotalTokens == 0)
                return null;

            // Deliberately divides by the full window even when activity only
            // covers part of it, so a session's first minutes read low and ramp
            // up as the window fills — smoother than extrapolating one burst,
            // and consistent with how the API-side burn rate behaves.
            double? burnRate = null;
            double? tokenRate = null;
            double? cacheReadRate = null;
            var cutoff = now - BurnRateWindow;
            var sum = 0.0;
            var tokenSum = 0L;
            var cacheReadSum = 0L;
            var any = false;
            foreach (var sample in _recentCosts)
            {
                if (sample.Timestamp >= cutoff)
                {
                    sum += sample.CostUsd;
                    tokenSum += sample.Tokens;
                    cacheReadSum += sample.CacheReadTokens;
                    any = true;
                }
            }
            if (any)
            {
                burnRate = sum / BurnRateWindow.TotalHours;
                tokenRate = tokenSum / BurnRateWindow.TotalHours;
                // The cache-read share of the token rate, so the flat-plan projection
                // (issue #202) can discount cache reads without a Services→Monitoring
                // dependency — the weighting itself lives with the capacity estimator.
                cacheReadRate = cacheReadSum / BurnRateWindow.TotalHours;
            }

            return new LocalUsageSnapshot(
                DateOnly.FromDateTime(now.ToLocalTime().DateTime),
                day.CostUsd,
                day.HasUnpricedModels,
                day.TotalTokens,
                day.CacheWriteTokens,
                day.CacheReadTokens,
                burnRate,
                tokenRate,
                cacheReadRate);
        }
    }

    /// <summary>
    /// Per-model and per-project tables for a timeframe ending today, or null
    /// when the feature is unavailable. Empty tables (no usage in range) are
    /// returned as empty lists — the UI shows its empty state.
    /// </summary>
    public LocalUsageBreakdown? Breakdown(BreakdownTimeframe timeframe)
    {
        var (from, to) = RangeOf(timeframe);
        return Breakdown(from, to);
    }

    /// <summary>
    /// The same tables over an explicit local date range, reading through to the
    /// warehouse for days the live cells no longer cover (issue #126) — the entry
    /// point a longer-than-30-day view uses (issue #68). Ranges inside the live
    /// window never touch the warehouse, so the timeframes the UI offers today
    /// behave exactly as before.
    /// </summary>
    internal LocalUsageBreakdown? Breakdown(DateOnly from, DateOnly to)
    {
        // Read before taking the lock: the warehouse hits the disk, and this runs
        // on the UI thread while the scan thread holds the lock.
        var warehouse = ReadWarehouse(from, to);

        lock (_lock)
        {
            if (!_available)
                return null;

            var projectPaths = MergedProjectPathsLocked(warehouse);
            var byModel = new Dictionary<string, BreakdownRow>(StringComparer.OrdinalIgnoreCase);
            var byProject = new Dictionary<string, BreakdownRow>(StringComparer.OrdinalIgnoreCase);
            // Keyed by the whole "project|model" cell key, case-insensitively so
            // pairs merge exactly where the two axis tables above merge.
            var pairs = new Dictionary<string, BreakdownPair>(StringComparer.OrdinalIgnoreCase);
            var totals = EmptyRow("total", "Total");

            for (var date = from; date <= to; date = date.AddDays(1))
            {
                var dayCells = CellsForLocked(DayKeyOf(date), warehouse);
                if (dayCells is null)
                    continue;

                foreach (var (cellKey, cell) in dayCells)
                {
                    var (project, model) = SplitCellKey(cellKey);
                    var display = ProjectDisplay.Resolve(project, projectPaths);

                    Fold(byModel, model, model, cell);
                    Fold(byProject, project, display, cell);
                    FoldPair(pairs, cellKey, project, display, model, cell);
                    totals = Add(totals, cell);
                }
            }

            return new LocalUsageBreakdown(from, to, Sorted(byModel), Sorted(byProject), totals)
            {
                Pairs = pairs.Values.ToList(),
            };
        }
    }

    // The local date range a timeframe covers, ending today.
    private (DateOnly From, DateOnly To) RangeOf(BreakdownTimeframe timeframe)
    {
        var today = DateOnly.FromDateTime(_clock().ToLocalTime().DateTime);
        var days = timeframe switch
        {
            BreakdownTimeframe.Today => 1,
            BreakdownTimeframe.SevenDays => 7,
            _ => 30,
        };
        return (today.AddDays(-(days - 1)), today);
    }

    /// <summary>
    /// Cost per local calendar day across a timeframe ending today, for the
    /// breakdown window's chart — or null when the feature is unavailable.
    /// Every day in range gets a point (days with no usage read $0), so the
    /// series is already the chart's dated x-axis. Cells are keyed by day, so
    /// this is a pure re-aggregation of cached data: no transcript rescan.
    /// </summary>
    public LocalCostSeries? CostSeries(BreakdownTimeframe timeframe)
    {
        // Same range helper as Breakdown, so the chart and the tables can never
        // disagree about which days a timeframe covers.
        var (from, to) = RangeOf(timeframe);
        return CostSeries(from, to);
    }

    /// <summary>
    /// The same series over an explicit local date range, reading through to the
    /// warehouse for days outside the live window — see
    /// <see cref="Breakdown(DateOnly, DateOnly)"/>.
    /// </summary>
    internal LocalCostSeries? CostSeries(DateOnly from, DateOnly to)
    {
        var warehouse = ReadWarehouse(from, to);

        lock (_lock)
        {
            if (!_available)
                return null;

            var days = new List<DailyCost>();
            for (var date = from; date <= to; date = date.AddDays(1))
            {
                var totals = SumDayLocked(DayKeyOf(date), warehouse);
                days.Add(new DailyCost(date, totals.CostUsd, totals.HasUnpricedModels));
            }

            return new LocalCostSeries(from, to, days);
        }
    }

    /// <summary>
    /// Month-to-date estimated cost for the flat-plan value line (issue #202). Built on
    /// <see cref="CostSeries(DateOnly, DateOnly)"/>, so near a month boundary — when the
    /// month's first days have rolled out of the live window — it reads through to the
    /// warehouse like every other range query. Null when the feature is unavailable.
    /// </summary>
    public LocalMonthTotals? MonthToDate()
    {
        var today = DateOnly.FromDateTime(_clock().ToLocalTime().DateTime);
        var monthStart = new DateOnly(today.Year, today.Month, 1);
        var series = CostSeries(monthStart, today);
        if (series is null)
            return null;

        return new LocalMonthTotals(
            monthStart, today, series.TotalCostUsd, series.HasUnpricedModels);
    }

    /// <summary>
    /// The warehoused cells a range needs, or <see cref="UsageWarehouseRange.Empty"/>
    /// when there's no warehouse or the range lies entirely inside the live
    /// window — which is every timeframe the UI offers today, so the hot path
    /// stays free of file IO.
    /// </summary>
    private UsageWarehouseRange ReadWarehouse(DateOnly from, DateOnly to)
    {
        if (_warehouse is null)
            return UsageWarehouseRange.Empty;

        lock (_lock)
        {
            // Nothing to read through to when the feature is off — and this is
            // checked before touching the disk, not after.
            if (!_available)
                return UsageWarehouseRange.Empty;
        }

        var oldestLive = DateOnly.FromDateTime((_clock() - AggregateRetention).ToLocalTime().DateTime);
        if (from >= oldestLive)
            return UsageWarehouseRange.Empty;

        // Only the part the live cells can't answer. The boundary day itself is
        // included rather than excluded: live cells win wherever both have a day
        // (see CellsForLocked), so the overlap costs nothing — and it means this
        // boundary and the pruner's, computed at different instants, don't have
        // to agree to the day for the seam to render — including when a scan
        // crosses local midnight between this read and the caller taking the lock.
        var overlap = oldestLive.AddDays(1);
        return _warehouse.ReadRange(from, overlap < to ? overlap : to);
    }

    // Caller holds _lock. The live cells for a day, falling back to the
    // warehouse and then to a carried unbanked day; null when none has it. Live
    // wins wherever both do — a day still in the window may have been amended
    // since it was banked.
    private IReadOnlyDictionary<string, LocalDayTotals>? CellsForLocked(
        string dayKey, UsageWarehouseRange warehouse)
    {
        if (_cells.TryGetValue(dayKey, out var live))
            return live;

        if (warehouse.Days.TryGetValue(dayKey, out var banked))
            return banked;

        // A day the warehouse hasn't accepted yet still exists and still cost
        // money; it would be odd to carry the only copy and then render it $0.
        return _unbankedDays.TryGetValue(dayKey, out var carried) ? carried : null;
    }

    // Caller holds _lock. Learned project paths for display, with the warehouse's
    // filling in the projects the live map has already forgotten.
    private IReadOnlyDictionary<string, string> MergedProjectPathsLocked(UsageWarehouseRange warehouse)
    {
        if (warehouse.ProjectPaths.Count == 0)
            return _projectPaths;

        var merged = new Dictionary<string, string>(_projectPaths, StringComparer.OrdinalIgnoreCase);
        foreach (var (project, path) in warehouse.ProjectPaths)
            merged.TryAdd(project, path);
        return merged;
    }

    /// <summary>
    /// Cumulative token totals by (normalized) model across every retained day — the local
    /// half of the correlated limit log's samples (issue #184). Cumulative within the 30-day
    /// retention window, so totals can dip when old days age out; the log's delta math clamps
    /// for that. A read-only re-aggregation of the existing cells — no transcript rescan.
    /// Null when the transcript directory is unavailable.
    ///
    /// Deliberately does NOT read through to the warehouse (issue #126): the limit log takes
    /// deltas of these totals between polls, so widening the window would land the whole back
    /// catalogue on a single poll as if it had just been burned.
    /// </summary>
    public Dictionary<string, ModelTokens>? TokensByModel()
    {
        lock (_lock)
        {
            if (!_available)
                return null;

            var totals = new Dictionary<string, ModelTokens>(StringComparer.OrdinalIgnoreCase);
            foreach (var dayCells in _cells.Values)
            {
                foreach (var (cellKey, cell) in dayCells)
                {
                    var model = SplitCellKey(cellKey).Model;
                    totals.TryGetValue(model, out var t);
                    totals[model] = (t ?? ModelTokens.Zero).Plus(new ModelTokens(
                        cell.InputTokens, cell.OutputTokens, cell.CacheWriteTokens, cell.CacheReadTokens));
                }
            }

            return totals;
        }
    }

    /// <summary>
    /// The sums the budget alerts compare against their caps: today, and the
    /// current local calendar week (Monday through today). Null when unavailable.
    /// </summary>
    public LocalBudgetTotals? BudgetTotals()
    {
        lock (_lock)
        {
            if (!_available)
                return null;

            var today = DateOnly.FromDateTime(_clock().ToLocalTime().DateTime);
            // Monday-start week: DayOfWeek is Sunday=0, so shift to Monday=0.
            var monday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));

            var todayUsd = SumDayLocked(DayKeyOf(today)).CostUsd;
            var weekUsd = 0.0;
            for (var date = monday; date <= today; date = date.AddDays(1))
                weekUsd += SumDayLocked(DayKeyOf(date)).CostUsd;

            return new LocalBudgetTotals(today, todayUsd, monday, weekUsd);
        }
    }

    // Caller holds _lock. One day's cells summed into a flat total (the
    // phase-1 per-day shape the flyout snapshot still consumes).
    private LocalDayTotals SumDayLocked(string dayKey) =>
        SumDayLocked(dayKey, UsageWarehouseRange.Empty);

    // Caller holds _lock. As above, reading through to the warehouse for days
    // the live cells no longer cover.
    private LocalDayTotals SumDayLocked(string dayKey, UsageWarehouseRange warehouse)
    {
        var sum = new LocalDayTotals();
        var dayCells = CellsForLocked(dayKey, warehouse);
        if (dayCells is null)
            return sum;

        foreach (var cell in dayCells.Values)
            sum = Add(sum, cell);
        return sum;
    }

    private static LocalDayTotals Add(LocalDayTotals a, LocalDayTotals b) => a with
    {
        InputTokens = a.InputTokens + b.InputTokens,
        OutputTokens = a.OutputTokens + b.OutputTokens,
        CacheWriteTokens = a.CacheWriteTokens + b.CacheWriteTokens,
        CacheReadTokens = a.CacheReadTokens + b.CacheReadTokens,
        CostUsd = a.CostUsd + b.CostUsd,
        HasUnpricedModels = a.HasUnpricedModels || b.HasUnpricedModels,
    };

    private static BreakdownRow EmptyRow(string key, string display) =>
        new(key, display, 0, 0, 0, 0, 0.0, false);

    private static BreakdownRow Add(BreakdownRow row, LocalDayTotals cell) => row with
    {
        InputTokens = row.InputTokens + cell.InputTokens,
        OutputTokens = row.OutputTokens + cell.OutputTokens,
        CacheWriteTokens = row.CacheWriteTokens + cell.CacheWriteTokens,
        CacheReadTokens = row.CacheReadTokens + cell.CacheReadTokens,
        CostUsd = row.CostUsd + cell.CostUsd,
        HasUnpricedModels = row.HasUnpricedModels || cell.HasUnpricedModels,
    };

    private static void Fold(
        Dictionary<string, BreakdownRow> rows, string key, string display, LocalDayTotals cell)
    {
        if (!rows.TryGetValue(key, out var row))
            row = EmptyRow(key, display);
        rows[key] = Add(row, cell);
    }

    // Same fold, one axis lower: a (project, model) cell summed across the days
    // in range, keeping the pairing the two axis tables discard (#112).
    private static void FoldPair(
        Dictionary<string, BreakdownPair> pairs,
        string cellKey, string project, string display, string model, LocalDayTotals cell)
    {
        if (!pairs.TryGetValue(cellKey, out var pair))
            pair = new BreakdownPair(project, display, model, new LocalDayTotals());
        pairs[cellKey] = pair with { Totals = Add(pair.Totals, cell) };
    }

    private static IReadOnlyList<BreakdownRow> Sorted(Dictionary<string, BreakdownRow> rows) =>
        rows.Values
            .OrderByDescending(r => r.CostUsd)
            .ThenByDescending(r => r.TotalTokens)
            .ToList();

    // "project|model" → its two halves. The model id can't contain '|', and
    // splitting on the LAST separator would be wrong if a directory name
    // contained one — it can't on Windows, so the first '|' is safe. Cell keys
    // come verbatim from the persisted cache and a malformed one must not be
    // able to throw on the UI thread, so a key with no separator degrades to
    // the unknown project with the whole key as the model.
    private static (string Project, string Model) SplitCellKey(string cellKey)
    {
        var sep = cellKey.IndexOf('|');
        return sep < 0 ? (UnknownProject, cellKey) : (cellKey[..sep], cellKey[(sep + 1)..]);
    }

    // First path segment under the projects root, e.g. "c--Projects-ClaudeMon";
    // files sitting directly in the root map to the unknown bucket.
    private string ProjectKeyFor(string path)
    {
        try
        {
            var relative = Path.GetRelativePath(_projectsDir, path);
            var sep = relative.IndexOfAny(['\\', '/']);
            return sep > 0 ? relative[..sep] : UnknownProject;
        }
        catch (ArgumentException)
        {
            return UnknownProject;
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

        WarehouseRollIn? rollIn;
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

            // Collected before pruning: a day that ages out of the live window
            // on this very pass gets its last chance to be banked.
            rollIn = CollectRollInLocked(now);

            changed |= PruneLocked(now);

            if (changed)
                Save();
        }

        // Deliberately outside the lock — the warehouse touches the disk, and
        // the UI thread takes snapshots under this lock every few seconds.
        RollInToWarehouse(rollIn, now);
    }

    /// <summary>
    /// Hands finalized days to the warehouse and, on success, stops tracking
    /// them as pending. Best-effort by construction: a write that doesn't land
    /// leaves the days pending for the next scan.
    /// </summary>
    private void RollInToWarehouse(WarehouseRollIn? rollIn, DateTimeOffset now)
    {
        if (_warehouse is null || rollIn is null)
            return;

        var banked = _warehouse.UpsertDays(rollIn.Days, rollIn.ProjectPaths);
        if (banked.Count == 0)
            return;

        lock (_lock)
        {
            // Only the days that actually landed: whatever didn't stays pending
            // (and, if it has aged out, stays in _unbankedDays) to be retried.
            var carriedDrained = false;
            foreach (var day in banked)
            {
                _pendingWarehouseDays.Remove(day);
                carriedDrained |= _unbankedDays.Remove(day);
            }

            // The cache was written before the roll-in, so a carried day it
            // still lists has just become stale. Persist now, or every restart
            // re-banks a set that is already safely in the warehouse.
            if (carriedDrained)
                Save();
        }

        // Only after a successful write, and only on the rare pass that banked
        // something — pruning enumerates the warehouse directory.
        _warehouse.Prune(now);
    }

    // Caller holds _lock. The finalized (before today, local) pending days and
    // the project paths that go with them, or null when there's nothing to bank.
    // Today is excluded on purpose: its cells are still moving, and banking a
    // partial day would have the warehouse briefly disagree with the live view.
    private WarehouseRollIn? CollectRollInLocked(DateTimeOffset now)
    {
        if (_warehouse is null || _pendingWarehouseDays.Count == 0)
            return null;

        var todayKey = DayKey(now);
        var days = new Dictionary<string, Dictionary<string, LocalDayTotals>>(StringComparer.Ordinal);
        foreach (var day in _pendingWarehouseDays)
        {
            if (string.CompareOrdinal(day, todayKey) >= 0)
                continue;

            // Live cells first; for a day that has already aged out of the
            // window, the carried copy is all that is left of it.
            if (_cells.TryGetValue(day, out var cells) || _unbankedDays.TryGetValue(day, out cells))
                days[day] = new Dictionary<string, LocalDayTotals>(cells, StringComparer.Ordinal);
        }

        return days.Count == 0
            ? null
            : new WarehouseRollIn(days, new Dictionary<string, string>(_projectPaths, StringComparer.OrdinalIgnoreCase));
    }

    // One roll-in's payload: whole-day cell snapshots (the warehouse replaces
    // rather than accumulates, so re-banking an amended day converges).
    private sealed record WarehouseRollIn(
        Dictionary<string, Dictionary<string, LocalDayTotals>> Days,
        Dictionary<string, string> ProjectPaths);

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

        var project = ProjectKeyFor(path);
        lock (_lock)
        {
            foreach (var entry in entries)
                Ingest(entry, project, now);

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

    // Caller holds _lock. Dedupes, prices, and folds one entry into its
    // (day, project, model) cell. Entries outside the retention window are
    // ignored entirely.
    private void Ingest(LocalUsageEntry entry, string project, DateTimeOffset now)
    {
        if (entry.Timestamp < now - AggregateRetention)
            return;

        if (entry.DedupeKey is not null && !_recentKeys.TryAdd(entry.DedupeKey, entry.Timestamp))
            return;

        var pricing = _pricing.Resolve(entry.Model);
        var cost = pricing?.CostUsd(entry) ?? 0.0;
        if (pricing is null && _loggedUnknownModels.Add(entry.Model))
            _logger?.Info($"Local usage: no pricing for model '{entry.Model}' — tokens counted, cost shown as unavailable.");

        if (entry.Cwd is { Length: > 0 } cwd)
            _projectPaths.TryAdd(project, cwd);

        var dayKey = DayKey(entry.Timestamp);
        if (!_cells.TryGetValue(dayKey, out var dayCells))
            _cells[dayKey] = dayCells = new Dictionary<string, LocalDayTotals>(StringComparer.Ordinal);

        // Normalized model in the key so dated/decorated variants merge into
        // one breakdown row. '|' can't appear in a directory name or model id.
        var cellKey = $"{project}|{PricingTable.Normalize(entry.Model)}";
        dayCells.TryGetValue(cellKey, out var cell);
        cell ??= new LocalDayTotals();
        dayCells[cellKey] = cell with
        {
            InputTokens = cell.InputTokens + entry.InputTokens,
            OutputTokens = cell.OutputTokens + entry.OutputTokens,
            CacheWriteTokens = cell.CacheWriteTokens + entry.CacheWrite5mTokens + entry.CacheWrite1hTokens,
            CacheReadTokens = cell.CacheReadTokens + entry.CacheReadTokens,
            CostUsd = cell.CostUsd + cost,
            HasUnpricedModels = cell.HasUnpricedModels || pricing is null,
        };
        _pendingWarehouseDays.Add(dayKey);

        if (entry.Timestamp >= now - RecentCostRetention)
            _recentCosts.Add(new RecentCostSample(
                entry.Timestamp, cost, entry.TotalTokens, entry.CacheReadTokens));
    }

    // Caller holds _lock. Drops everything outside its retention window;
    // returns true when anything was removed.
    private bool PruneLocked(DateTimeOffset now)
    {
        var changed = false;

        var minDayKey = DayKey(now - AggregateRetention);
        var oldDays = _cells.Keys
            .Where(k => string.CompareOrdinal(k, minDayKey) < 0)
            .ToList();
        foreach (var key in oldDays)
        {
            // A pending day that ages out has run out of chances to be re-read:
            // its transcripts are past the horizon, so Ingest would drop them.
            // Carry the cells rather than the bare key, or a roll-in that fails
            // on this very pass would destroy the day it was meant to preserve.
            // Only worth carrying when there is somewhere to carry it to —
            // without a warehouse nothing would ever drain the set.
            if (_warehouse is not null
                && _pendingWarehouseDays.Contains(key)
                && _cells.TryGetValue(key, out var cells))
                _unbankedDays[key] = new Dictionary<string, LocalDayTotals>(cells, StringComparer.Ordinal);

            _cells.Remove(key);
            changed = true;
        }

        // A warehouse that has been unwritable for months must not grow the
        // cache without bound. Oldest first — they are the least likely to ever
        // be wanted, and dropping any of them is worth a line in the log.
        while (_unbankedDays.Count > MaxUnbankedDays)
        {
            var oldest = _unbankedDays.Keys.Min(StringComparer.Ordinal)!;
            _unbankedDays.Remove(oldest);
            _pendingWarehouseDays.Remove(oldest);
            _logger?.Warn(
                $"Local usage: dropping unbanked day {oldest} — more than {MaxUnbankedDays} days are waiting on a usage warehouse that won't accept them.");
            changed = true;
        }

        // Anything still pending with no cells anywhere can never be banked.
        _pendingWarehouseDays.RemoveWhere(d => !_cells.ContainsKey(d) && !_unbankedDays.ContainsKey(d));

        // Drop learned paths for projects no longer present in any cell, so
        // the map can't grow forever across long-dead projects. Paths only die
        // when a day's cells are pruned, so this runs at most once a day.
        if (oldDays.Count > 0)
        {
            var liveProjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var dayCells in _cells.Values.Concat(_unbankedDays.Values))
                foreach (var cellKey in dayCells.Keys)
                    // Carried days count as live: their path has to outlive the
                    // prune too, or a day banked on a later retry would land in
                    // the warehouse under its raw directory name, permanently.
                    liveProjects.Add(SplitCellKey(cellKey).Project);

            var deadPaths = _projectPaths.Keys.Where(p => !liveProjects.Contains(p)).ToList();
            foreach (var project in deadPaths)
            {
                _projectPaths.Remove(project);
                changed = true;
            }
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
                Version = LocalUsageCacheFile.CurrentVersion,
                Files = new Dictionary<string, FileScanState>(_files),
                Cells = _cells.ToDictionary(
                    kv => kv.Key,
                    kv => new Dictionary<string, LocalDayTotals>(kv.Value, StringComparer.Ordinal)),
                ProjectPaths = new Dictionary<string, string>(_projectPaths),
                RecentDedupeKeys = new Dictionary<string, DateTimeOffset>(_recentKeys),
                RecentCosts = new List<RecentCostSample>(_recentCosts),
                UnbankedDays = _unbankedDays.ToDictionary(
                    kv => kv.Key,
                    kv => new Dictionary<string, LocalDayTotals>(kv.Value, StringComparer.Ordinal)),
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

    private static string DayKeyOf(DateOnly date) =>
        date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

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
