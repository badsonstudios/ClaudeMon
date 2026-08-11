namespace ClaudeMon.Tests;

using ClaudeMon.Models;
using ClaudeMon.Services;

public class PricingTableTests
{
    private static PricingTable Table() => new(new Dictionary<string, ModelPricing>
    {
        ["claude-fable-5"] = new(10.0, 50.0, 12.5, 20.0, 1.0),
        ["claude-opus-4-8"] = new(5.0, 25.0, 6.25, 10.0, 0.5),
        ["claude-sonnet-4"] = new(3.0, 15.0, 3.75, 6.0, 0.3),
        ["claude-sonnet-4-6"] = new(3.0, 15.0, 3.75, 6.0, 0.3),
    });

    /// <summary>One entry of pure input tokens, stamped at a given UTC instant.</summary>
    private static LocalUsageEntry InputEntryOn(int year, int month, int day, int hour = 12) => new(
        new DateTimeOffset(year, month, day, hour, 0, 0, TimeSpan.Zero),
        "claude-sonnet-5", null,
        InputTokens: 1_000_000,
        OutputTokens: 0,
        CacheWrite5mTokens: 0,
        CacheWrite1hTokens: 0,
        CacheReadTokens: 0);

    [Fact]
    public void Resolve_ExactId_ReturnsPricing()
    {
        var pricing = Table().Resolve("claude-fable-5");

        Assert.NotNull(pricing);
        Assert.Equal(10.0, pricing.InputPerMTok);
    }

    [Fact]
    public void Resolve_DatedId_StripsDateSuffix()
    {
        Assert.NotNull(Table().Resolve("claude-opus-4-8-20260115"));
    }

    [Fact]
    public void Resolve_BedrockPrefixAndVertexSuffix_Stripped()
    {
        var table = Table();
        Assert.NotNull(table.Resolve("anthropic.claude-opus-4-8"));
        Assert.NotNull(table.Resolve("claude-opus-4-8@20260115"));
    }

    [Fact]
    public void Resolve_BracketedRequestTier_Stripped()
    {
        var table = Table();

        // Claude Code reports the long-context tier as "claude-opus-4-8[1m]".
        // That window costs standard rates, so the tag must resolve to the
        // family row rather than falling through to the unpriced path.
        Assert.NotNull(table.Resolve("claude-opus-4-8[1m]"));
        Assert.Equal(table.Resolve("claude-opus-4-8"), table.Resolve("claude-opus-4-8[1m]"));
        Assert.Equal(table.Resolve("claude-opus-4-8"), table.Resolve("claude-opus-4-8[1m]-20260115"));
        Assert.Equal(table.Resolve("claude-fable-5"), table.Resolve("anthropic.claude-fable-5[1m]@vertex"));
    }

    [Fact]
    public void Resolve_LongestPrefixWins()
    {
        var table = Table();

        // "claude-sonnet-4-6-fast" must land on claude-sonnet-4-6, not the
        // shorter claude-sonnet-4 — and plain claude-sonnet-4-6 is an exact hit.
        Assert.Equal(table.Resolve("claude-sonnet-4-6"), table.Resolve("claude-sonnet-4-6-fast"));
        // A suffix at a '-' boundary still matches its base model.
        Assert.Equal(table.Resolve("claude-opus-4-8"), table.Resolve("claude-opus-4-8-fast"));
    }

    [Fact]
    public void Resolve_PrefixOnlyAtDashBoundary()
    {
        // "claude-sonnet-45" must NOT match "claude-sonnet-4" (no '-' boundary).
        Assert.Null(Table().Resolve("claude-sonnet-45"));
    }

    [Theory]
    [InlineData("claude-opus-4-9")]
    [InlineData("claude-sonnet-4-7")]
    [InlineData("claude-fable-5-1")]
    public void Resolve_UnknownNumericVersion_DoesNotFallBackToOlderPricing(string id)
    {
        // A new numeric version is a NEW model that may be priced differently —
        // it must show as unpriced, not silently billed at an older row's rate.
        Assert.Null(Table().Resolve(id));
    }

    [Theory]
    [InlineData("claude-new-hotness-6")]
    [InlineData("gpt-4o")]
    [InlineData("")]
    [InlineData("   ")]
    public void Resolve_UnknownModel_ReturnsNull(string id)
    {
        Assert.Null(Table().Resolve(id));
    }

    [Fact]
    public void CostUsd_UsesSeparateCacheRates()
    {
        var pricing = new ModelPricing(10.0, 50.0, 12.5, 20.0, 1.0);
        var entry = new LocalUsageEntry(
            DateTimeOffset.UtcNow, "claude-fable-5", null,
            InputTokens: 1_000_000,
            OutputTokens: 100_000,
            CacheWrite5mTokens: 200_000,
            CacheWrite1hTokens: 50_000,
            CacheReadTokens: 2_000_000);

        // 10 + 5 + 2.5 + 1 + 2 = 20.5
        Assert.Equal(20.5, pricing.CostUsd(entry), precision: 10);
    }

    [Fact]
    public void CostUsd_DatedPriorRate_AppliesInsideItsWindowOnly()
    {
        // Standing rate $3/$15 with a $2/$10 introductory window through Aug 31.
        var pricing = new ModelPricing(3.0, 15.0, 3.75, 6.0, 0.3, new[]
        {
            new DatedRate(new DateOnly(2026, 8, 31), 2.0, 10.0, 2.5, 4.0, 0.2),
        });

        Assert.Equal(2.0, pricing.CostUsd(InputEntryOn(2026, 8, 11)), precision: 10);
        // The last hour inside the window is still introductory...
        Assert.Equal(2.0, pricing.CostUsd(InputEntryOn(2026, 8, 31, hour: 23)), precision: 10);
        // ...and the first of the next day is not.
        Assert.Equal(3.0, pricing.CostUsd(InputEntryOn(2026, 9, 1, hour: 0)), precision: 10);
    }

    [Fact]
    public void CostUsd_DatedPriorRates_NarrowestCoveringWindowWins()
    {
        // Two superseded rate sets, listed newest-first so the tie-break can't
        // be an artifact of ordering: a day inside both must price at the one
        // that ended first.
        var pricing = new ModelPricing(3.0, 15.0, 3.75, 6.0, 0.3, new[]
        {
            new DatedRate(new DateOnly(2026, 8, 31), 2.0, 10.0, 2.5, 4.0, 0.2),
            new DatedRate(new DateOnly(2026, 6, 30), 1.0, 5.0, 1.25, 2.0, 0.1),
        });

        Assert.Equal(1.0, pricing.CostUsd(InputEntryOn(2026, 6, 1)), precision: 10);
        Assert.Equal(2.0, pricing.CostUsd(InputEntryOn(2026, 7, 1)), precision: 10);
        Assert.Equal(3.0, pricing.CostUsd(InputEntryOn(2026, 9, 1)), precision: 10);
    }

    [Fact]
    public void CostUsd_AllRateCategories_UsePriorWindowTogether()
    {
        var pricing = new ModelPricing(3.0, 15.0, 3.75, 6.0, 0.3, new[]
        {
            new DatedRate(new DateOnly(2026, 8, 31), 2.0, 10.0, 2.5, 4.0, 0.2),
        });
        var entry = new LocalUsageEntry(
            new DateTimeOffset(2026, 8, 11, 12, 0, 0, TimeSpan.Zero), "claude-sonnet-5", null,
            InputTokens: 1_000_000,
            OutputTokens: 1_000_000,
            CacheWrite5mTokens: 1_000_000,
            CacheWrite1hTokens: 1_000_000,
            CacheReadTokens: 1_000_000);

        // 2 + 10 + 2.5 + 4 + 0.2 = 18.7 — every category comes from the window.
        Assert.Equal(18.7, pricing.CostUsd(entry), precision: 10);
    }

    [Fact]
    public void LoadEmbedded_Sonnet5_UsesIntroRateThroughAugust2026()
    {
        var pricing = PricingTable.LoadEmbedded().Resolve("claude-sonnet-5");

        Assert.NotNull(pricing);
        // Introductory $2/MTok input through 2026-08-31, standard $3 from
        // 2026-09-01 — the changeover needs no release of its own.
        Assert.Equal(2.0, pricing.CostUsd(InputEntryOn(2026, 8, 31)), precision: 10);
        Assert.Equal(3.0, pricing.CostUsd(InputEntryOn(2026, 9, 1)), precision: 10);
    }

    [Fact]
    public void LoadEmbedded_LongContextTierIds_PriceAtTheFamilyRate()
    {
        var table = PricingTable.LoadEmbedded();

        Assert.NotNull(table.Resolve("claude-opus-5[1m]"));
        Assert.NotNull(table.Resolve("claude-sonnet-5[1m]"));
        Assert.Equal(table.Resolve("claude-opus-5"), table.Resolve("claude-opus-5[1m]"));
        Assert.Equal(table.Resolve("claude-sonnet-5"), table.Resolve("claude-sonnet-5[1m]"));
    }

    [Fact]
    public void LoadEmbedded_ParsesBundledTable()
    {
        var table = PricingTable.LoadEmbedded();

        // The bundled table must cover the current model families.
        Assert.NotNull(table.Resolve("claude-fable-5"));
        Assert.NotNull(table.Resolve("claude-opus-5"));
        Assert.NotNull(table.Resolve("claude-opus-4-8"));
        Assert.NotNull(table.Resolve("claude-sonnet-5"));
        Assert.NotNull(table.Resolve("claude-haiku-4-5"));
        // Dated variants land on their base rows.
        Assert.NotNull(table.Resolve("claude-sonnet-5-20251101"));
    }

    [Theory]
    // Anthropic standing list prices per MTok: input, output, 5m cache write,
    // 1h cache write, cache read. Source:
    // platform.claude.com/docs/en/about-claude/pricing (retrieved 2026-08-11).
    // Sonnet 5's introductory window is a superseded rate and is pinned
    // separately by LoadEmbedded_Sonnet5_UsesIntroRateThroughAugust2026.
    [InlineData("claude-fable-5", 10.0, 50.0, 12.5, 20.0, 1.0)]
    [InlineData("claude-mythos-5", 10.0, 50.0, 12.5, 20.0, 1.0)]
    [InlineData("claude-opus-5", 5.0, 25.0, 6.25, 10.0, 0.5)]
    [InlineData("claude-opus-4-8", 5.0, 25.0, 6.25, 10.0, 0.5)]
    [InlineData("claude-sonnet-5", 3.0, 15.0, 3.75, 6.0, 0.3)]
    [InlineData("claude-haiku-4-5", 1.0, 5.0, 1.25, 2.0, 0.1)]
    public void LoadEmbedded_CurrentModels_MatchPublishedRates(
        string id, double input, double output, double write5m, double write1h, double read)
    {
        var pricing = PricingTable.LoadEmbedded().Resolve(id);

        Assert.NotNull(pricing);
        Assert.Equal(input, pricing.InputPerMTok);
        Assert.Equal(output, pricing.OutputPerMTok);
        Assert.Equal(write5m, pricing.CacheWrite5mPerMTok);
        Assert.Equal(write1h, pricing.CacheWrite1hPerMTok);
        Assert.Equal(read, pricing.CacheReadPerMTok);
    }

    [Fact]
    public void Resolve_Opus5Variants_LandOnOpus5AndNotOnOpus4()
    {
        var table = PricingTable.LoadEmbedded();

        // The ids a transcript can carry for this model must all price at the
        // opus-5 row rather than falling through to an opus-4-x row.
        Assert.Equal(table.Resolve("claude-opus-5"), table.Resolve("claude-opus-5-20260101"));
        Assert.Equal(table.Resolve("claude-opus-5"), table.Resolve("anthropic.claude-opus-5"));
        Assert.Equal(table.Resolve("claude-opus-5"), table.Resolve("claude-opus-5-fast"));
        // A future numeric version is a different model and must stay unpriced.
        Assert.Null(table.Resolve("claude-opus-5-1"));
    }

    [Fact]
    public void Normalize_Composed_HandlesAllDecorations()
    {
        Assert.Equal("claude-opus-4-5", PricingTable.Normalize("anthropic.Claude-Opus-4-5-20251101@vertex"));
    }
}
