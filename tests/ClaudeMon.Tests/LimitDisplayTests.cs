namespace ClaudeMon.Tests;

using ClaudeMon.Models;
using ClaudeMon.Monitoring;

public class LimitDisplayTests
{
    private static readonly DateTimeOffset Reset = DateTimeOffset.UtcNow.AddDays(2);

    private static UsageLimit Limit(
        string? kind, double pct, string? group = "seven_day", string? severity = "normal",
        string? displayName = null, bool? isActive = true)
        => new(kind, group, pct, severity, Reset, isActive,
            displayName is null ? null : new LimitScope(new LimitScopeModel(displayName)));

    private static UsageResponse WithLimits(params UsageLimit[] limits)
        => new(new UsageBucket(20, Reset), new UsageBucket(10, Reset), limits);

    // ================================================================
    // Legacy fallback
    // ================================================================

    [Fact]
    public void BuildRows_NoLimitsArray_EmitsExactlyTheLegacyRows()
    {
        var usage = new UsageResponse(new UsageBucket(23.4, Reset), new UsageBucket(45.2, Reset));

        var rows = LimitDisplay.BuildRows(usage);

        Assert.Equal(2, rows.Count);
        Assert.Equal(new LimitRow("5-hour", usage.FiveHour!, UsageWindows.FiveHour, 5, LimitSeverity.Normal), rows[0]);
        Assert.Equal(new LimitRow("7-day", usage.SevenDay!, UsageWindows.SevenDay, 7, LimitSeverity.Normal), rows[1]);
    }

    [Fact]
    public void BuildRows_EmptyLimitsArray_FallsBackToLegacyRows()
    {
        var usage = new UsageResponse(
            new UsageBucket(23.4, Reset), new UsageBucket(45.2, Reset), Array.Empty<UsageLimit>());

        var rows = LimitDisplay.BuildRows(usage);

        Assert.Equal(2, rows.Count);
        Assert.Equal("5-hour", rows[0].Label);
        Assert.Equal("7-day", rows[1].Label);
    }

    [Fact]
    public void BuildRows_LegacyFallback_OmitsMissingBuckets()
    {
        var fiveOnly = LimitDisplay.BuildRows(new UsageResponse(new UsageBucket(10, Reset), null));
        Assert.Equal("5-hour", Assert.Single(fiveOnly).Label);

        var none = LimitDisplay.BuildRows(new UsageResponse(null, null));
        Assert.Empty(none);
    }

    // ================================================================
    // Labeling
    // ================================================================

    [Fact]
    public void BuildRows_KnownKinds_GetFriendlyLabelsAndWindows()
    {
        var rows = LimitDisplay.BuildRows(WithLimits(
            Limit("session", 23, group: "session"),
            Limit("weekly_all", 4),
            Limit("weekly_scoped", 7, displayName: "Fable")));

        Assert.Equal(3, rows.Count);
        Assert.Equal(("5-hour", (TimeSpan?)UsageWindows.FiveHour, 5), (rows[0].Label, rows[0].Window, rows[0].Segments));
        Assert.Equal(("7-day", (TimeSpan?)UsageWindows.SevenDay, 7), (rows[1].Label, rows[1].Window, rows[1].Segments));
        Assert.Equal("Weekly (Fable)", rows[2].Label);
        Assert.Equal(UsageWindows.SevenDay, rows[2].Window);
    }

    [Fact]
    public void BuildRows_ScopedWithoutDisplayName_UsesGenericModelLabel()
    {
        var rows = LimitDisplay.BuildRows(WithLimits(Limit("weekly_scoped", 7)));
        Assert.Equal("Weekly (model)", Assert.Single(rows).Label);
    }

    [Fact]
    public void BuildRows_UnknownKindInSevenDayGroup_RendersGenericSevenDayRow()
    {
        var rows = LimitDisplay.BuildRows(WithLimits(
            Limit("seven_day_cowork", 12, severity: "normal")));

        var row = Assert.Single(rows);
        // Group gives the window; the kind's residual after the group prefix disambiguates.
        Assert.Equal("7-day (Cowork)", row.Label);
        Assert.Equal(UsageWindows.SevenDay, row.Window);
        Assert.Equal(7, row.Segments);
    }

    [Fact]
    public void BuildRows_UnknownKindWithScopeName_PrefersScopeName()
    {
        var rows = LimitDisplay.BuildRows(WithLimits(
            Limit("seven_day_opus", 12, displayName: "Opus")));

        Assert.Equal("7-day (Opus)", Assert.Single(rows).Label);
    }

    [Fact]
    public void BuildRows_UnknownKindAndGroup_RendersWithoutWindowOrCrash()
    {
        var rows = LimitDisplay.BuildRows(WithLimits(
            Limit("monthly_special", 33, group: "monthly")));

        var row = Assert.Single(rows);
        // Group names the row; the kind's residual after the group prefix disambiguates.
        Assert.Equal("Monthly (Special)", row.Label);
        Assert.Null(row.Window);
        Assert.Equal(0, row.Segments);
    }

    [Fact]
    public void BuildRows_NullKindAndGroup_StillRenders()
    {
        var rows = LimitDisplay.BuildRows(WithLimits(
            new UsageLimit(null, null, 5, null, null)));

        var row = Assert.Single(rows);
        Assert.Equal("Limit", row.Label);
        Assert.Null(row.Window);
    }

    // ================================================================
    // Severity passthrough
    // ================================================================

    [Fact]
    public void BuildRows_CarriesSeverityPerRow()
    {
        var rows = LimitDisplay.BuildRows(WithLimits(
            Limit("session", 23, group: "session", severity: "critical"),
            Limit("weekly_scoped", 7, severity: "warning", displayName: "Fable")));

        Assert.Equal(LimitSeverity.Critical, rows[0].Severity);
        Assert.Equal(LimitSeverity.Warning, rows[1].Severity);
    }

    // ================================================================
    // Ordering & de-dup
    // ================================================================

    [Fact]
    public void BuildRows_OrdersSessionThenWeeklyThenScopedByPercentThenUnknown()
    {
        var rows = LimitDisplay.BuildRows(WithLimits(
            Limit("mystery_kind", 99, group: "mystery"),
            Limit("weekly_scoped", 7, displayName: "Fable"),
            Limit("weekly_all", 4),
            Limit("weekly_scoped", 55, displayName: "Opus"),
            Limit("session", 23, group: "session")));

        Assert.Equal(
            new[] { "5-hour", "7-day", "Weekly (Opus)", "Weekly (Fable)", "Mystery (Kind)" },
            rows.Select(r => r.Label).ToArray());
    }

    [Fact]
    public void BuildRows_DuplicateKindAndScope_KeepsHigherPercent()
    {
        var rows = LimitDisplay.BuildRows(WithLimits(
            Limit("weekly_scoped", 7, displayName: "Fable"),
            Limit("weekly_scoped", 9, displayName: "Fable")));

        var row = Assert.Single(rows);
        Assert.Equal(9, row.Bucket.UtilizationPct);
    }

    [Fact]
    public void BuildRows_SameKindDifferentModels_AreDistinctRows()
    {
        var rows = LimitDisplay.BuildRows(WithLimits(
            Limit("weekly_scoped", 7, displayName: "Fable"),
            Limit("weekly_scoped", 5, displayName: "Opus")));

        Assert.Equal(2, rows.Count);
    }

    [Fact]
    public void BuildRows_InactiveLimit_StillRendered()
    {
        // A nearly-exhausted-but-inactive scoped cap is exactly what the user needs to see.
        var rows = LimitDisplay.BuildRows(WithLimits(
            Limit("weekly_scoped", 84, displayName: "Fable", isActive: false)));

        Assert.Single(rows);
    }

    // ================================================================
    // MostConstrainedScopedWeekly
    // ================================================================

    [Fact]
    public void MostConstrainedScopedWeekly_PicksHighestPercent()
    {
        var scoped = LimitDisplay.MostConstrainedScopedWeekly(WithLimits(
            Limit("weekly_scoped", 7, displayName: "Fable"),
            Limit("weekly_scoped", 55, displayName: "Opus"),
            Limit("weekly_all", 90)));

        Assert.Equal("Opus", scoped?.Scope?.Model?.DisplayName);
    }

    [Fact]
    public void MostConstrainedScopedWeekly_NoScopedLimit_ReturnsNull()
    {
        Assert.Null(LimitDisplay.MostConstrainedScopedWeekly(
            WithLimits(Limit("weekly_all", 90))));
        Assert.Null(LimitDisplay.MostConstrainedScopedWeekly(
            new UsageResponse(null, null)));
    }

    [Fact]
    public void BuildRows_UnknownKindInLiveWeeklyGroup_GetsSevenDayVisuals()
    {
        // The live API's weekly group is "weekly"; an unrecognized kind in it should still draw
        // the 7-day window and segments rather than falling back to a windowless generic row.
        var rows = LimitDisplay.BuildRows(WithLimits(Limit("weekly_beta", 60, group: "weekly")));

        var row = Assert.Single(rows);
        Assert.Equal(UsageWindows.SevenDay, row.Window);
        Assert.Equal(7, row.Segments);
        Assert.StartsWith("7-day", row.Label);
    }

    // ================================================================
    // WeeklyAlertTargets (issue #98)
    // ================================================================

    [Fact]
    public void WeeklyAlertTargets_NoLimitsArray_UsesLegacySevenDayOnly()
    {
        var usage = new UsageResponse(new UsageBucket(20, Reset), new UsageBucket(45, Reset));

        var target = Assert.Single(LimitDisplay.WeeklyAlertTargets(usage));

        Assert.Equal("weekly_all", target.Key);
        Assert.Equal("7-day", target.Noun);
        Assert.Equal(45, target.Bucket.UtilizationPct);
    }

    [Fact]
    public void WeeklyAlertTargets_NoWeeklyDataAtAll_IsEmpty()
    {
        Assert.Empty(LimitDisplay.WeeklyAlertTargets(new UsageResponse(new UsageBucket(20, Reset), null)));
    }

    [Fact]
    public void WeeklyAlertTargets_LimitsPresent_IgnoresLegacySevenDaySoOverallIsNotDoubled()
    {
        // The response carries the overall weekly twice — top-level seven_day (10) and
        // weekly_all (77). Exactly one overall target must come out, sourced from limits[].
        var usage = WithLimits(Limit("weekly_all", 77));

        var target = Assert.Single(LimitDisplay.WeeklyAlertTargets(usage));

        Assert.Equal("weekly_all", target.Key);
        Assert.Equal(77, target.Bucket.UtilizationPct);
    }

    [Fact]
    public void WeeklyAlertTargets_ScopedCaps_KeyedByModelWithNamedNoun()
    {
        var targets = LimitDisplay.WeeklyAlertTargets(WithLimits(
            Limit("weekly_all", 40),
            Limit("weekly_scoped", 84, displayName: "Fable"),
            Limit("weekly_scoped", 60, displayName: "Opus")));

        Assert.Equal(3, targets.Count);
        var fable = Assert.Single(targets, t => t.Key == "scoped:fable");
        Assert.Equal("Fable weekly", fable.Noun);
        Assert.Equal(84, fable.Bucket.UtilizationPct);
        Assert.Contains(targets, t => t.Key == "scoped:opus");
    }

    [Fact]
    public void WeeklyAlertTargets_SessionIsExcluded()
    {
        // The 5-hour alerts own the session bucket; including it here would double-drive them.
        var targets = LimitDisplay.WeeklyAlertTargets(WithLimits(
            Limit("session", 95, group: "session"),
            Limit("weekly_all", 40)));

        Assert.Equal(new[] { "weekly_all" }, targets.Select(t => t.Key));
    }

    [Fact]
    public void WeeklyAlertTargets_InactiveCapsIncluded()
    {
        // The live API marks the overall weekly is_active=false while it is plainly in force,
        // so the flag can't gate alerting (BuildRows renders inactive caps for the same reason).
        var targets = LimitDisplay.WeeklyAlertTargets(WithLimits(
            Limit("weekly_all", 40, isActive: false),
            Limit("weekly_scoped", 95, displayName: "Fable", isActive: false)));

        Assert.Equal(2, targets.Count);
    }

    [Theory]
    [InlineData("weekly")]      // what the live API sends
    [InlineData("seven_day")]   // older payloads / parsing fixtures
    public void WeeklyAlertTargets_UnknownKind_RecognizedInEitherWeeklyGroup(string group)
    {
        var target = Assert.Single(LimitDisplay.WeeklyAlertTargets(
            WithLimits(Limit($"{group}_cowork", 60, group: group))));

        Assert.StartsWith($"kind:{group}_cowork", target.Key);
        Assert.Equal("Cowork weekly", target.Noun);
    }

    [Fact]
    public void WeeklyAlertTargets_CarriesApiSeverity()
    {
        var target = Assert.Single(LimitDisplay.WeeklyAlertTargets(WithLimits(
            Limit("weekly_scoped", 60, severity: "critical", displayName: "Fable"))));

        Assert.Equal(LimitSeverity.Critical, target.Severity);
    }

    [Fact]
    public void WeeklyAlertTargets_UnknownWeeklyGroupKind_IsIncluded()
    {
        var target = Assert.Single(LimitDisplay.WeeklyAlertTargets(
            WithLimits(Limit("seven_day_cowork", 60))));

        Assert.StartsWith("kind:seven_day_cowork", target.Key);
        Assert.Equal("Cowork weekly", target.Noun);
    }

    [Fact]
    public void WeeklyAlertTargets_UnknownNonWeeklyKind_IsExcluded()
    {
        // No reliable weekly semantics — the flyout still renders it generically.
        Assert.Empty(LimitDisplay.WeeklyAlertTargets(
            WithLimits(Limit("monthly_thing", 60, group: "monthly"))));
    }

    [Fact]
    public void WeeklyAlertTargets_DuplicateEntries_DedupedKeepingHigher()
    {
        var targets = LimitDisplay.WeeklyAlertTargets(WithLimits(
            Limit("weekly_scoped", 40, displayName: "Fable"),
            Limit("weekly_scoped", 84, displayName: "Fable")));

        var target = Assert.Single(targets);
        Assert.Equal(84, target.Bucket.UtilizationPct);
    }

    [Fact]
    public void WeeklyAlertTargets_ScopedWithoutModelName_FallsBackToGenericIdentity()
    {
        var target = Assert.Single(LimitDisplay.WeeklyAlertTargets(
            WithLimits(Limit("weekly_scoped", 60))));

        // Keyed on the empty name, which no real display name can produce — so a model
        // literally called "Model" can't collide with the unnamed fallback.
        Assert.Equal("scoped:", target.Key);
        Assert.Equal("model weekly", target.Noun);
    }

    [Fact]
    public void WeeklyAlertTargets_ModelNamedModel_DoesNotCollideWithUnnamedFallback()
    {
        var targets = LimitDisplay.WeeklyAlertTargets(WithLimits(
            Limit("weekly_scoped", 60),
            Limit("weekly_scoped", 70, displayName: "Model")));

        Assert.Equal(2, targets.Select(t => t.Key).Distinct().Count());
    }

    [Fact]
    public void WeeklyAlertTargets_UnknownKindsDifferingOnlyByScope_GetDistinctKeys()
    {
        // Dedup keys on (kind, scope), so both survive — they must not then share alert state.
        var targets = LimitDisplay.WeeklyAlertTargets(WithLimits(
            Limit("weekly_beta", 60, group: "weekly", displayName: "Fable"),
            Limit("weekly_beta", 70, group: "weekly", displayName: "Opus")));

        Assert.Equal(2, targets.Count);
        Assert.Equal(2, targets.Select(t => t.Key).Distinct().Count());
    }

    // ================================================================
    // UnknownKinds
    // ================================================================

    [Fact]
    public void UnknownKinds_ReturnsDistinctUnrecognizedKindsOnly()
    {
        var kinds = LimitDisplay.UnknownKinds(WithLimits(
            Limit("session", 1, group: "session"),
            Limit("weekly_all", 2),
            Limit("weekly_scoped", 3, displayName: "Fable"),
            Limit("seven_day_cowork", 4),
            Limit("seven_day_cowork", 5),
            Limit(null, 6))).ToList();

        Assert.Equal(new[] { "seven_day_cowork" }, kinds);
    }
}
