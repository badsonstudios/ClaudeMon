namespace ClaudeMon.Tests;

using ClaudeMon.Models;
using ClaudeMon.Monitoring;

public class LocalCostTextTests
{
    private static LocalUsageSnapshot Snapshot(
        double cost = 4.2,
        bool unpriced = false,
        long tokens = 1_812_345,
        double? burnRate = 1.1) =>
        new(new DateOnly(2026, 7, 19), cost, unpriced, tokens,
            CacheWriteTokens: 0, CacheReadTokens: 0, BurnRateUsdPerHour: burnRate);

    [Fact]
    public void Compose_FullSnapshot_AllThreeSegments()
    {
        Assert.Equal(
            "Today: ~$4.20 · 1.8M tokens · ~$1.10/hr (est.)",
            LocalCostText.Compose(Snapshot()));
    }

    [Fact]
    public void Compose_NoBurnRate_OmitsRateSegment()
    {
        Assert.Equal(
            "Today: ~$4.20 · 1.8M tokens (est.)",
            LocalCostText.Compose(Snapshot(burnRate: null)));
    }

    [Fact]
    public void Compose_UnpricedOnly_ShowsDashForCost()
    {
        Assert.Equal(
            "Today: — · 1.8M tokens (est.)",
            LocalCostText.Compose(Snapshot(cost: 0, unpriced: true, burnRate: null)));
    }

    [Fact]
    public void Compose_PartiallyUnpriced_ShowsKnownCostAsFloor()
    {
        // Some tokens priced, some not: the known portion shows, marked as a
        // floor rather than an estimate.
        Assert.Equal(
            "Today: ≥$4.20 · 1.8M tokens · ~$1.10/hr (est.)",
            LocalCostText.Compose(Snapshot(unpriced: true)));
    }

    [Fact]
    public void Compose_NullSnapshot_ReturnsNull()
    {
        Assert.Null(LocalCostText.Compose(null));
    }

    [Fact]
    public void Compose_ZeroTokens_ReturnsNull()
    {
        Assert.Null(LocalCostText.Compose(Snapshot(cost: 0, tokens: 0, burnRate: null)));
    }

    [Theory]
    [InlineData(950, "950")]
    [InlineData(1_000, "1K")]
    [InlineData(18_500, "18.5K")]
    [InlineData(950_000, "950K")]
    [InlineData(1_000_000, "1M")]
    [InlineData(1_812_345, "1.8M")]
    [InlineData(123_456_789, "123.5M")]
    public void FormatTokens_Thresholds(long tokens, string expected)
    {
        Assert.Equal(expected, LocalCostText.FormatTokens(tokens));
    }

    [Theory]
    [InlineData(0.004, "<$0.01")]
    [InlineData(0.01, "~$0.01")]
    [InlineData(4.2, "~$4.20")]
    [InlineData(12.4, "~$12.40")]
    [InlineData(123.4, "~$123")]
    public void FormatCost_Thresholds(double usd, string expected)
    {
        Assert.Equal(expected, LocalCostText.FormatCost(usd));
    }
}
