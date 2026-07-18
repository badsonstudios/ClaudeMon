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
