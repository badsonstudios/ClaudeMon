namespace ClaudeMon.Tests;

using ClaudeMon.Models;
using ClaudeMon.Monitoring;

public class LimitHistoryTextTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 22, 12, 0, 0, TimeSpan.Zero);

    private static LimitWindowRecord Window(
        Dictionary<string, ModelTokens>? tokens = null, ClaudePlan? plan = ClaudePlan.Max20x,
        bool planChanged = false) =>
        new("session", null, null, T0 - UsageWindows.FiveHour, T0, false,
            40, 38, T0, 10, plan, plan, planChanged,
            tokens ?? new Dictionary<string, ModelTokens> { ["claude-opus-4-6"] = new(1_200_000, 0, 0, 0) },
            false, null);

    [Fact]
    public void KindLabel_CoversTheKnownKindsAndHumanizesUnknowns()
    {
        Assert.Equal("Session (5-hour)", LimitHistoryText.KindLabel("session", null));
        Assert.Equal("Weekly", LimitHistoryText.KindLabel("weekly_all", null));
        Assert.Equal("Weekly (Opus 4)", LimitHistoryText.KindLabel("weekly_scoped", "Opus 4"));
        Assert.Equal("Weekly (model)", LimitHistoryText.KindLabel("weekly_scoped", "  "));
        Assert.Equal("Monthly special", LimitHistoryText.KindLabel("monthly_special", null));
        Assert.Equal("Limit", LimitHistoryText.KindLabel(null, null));
    }

    [Fact]
    public void RawTotalAndTopModel_AgreeBetweenTheCellAndTheSort()
    {
        // Both the table cell and the sorter go through these helpers, so a cache-heavy
        // window can't display one number and sort by another.
        var window = Window(tokens: new Dictionary<string, ModelTokens>
        {
            ["claude-opus-4-6"] = new(100_000, 0, 0, 10_000_000), // raw 10.1M, weighted ~1.1M
            ["claude-fable-5"] = new(200_000, 0, 0, 0),
        });

        Assert.Equal(10_300_000, LimitWindowCapacity.RawTotal(window));
        Assert.Equal("claude-opus-4-6", LimitWindowCapacity.TopModel(window));
        Assert.Null(LimitWindowCapacity.TopModel(Window(tokens: new Dictionary<string, ModelTokens>())));
    }

    [Fact]
    public void CapacityText_FormatsOrDashes()
    {
        var row = LimitWindowCapacity.RowFor(Window(tokens: new Dictionary<string, ModelTokens>
        {
            ["opus"] = new(24_400_000, 0, 0, 0), // 40% peak → ≈61M
        }));
        Assert.Equal("≈61M", LimitHistoryText.CapacityText(row));

        var none = LimitWindowCapacity.RowFor(Window(tokens: new Dictionary<string, ModelTokens>()));
        Assert.Equal("—", LimitHistoryText.CapacityText(none));
    }

    [Fact]
    public void PlanText_NamesThePlanAndFlagsAMidWindowChange()
    {
        Assert.Equal("Max 20x", LimitHistoryText.PlanText(Window()));
        Assert.Equal("Pro (changed)", LimitHistoryText.PlanText(Window(plan: ClaudePlan.Pro, planChanged: true)));
        Assert.Equal("—", LimitHistoryText.PlanText(Window(plan: null)));
    }

    [Fact]
    public void DriftAlert_SaysHowFarBelowTheNorm()
    {
        var (title, text) = LimitHistoryText.DriftAlert("session", null, 42_000_000, 60_000_000);

        Assert.Equal("Possible throttling: Session (5-hour) capacity down", title);
        Assert.Contains("≈42M", text);
        Assert.Contains("30% below", text);
        Assert.Contains("≈60M", text);
        Assert.EndsWith("(est.)", text);
    }
}
