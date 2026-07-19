namespace ClaudeMon.Tests;

using System.Text.Json;
using ClaudeMon.Models;

/// <summary>
/// Deserialization of the usage API's <c>limits[]</c> array (issue #67) — including the
/// tolerance contract: unknown kinds/severities and null scopes must parse, and a response
/// without <c>limits[]</c> must behave exactly as before.
/// </summary>
public class UsageLimitsParsingTests
{
    private static UsageResponse Parse(string json) =>
        JsonSerializer.Deserialize<UsageResponse>(json)!;

    [Fact]
    public void Parse_FullLimitsArray_PopulatesEveryField()
    {
        var response = Parse("""
        {
            "five_hour": {"utilization": 23.4, "resets_at": "2026-07-18T18:00:00Z"},
            "seven_day": {"utilization": 4.0, "resets_at": "2026-07-23T00:00:00Z"},
            "limits": [
                {
                    "kind": "session",
                    "group": "session",
                    "percent": 23.4,
                    "severity": "normal",
                    "resets_at": "2026-07-18T18:00:00Z",
                    "is_active": true
                },
                {
                    "kind": "weekly_all",
                    "group": "weekly",
                    "percent": 4.0,
                    "severity": "normal",
                    "resets_at": "2026-07-23T00:00:00Z",
                    "is_active": true
                },
                {
                    "kind": "weekly_scoped",
                    "group": "weekly",
                    "percent": 7.0,
                    "severity": "warning",
                    "resets_at": "2026-07-23T00:00:00Z",
                    "is_active": false,
                    "scope": {"model": {"display_name": "Fable"}}
                }
            ]
        }
        """);

        Assert.NotNull(response.Limits);
        Assert.Equal(3, response.Limits.Count);

        var scoped = response.Limits[2];
        Assert.Equal("weekly_scoped", scoped.Kind);
        // "weekly" is the group the live API sends; older payloads used "seven_day" and both
        // are still recognized (see LimitDisplay.WeeklyGroups).
        Assert.Equal("weekly", scoped.Group);
        Assert.Equal(7.0, scoped.Percent);
        Assert.Equal("warning", scoped.Severity);
        Assert.Equal(LimitSeverity.Warning, scoped.SeverityLevel);
        Assert.Equal(new DateTimeOffset(2026, 7, 23, 0, 0, 0, TimeSpan.Zero), scoped.ResetsAt);
        Assert.False(scoped.IsActive);
        Assert.Equal("Fable", scoped.Scope?.Model?.DisplayName);

        // The legacy fields still parse alongside limits[].
        Assert.Equal(23.4, response.FiveHour?.UtilizationPct);
        Assert.Equal(4.0, response.SevenDay?.UtilizationPct);
    }

    [Fact]
    public void Parse_UnknownKindAndSeverity_ParsesWithoutThrowing()
    {
        var response = Parse("""
        {
            "limits": [
                {
                    "kind": "seven_day_cowork",
                    "group": "seven_day",
                    "percent": 12.5,
                    "severity": "apocalyptic",
                    "resets_at": "2026-07-23T00:00:00Z"
                }
            ]
        }
        """);

        var limit = Assert.Single(response.Limits!);
        Assert.Equal("seven_day_cowork", limit.Kind);
        Assert.Equal(12.5, limit.Percent);
        Assert.Equal(LimitSeverity.Unknown, limit.SeverityLevel);
        Assert.Null(limit.IsActive);
        Assert.Null(limit.Scope);
    }

    [Fact]
    public void Parse_NullAndMissingScope_BothYieldNullScope()
    {
        var response = Parse("""
        {
            "limits": [
                {"kind": "weekly_scoped", "group": "seven_day", "percent": 5.0, "severity": "normal", "resets_at": null, "scope": null},
                {"kind": "weekly_scoped", "group": "seven_day", "percent": 6.0, "severity": "normal", "resets_at": "2026-07-23T00:00:00Z"}
            ]
        }
        """);

        Assert.Equal(2, response.Limits!.Count);
        Assert.Null(response.Limits[0].Scope);
        Assert.Null(response.Limits[0].ResetsAt);
        Assert.Null(response.Limits[1].Scope);
    }

    [Fact]
    public void Parse_NullPercent_ParsesAndRendersAsZero()
    {
        // One malformed limits entry must never blank the whole poll: percent is nullable
        // and displays as 0 rather than throwing during deserialization.
        var response = Parse("""
        {
            "five_hour": {"utilization": 23.4, "resets_at": "2026-07-18T18:00:00Z"},
            "limits": [
                {"kind": "weekly_scoped", "group": "seven_day", "percent": null, "severity": "normal", "resets_at": "2026-07-23T00:00:00Z"}
            ]
        }
        """);

        var limit = Assert.Single(response.Limits!);
        Assert.Null(limit.Percent);
        Assert.Equal(0, limit.ToBucket().UtilizationPct);
        Assert.Equal(23.4, response.FiveHour?.UtilizationPct);
    }

    [Fact]
    public void Parse_NoLimitsArray_LegacyFieldsExactlyAsBefore()
    {
        var response = Parse("""
        {
            "five_hour": {"utilization": 23.4, "resets_at": "2026-05-22T18:00:00Z"},
            "seven_day": {"utilization": 45.2, "resets_at": "2026-05-25T00:00:00Z"}
        }
        """);

        Assert.Null(response.Limits);
        Assert.Equal(23.4, response.FiveHour?.UtilizationPct);
        Assert.Equal(45.2, response.SevenDay?.UtilizationPct);
    }

    [Theory]
    [InlineData("normal", LimitSeverity.Normal)]
    [InlineData("Normal", LimitSeverity.Normal)]
    [InlineData("warning", LimitSeverity.Warning)]
    [InlineData("CRITICAL", LimitSeverity.Critical)]
    [InlineData("elevated", LimitSeverity.Unknown)]
    [InlineData(null, LimitSeverity.Normal)]
    public void SeverityLevel_ParsesCaseInsensitively(string? severity, LimitSeverity expected)
    {
        var limit = new UsageLimit("session", "session", 10, severity, null);
        Assert.Equal(expected, limit.SeverityLevel);
    }

    [Fact]
    public void ToBucket_CarriesPercentAndReset()
    {
        var resetsAt = DateTimeOffset.UtcNow.AddDays(2);
        var limit = new UsageLimit("weekly_scoped", "seven_day", 84.0, "critical", resetsAt);

        var bucket = limit.ToBucket();

        Assert.Equal(84.0, bucket.UtilizationPct);
        Assert.Equal(resetsAt, bucket.ResetAt);
    }
}
