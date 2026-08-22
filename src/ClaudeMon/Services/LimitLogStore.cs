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
            File.AppendAllText(path, JsonSerializer.Serialize(line, JsonOptions) + "\n");
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
