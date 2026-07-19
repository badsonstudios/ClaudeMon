namespace ClaudeMon.Tests;

using ClaudeMon.Models;
using ClaudeMon.Monitoring;

public class TrayTooltipTests
{
    private static readonly DateTimeOffset FiveHourReset = DateTimeOffset.UtcNow.AddHours(2).AddMinutes(30);
    private static readonly DateTimeOffset WeeklyReset = DateTimeOffset.UtcNow.AddDays(2).AddHours(3);

    private static UsageLimit Scoped(double pct, string? displayName)
        => new("weekly_scoped", "seven_day", pct, "normal", WeeklyReset, true,
            new LimitScope(new LimitScopeModel(displayName)));

    private static UsageResponse Usage(params UsageLimit[] limits)
        => new(
            new UsageBucket(23.0, FiveHourReset),
            new UsageBucket(45.0, WeeklyReset),
            limits.Length == 0 ? null : limits);

    [Fact]
    public void Compose_NoScopedWeekly_MatchesLegacyFormatExactly()
    {
        var usage = Usage();
        var expected = string.Join("\n",
            "ClaudeMon",
            $"5hr: 23% ({usage.FiveHour!.FormatResetCountdown()})",
            $"7day: 45% ({usage.SevenDay!.FormatResetCountdown()})");

        Assert.Equal(expected, TrayTooltip.Compose(usage, MonitorStatus.Connected));
    }

    [Fact]
    public void Compose_NonConnectedStatus_AppendsStatusLine()
    {
        var text = TrayTooltip.Compose(Usage(), MonitorStatus.RateLimited);
        Assert.EndsWith("[RateLimited]", text);
    }

    [Fact]
    public void Compose_ScopedWeekly_AppendsCompactLine()
    {
        var text = TrayTooltip.Compose(Usage(Scoped(84, "Fable")), MonitorStatus.Connected);

        var scopedLine = text.Split('\n')[3];
        Assert.StartsWith("Fable wk: 84% (", scopedLine);
        // Countdown is the compact form, without the "resets " prefix.
        Assert.DoesNotContain("resets", scopedLine);
        Assert.Contains("2d", scopedLine);
    }

    [Fact]
    public void Compose_PicksTightestScopedWeekly()
    {
        var text = TrayTooltip.Compose(
            Usage(Scoped(7, "Fable"), Scoped(55, "Opus")), MonitorStatus.Connected);

        Assert.Contains("Opus wk: 55%", text);
        Assert.DoesNotContain("Fable", text);
    }

    [Fact]
    public void Compose_LongModelName_StaysUnderNotifyIconCap()
    {
        var text = TrayTooltip.Compose(
            Usage(Scoped(84, "Extremely Long Model Display Name 3.5 Turbo Ultra")),
            MonitorStatus.Connected);

        Assert.True(text.Length <= 127, $"tooltip is {text.Length} chars");
        // The name is truncated with an ellipsis rather than dropped.
        Assert.Contains("…", text);
        Assert.Contains("wk: 84%", text);
    }

    [Fact]
    public void Compose_ScopedWithoutDisplayName_UsesModelPlaceholder()
    {
        var text = TrayTooltip.Compose(Usage(Scoped(12, null)), MonitorStatus.Connected);
        Assert.Contains("Model wk: 12%", text);
    }

    [Fact]
    public void Compose_NeverExceedsNotifyIconCap()
    {
        // Worst case across the degradation ladder: long names, all lines, error status.
        var usage = Usage(
            Scoped(100, new string('W', 60)),
            Scoped(99, "Second Model"));

        foreach (var status in new[]
        {
            MonitorStatus.Connected, MonitorStatus.RateLimited,
            MonitorStatus.Offline, MonitorStatus.Initializing,
        })
        {
            var text = TrayTooltip.Compose(usage, status);
            Assert.True(text.Length <= 127, $"{status}: tooltip is {text.Length} chars");
        }
    }

    // Expired-and-idle 5-hour window (past resets_at, issue #61): the tooltip shows the
    // distinct idle state instead of a perpetual "resetting...".
    [Fact]
    public void Compose_ExpiredIdleWindow_ShowsIdleState()
    {
        var usage = new UsageResponse(
            new UsageBucket(50.0, DateTimeOffset.UtcNow.AddHours(-3)),
            new UsageBucket(45.0, WeeklyReset));

        var text = TrayTooltip.Compose(usage, MonitorStatus.Connected);

        Assert.Contains("5hr: 50% (resets on next use)", text);
        Assert.DoesNotContain("resetting", text);
    }

    // The scoped line strips the "resets " prefix from countdowns, so the idle text renders
    // as "(on next use)" — pin that coupling to the exact idle string.
    [Fact]
    public void Compose_ExpiredScopedWeekly_StripsPrefixFromIdleState()
    {
        var expired = new UsageLimit(
            "weekly_scoped", "seven_day", 84, "normal",
            DateTimeOffset.UtcNow.AddHours(-3), false,
            new LimitScope(new LimitScopeModel("Fable")));

        var text = TrayTooltip.Compose(Usage(expired), MonitorStatus.Connected);

        Assert.Contains("Fable wk: 84% (on next use)", text);
    }

    [Fact]
    public void Compose_MissingLegacyBuckets_OmitsThoseLines()
    {
        var usage = new UsageResponse(null, null, new[] { Scoped(9, "Fable") });

        var text = TrayTooltip.Compose(usage, MonitorStatus.Connected);

        Assert.DoesNotContain("5hr", text);
        Assert.DoesNotContain("7day", text);
        Assert.Contains("Fable wk: 9%", text);
    }
}
