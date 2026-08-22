namespace ClaudeMon.Tests;

using ClaudeMon.Models;
using ClaudeMon.Monitoring;

public class CapacityReadoutTextTests
{
    private static ImpliedCapacity Estimate(
        string kind, double capacity, CapacityConfidence confidence,
        string? scopeModel = null, string? equivalentModel = null) =>
        new(kind, scopeModel, capacity, equivalentModel, confidence, 12, 0, null, null);

    private static UsageResponse UsageWith(params UsageLimit[] limits) => new(null, null, limits);

    private static UsageLimit Limit(string kind, double pct, string? model = null) =>
        new(kind, null, pct, null, null, null,
            model is null ? null : new LimitScope(new LimitScopeModel(model)));

    [Fact]
    public void Compose_ConfidentEstimateWithALivePercent_MakesALine()
    {
        var lines = CapacityReadoutText.Compose(
            [Estimate("session", 61_000_000, CapacityConfidence.Medium)],
            UsageWith(Limit("session", 13.3)));

        Assert.Equal("5-hour: ≈8.1M of ≈61M tokens (est.)", Assert.Single(lines));
    }

    [Fact]
    public void Compose_EquivalentModelNamesTheUnit()
    {
        var lines = CapacityReadoutText.Compose(
            [Estimate("weekly_all", 310_000_000, CapacityConfidence.High, equivalentModel: "claude-opus-4-6")],
            UsageWith(Limit("weekly_all", 12.3)));

        Assert.Equal("7-day: ≈38.1M of ≈310M claude-opus-4-6 tokens (est.)", Assert.Single(lines));
    }

    [Fact]
    public void Compose_LowOrNoConfidence_IsHidden()
    {
        var usage = UsageWith(Limit("session", 50));

        Assert.Empty(CapacityReadoutText.Compose(
            [Estimate("session", 1_000_000, CapacityConfidence.Low)], usage));
        Assert.Empty(CapacityReadoutText.Compose(
            [Estimate("session", 1_000_000, CapacityConfidence.None)], usage));
    }

    [Fact]
    public void Compose_NoLivePercentForTheKind_IsHidden()
    {
        // A confident weekly estimate, but the payload only carries the session limit:
        // there is no official % for the line to sit alongside.
        Assert.Empty(CapacityReadoutText.Compose(
            [Estimate("weekly_all", 1_000_000, CapacityConfidence.High)],
            UsageWith(Limit("session", 50))));
    }

    [Fact]
    public void Compose_LegacyPayload_MapsTheCanonicalKinds()
    {
        var legacy = new UsageResponse(new UsageBucket(50, null), new UsageBucket(10, null));

        var lines = CapacityReadoutText.Compose(
            [
                Estimate("session", 2_000_000, CapacityConfidence.Medium),
                Estimate("weekly_all", 10_000_000, CapacityConfidence.Medium),
            ],
            legacy);

        Assert.Equal(2, lines.Count);
        Assert.Equal("5-hour: ≈1M of ≈2M tokens (est.)", lines[0]);
        Assert.Equal("7-day: ≈1M of ≈10M tokens (est.)", lines[1]);
    }

    [Fact]
    public void Compose_ScopedLine_OnlyForTheTightestScopedLimit()
    {
        var usage = UsageWith(
            Limit("weekly_scoped", 30, "Fable"),
            Limit("weekly_scoped", 70, "Opus 4"));

        var lines = CapacityReadoutText.Compose(
            [
                Estimate("weekly_scoped", 50_000_000, CapacityConfidence.Medium, scopeModel: "Opus 4"),
                Estimate("weekly_scoped", 90_000_000, CapacityConfidence.Medium, scopeModel: "Fable"),
            ],
            usage);

        // Opus 4 is the tighter cap (70%) — only it gets a line, even though both qualify.
        Assert.Equal("Weekly (Opus 4): ≈35M of ≈50M tokens (est.)", Assert.Single(lines));
    }

    [Fact]
    public void Compose_NothingToSay_IsEmpty()
    {
        Assert.Empty(CapacityReadoutText.Compose(null, UsageWith(Limit("session", 50))));
        Assert.Empty(CapacityReadoutText.Compose([], UsageWith(Limit("session", 50))));
        Assert.Empty(CapacityReadoutText.Compose(
            [Estimate("session", 1_000_000, CapacityConfidence.High)], usage: null));
    }
}
