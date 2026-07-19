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
    public void LoadEmbedded_ParsesBundledTable()
    {
        var table = PricingTable.LoadEmbedded();

        // The bundled table must cover the current model families.
        Assert.NotNull(table.Resolve("claude-fable-5"));
        Assert.NotNull(table.Resolve("claude-opus-4-8"));
        Assert.NotNull(table.Resolve("claude-sonnet-5"));
        Assert.NotNull(table.Resolve("claude-haiku-4-5"));
        // Dated variants land on their base rows.
        Assert.NotNull(table.Resolve("claude-sonnet-5-20251101"));
    }

    [Fact]
    public void Normalize_Composed_HandlesAllDecorations()
    {
        Assert.Equal("claude-opus-4-5", PricingTable.Normalize("anthropic.Claude-Opus-4-5-20251101@vertex"));
    }
}
