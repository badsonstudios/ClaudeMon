namespace ClaudeMon.Monitoring;

using ClaudeMon.Models;

/// <summary>
/// One displayable usage row: what the flyout draws a bar for. A null <see cref="Window"/>
/// means the reset-window length is unknown (an unrecognized bucket kind), so pace visuals
/// (segments, time marker) are skipped and the bar colours by absolute level.
/// </summary>
public sealed record LimitRow(
    string Label,
    UsageBucket Bucket,
    TimeSpan? Window,
    int Segments,
    LimitSeverity Severity);

/// <summary>
/// Pure presentation logic for the usage API's <c>limits[]</c> array: turns a
/// <see cref="UsageResponse"/> into the list of rows the flyout and tray tooltip display.
/// Kept free of UI dependencies so the row-building rules are unit-testable.
/// </summary>
public static class LimitDisplay
{
    private const string SessionKind = "session";
    private const string WeeklyAllKind = "weekly_all";
    private const string WeeklyScopedKind = "weekly_scoped";

    private const int FiveHourSegments = 5;
    private const int SevenDaySegments = 7;

    /// <summary>
    /// Builds the display rows for <paramref name="usage"/>. When <c>limits[]</c> is absent
    /// (or empty) this falls back to exactly the legacy 5-hour/7-day pair, so older API
    /// responses render pixel-identically to before. Inactive limits are rendered too: a
    /// nearly-exhausted-but-inactive scoped cap is precisely what the user needs to see.
    /// Real payloads carry a handful of limits; if the API ever returns many, cap the row
    /// count here (tightest-first) rather than letting the flyout outgrow the screen.
    /// </summary>
    public static IReadOnlyList<LimitRow> BuildRows(UsageResponse usage)
    {
        var limits = usage.Limits;
        if (limits is null || limits.Count == 0)
            return BuildLegacyRows(usage);

        // De-dup exact (kind, scope) repeats, keeping the higher percent. Two weekly_scoped
        // entries for *different* models are distinct buckets, not duplicates.
        var deduped = new List<UsageLimit>();
        var indexByKey = new Dictionary<(string Kind, string Scope), int>();
        foreach (var limit in limits)
        {
            var key = (Normalize(limit.Kind), Normalize(limit.Scope?.Model?.DisplayName));
            if (indexByKey.TryGetValue(key, out var existing))
            {
                if ((limit.Percent ?? 0) > (deduped[existing].Percent ?? 0))
                    deduped[existing] = limit;
            }
            else
            {
                indexByKey[key] = deduped.Count;
                deduped.Add(limit);
            }
        }

        // Session first, overall weekly second, scoped weeklies tightest-first, then unknown
        // kinds in API order.
        return deduped
            .Select((limit, index) => (limit, index))
            .OrderBy(x => Rank(x.limit))
            .ThenByDescending(x => Rank(x.limit) == ScopedRank ? x.limit.Percent ?? 0 : 0)
            .ThenBy(x => x.index)
            .Select(x => ToRow(x.limit))
            .ToList();
    }

    /// <summary>
    /// The tightest per-model weekly cap, for the tray tooltip's extra line. Null when the
    /// response carries no scoped weekly limit.
    /// </summary>
    public static UsageLimit? MostConstrainedScopedWeekly(UsageResponse usage)
        => usage.Limits?
            .Where(l => Normalize(l.Kind) == WeeklyScopedKind)
            .OrderByDescending(l => l.Percent ?? 0)
            .FirstOrDefault();

    /// <summary>Distinct limit kinds this code doesn't recognize — the log-once feed.</summary>
    public static IEnumerable<string> UnknownKinds(UsageResponse usage)
        => (usage.Limits ?? Array.Empty<UsageLimit>())
            .Select(l => l.Kind)
            .Where(k => !string.IsNullOrWhiteSpace(k) && Rank(Normalize(k)) == UnknownRank)
            .Select(k => k!)
            .Distinct(StringComparer.OrdinalIgnoreCase);

    private static IReadOnlyList<LimitRow> BuildLegacyRows(UsageResponse usage)
    {
        var rows = new List<LimitRow>(2);
        if (usage.FiveHour is not null)
            rows.Add(new("5-hour", usage.FiveHour, UsageWindows.FiveHour, FiveHourSegments, LimitSeverity.Normal));
        if (usage.SevenDay is not null)
            rows.Add(new("7-day", usage.SevenDay, UsageWindows.SevenDay, SevenDaySegments, LimitSeverity.Normal));
        return rows;
    }

    private const int ScopedRank = 2;
    private const int UnknownRank = 3;

    private static int Rank(UsageLimit limit) => Rank(Normalize(limit.Kind));

    private static int Rank(string kind) => kind switch
    {
        SessionKind => 0,
        WeeklyAllKind => 1,
        WeeklyScopedKind => ScopedRank,
        _ => UnknownRank,
    };

    private static LimitRow ToRow(UsageLimit limit)
    {
        var severity = limit.SeverityLevel;
        var scopeName = limit.Scope?.Model?.DisplayName;

        return Normalize(limit.Kind) switch
        {
            SessionKind => new("5-hour", limit.ToBucket(), UsageWindows.FiveHour, FiveHourSegments, severity),
            WeeklyAllKind => new("7-day", limit.ToBucket(), UsageWindows.SevenDay, SevenDaySegments, severity),
            WeeklyScopedKind => new(
                $"Weekly ({(string.IsNullOrWhiteSpace(scopeName) ? "model" : scopeName)})",
                limit.ToBucket(), UsageWindows.SevenDay, SevenDaySegments, severity),
            _ => GenericRow(limit, severity, scopeName),
        };
    }

    /// <summary>
    /// A future-proof row for a kind this code doesn't recognize: window and label derive from
    /// the limit's <c>group</c> (so e.g. a new <c>seven_day_*</c> bucket still gets 7-day pace
    /// visuals), with the scope's display name — or the kind's residual after the group prefix —
    /// distinguishing it from siblings.
    /// </summary>
    private static LimitRow GenericRow(UsageLimit limit, LimitSeverity severity, string? scopeName)
    {
        var group = Normalize(limit.Group);
        var kind = Normalize(limit.Kind);

        var (label, window, segments) = group switch
        {
            "seven_day" or WeeklyAllKind => ("7-day", (TimeSpan?)UsageWindows.SevenDay, SevenDaySegments),
            SessionKind or "five_hour" => ("5-hour", UsageWindows.FiveHour, FiveHourSegments),
            _ => (Humanize(group) ?? Humanize(kind) ?? "Limit", null, 0),
        };

        var qualifier = !string.IsNullOrWhiteSpace(scopeName)
            ? scopeName
            : group.Length > 0 && kind.StartsWith(group + "_", StringComparison.Ordinal)
                ? Humanize(kind[(group.Length + 1)..])
                : null;

        return new(
            qualifier is null ? label : $"{label} ({qualifier})",
            limit.ToBucket(), window, segments, severity);
    }

    private static string Normalize(string? value) => value?.Trim().ToLowerInvariant() ?? "";

    // "seven_day_cowork" residue → "Cowork": readable next to the hand-written labels.
    private static string? Humanize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var text = value.Replace('_', ' ');
        return char.ToUpperInvariant(text[0]) + text[1..];
    }
}
