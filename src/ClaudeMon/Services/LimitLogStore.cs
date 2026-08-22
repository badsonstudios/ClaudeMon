namespace ClaudeMon.Services;

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClaudeMon.Models;

/// <summary>
/// I/O for the correlated limit log (issue #184), under
/// %LocalAppData%\ClaudeMon\limit-log\: per-month append-only JSONL files
/// (samples-YYYY-MM.jsonl, windows-YYYY-MM.jsonl) that are never pruned — forever retention is
/// the point — plus the small state.json the tracker round-trips between polls. Each append is
/// a single write of one line ending in '\n', so a crash leaves at most one torn final line,
/// which readers skip by the per-line schema version; the app itself never reads the JSONL
/// back, so a torn line can't poison tracking. All I/O is best-effort: recording must never
/// disrupt the poll (the same contract as <see cref="UsageHistoryStore"/>).
/// </summary>
public sealed class LimitLogStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        // Null fields (no scope model, no incomplete reason, unset plan) drop off the line.
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _dir;

    public LimitLogStore(string? dir = null)
    {
        _dir = dir ?? GetDefaultDir();
    }

    /// <summary>Appends one poll's sample to its UTC month's file.</summary>
    public void AppendSample(LimitLogSample sample) =>
        AppendLine(MonthFile("samples", sample.Timestamp), sample);

    /// <summary>Appends one finalized window to the UTC month file of the window's end.</summary>
    public void AppendWindow(LimitWindowRecord record) =>
        AppendLine(MonthFile("windows", record.End), record);

    /// <summary>
    /// Loads the tracker state, or null when it's missing, unreadable, or from another schema
    /// version — the tracker then starts fresh and flags its next windows incomplete rather
    /// than trusting state it can't vouch for.
    /// </summary>
    public LimitLogState? LoadState()
    {
        try
        {
            var path = StatePath;
            if (!File.Exists(path))
                return null;

            var state = JsonSerializer.Deserialize<LimitLogState>(File.ReadAllText(path), JsonOptions);
            return state?.Version == LimitLogState.CurrentVersion ? WithFoldedModelKeys(state) : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Persists the tracker state (temp file + atomic move, like the other stores).</summary>
    public void SaveState(LimitLogState state)
    {
        try
        {
            EnsureDir();
            var path = StatePath;
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(
                state with { Version = LimitLogState.CurrentVersion }, JsonOptions));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or NotSupportedException or JsonException)
        {
            // Best-effort: losing state costs only active-window continuity, never the poll.
        }
    }

    /// <summary>
    /// Loads the capacity estimator's state (issue #185), or null when missing, unreadable, or
    /// from another schema version — the estimator then rebuilds from the samples themselves
    /// (<see cref="ReadSamples"/>): this file is a cache of sufficient statistics, the
    /// forever-log is the source of truth.
    /// </summary>
    public CapacityEstimateState? LoadCapacityState()
    {
        try
        {
            var path = CapacityPath;
            if (!File.Exists(path))
                return null;

            var state = JsonSerializer.Deserialize<CapacityEstimateState>(File.ReadAllText(path), JsonOptions);
            return state?.Version == CapacityEstimateState.CurrentVersion
                ? WithFoldedCapacityModelKeys(state)
                : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Persists the estimator state (temp file + atomic move, like the other stores).</summary>
    public void SaveCapacityState(CapacityEstimateState state)
    {
        try
        {
            EnsureDir();
            var path = CapacityPath;
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(
                state with { Version = CapacityEstimateState.CurrentVersion }, JsonOptions));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or NotSupportedException or JsonException)
        {
            // Best-effort: losing this state costs only a rebuild from the log, never the poll.
        }
    }

    /// <summary>
    /// Streams the recorded samples from <paramref name="from"/> onward, oldest file first —
    /// the estimator's one-time cold-start backfill. Only month files intersecting the range
    /// are opened, each line parses independently, and torn or foreign-version lines are
    /// skipped: exactly the reader contract the schema promises. Never used on the poll path.
    /// </summary>
    public IEnumerable<LimitLogSample> ReadSamples(DateTimeOffset from, DateTimeOffset until)
    {
        if (!Directory.Exists(_dir))
            yield break;

        for (var month = new DateTime(from.UtcDateTime.Year, from.UtcDateTime.Month, 1);
             month <= until.UtcDateTime;
             month = month.AddMonths(1))
        {
            var path = MonthFile("samples", new DateTimeOffset(month, TimeSpan.Zero));
            if (!File.Exists(path))
                continue;

            IEnumerator<string>? lines = null;
            try
            {
                lines = File.ReadLines(path).GetEnumerator();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            using (lines)
            {
                while (true)
                {
                    LimitLogSample? sample;
                    try
                    {
                        if (!lines.MoveNext())
                            break;

                        sample = JsonSerializer.Deserialize<LimitLogSample>(lines.Current, JsonOptions);
                    }
                    catch (JsonException)
                    {
                        continue; // A torn or malformed line — skip it, keep streaming.
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        break; // The file went away mid-read — take what we got.
                    }

                    if (sample is { Version: LimitLogSchema.SchemaVersion } s
                        && s.Timestamp >= from && s.Timestamp <= until)
                    {
                        yield return s;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Loads the drift detector's state (issue #186), or null when missing, unreadable, or
    /// from another schema version — the detector then re-accumulates quietly (a week or so
    /// of silence, never a wrong alert).
    /// </summary>
    public DriftState? LoadDriftState()
    {
        try
        {
            var path = DriftPath;
            if (!File.Exists(path))
                return null;

            var state = JsonSerializer.Deserialize<DriftState>(File.ReadAllText(path), JsonOptions);
            return state?.Version == DriftState.CurrentVersion ? state : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Persists the drift state (temp file + atomic move, like the other state files).</summary>
    public void SaveDriftState(DriftState state)
    {
        try
        {
            EnsureDir();
            var path = DriftPath;
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(
                state with { Version = DriftState.CurrentVersion }, JsonOptions));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or NotSupportedException or JsonException)
        {
            // Best-effort: losing drift state costs a quiet week, never the poll.
        }
    }

    /// <summary>
    /// Streams recorded window rollups from <paramref name="from"/> onward, oldest file first —
    /// the Limit history tab's page loader. Same reader contract as <see cref="ReadSamples"/>:
    /// month files intersecting the range only, per-line parsing, torn and foreign-version
    /// lines skipped. Callers dedupe on (kind, model, end) per the schema's at-least-once note.
    /// </summary>
    public IEnumerable<LimitWindowRecord> ReadWindows(DateTimeOffset from, DateTimeOffset until)
    {
        if (!Directory.Exists(_dir))
            yield break;

        for (var month = new DateTime(from.UtcDateTime.Year, from.UtcDateTime.Month, 1);
             month <= until.UtcDateTime;
             month = month.AddMonths(1))
        {
            var path = MonthFile("windows", new DateTimeOffset(month, TimeSpan.Zero));
            if (!File.Exists(path))
                continue;

            IEnumerator<string>? lines = null;
            try
            {
                lines = File.ReadLines(path).GetEnumerator();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            using (lines)
            {
                while (true)
                {
                    LimitWindowRecord? record;
                    try
                    {
                        if (!lines.MoveNext())
                            break;

                        record = JsonSerializer.Deserialize<LimitWindowRecord>(lines.Current, JsonOptions);
                    }
                    catch (JsonException)
                    {
                        continue;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        break;
                    }

                    if (record is { Version: LimitLogSchema.SchemaVersion } r
                        && r.End >= from && r.End <= until)
                    {
                        yield return r;
                    }
                }
            }
        }
    }

    /// <summary>
    /// The month (first day, UTC) of the oldest windows-*.jsonl file on disk, or null when
    /// none exist — how the history tab's "Load older" knows when to stop, without opening
    /// a single file.
    /// </summary>
    public DateTime? OldestWindowMonth()
    {
        try
        {
            if (!Directory.Exists(_dir))
                return null;

            DateTime? oldest = null;
            foreach (var path in Directory.EnumerateFiles(_dir, "windows-*.jsonl"))
            {
                var name = Path.GetFileNameWithoutExtension(path);
                if (DateTime.TryParseExact(
                        name["windows-".Length..], "yyyy-MM", CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var month)
                    && (oldest is null || month < oldest))
                {
                    oldest = month;
                }
            }

            return oldest;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private string DriftPath => Path.Combine(_dir, "drift.json");

    // Same comparer fold as LoadState, for the estimator's dictionaries — plus dropping any
    // ring entry whose delta is below the engine's closing threshold: the engine can never
    // write one, but this file tolerates hand-editing, and a dp of 0 would divide out to an
    // infinite capacity that sails through every confidence gate into the UI.
    private static CapacityEstimateState WithFoldedCapacityModelKeys(CapacityEstimateState state) => state with
    {
        LastTokens = FoldModelKeys(state.LastTokens),
        Limits = state.Limits
            .Select(l =>
            {
                var folded = l with
                {
                    Ring = l.Ring
                        .Where(o => o.DeltaPercent >= Monitoring.CapacityEstimator.MinDeltaPct)
                        .ToList(),
                };
                return folded.Accumulator is { } acc
                    ? folded with { Accumulator = acc with { Tokens = FoldModelKeys(acc.Tokens)! } }
                    : folded;
            })
            .ToList(),
    };

    // Deserialization loses the tracker's case-insensitive comparer, and a case miss on a
    // model key would silently rebase that model's delta to zero — attributing its whole
    // 30-day cumulative total as fresh burn to every open window, in a log built for later
    // analysis. Rebuild every token dictionary under OrdinalIgnoreCase, folding any
    // case-duplicate keys a hand-edited file might hold instead of throwing.
    private static LimitLogState WithFoldedModelKeys(LimitLogState state) => state with
    {
        LastTokens = FoldModelKeys(state.LastTokens),
        Windows = state.Windows
            .Select(w => w with { TokensByModel = FoldModelKeys(w.TokensByModel)! })
            .ToList(),
    };

    private static Dictionary<string, ModelTokens>? FoldModelKeys(Dictionary<string, ModelTokens>? tokens)
    {
        if (tokens is null)
            return null;

        var folded = new Dictionary<string, ModelTokens>(tokens.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var (model, t) in tokens)
            folded[model] = folded.TryGetValue(model, out var existing) ? existing.Plus(t) : t;
        return folded;
    }

    private void AppendLine<T>(string path, T line)
    {
        try
        {
            EnsureDir();
            var bytes = System.Text.Encoding.UTF8.GetBytes(
                JsonSerializer.Serialize(line, JsonOptions) + "\n");

            using var fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);

            // Heal a torn tail first: a crash mid-append can leave the file ending without a
            // newline, and appending straight onto it would weld the new line to the torn one —
            // losing a good record, not just the torn fragment. Terminating the tail isolates
            // the damage to the one line readers were already going to skip.
            if (fs.Length > 0)
            {
                fs.Seek(-1, SeekOrigin.End);
                if (fs.ReadByte() != '\n')
                    fs.WriteByte((byte)'\n');
            }

            fs.Write(bytes, 0, bytes.Length);
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or NotSupportedException or JsonException)
        {
            // Best-effort: a locked or unwritable log must never disrupt the poll.
        }
    }

    private void EnsureDir()
    {
        if (!Directory.Exists(_dir))
            Directory.CreateDirectory(_dir);
    }

    private string StatePath => Path.Combine(_dir, "state.json");

    private string CapacityPath => Path.Combine(_dir, "capacity.json");

    private string MonthFile(string prefix, DateTimeOffset timestamp) =>
        Path.Combine(
            _dir,
            $"{prefix}-{timestamp.UtcDateTime.ToString("yyyy-MM", CultureInfo.InvariantCulture)}.jsonl");

    private static string GetDefaultDir() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ClaudeMon",
            "limit-log");
}
