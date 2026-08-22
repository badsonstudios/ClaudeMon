namespace ClaudeMon.Tests;

using ClaudeMon.Models;
using ClaudeMon.Monitoring;
using ClaudeMon.Services;

public class LimitWindowSortTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 22, 12, 0, 0, TimeSpan.Zero);

    private static LimitHistoryRow Row(
        DateTimeOffset end, double peak = 40, long tokens = 400_000, string kind = "session") =>
        LimitWindowCapacity.RowFor(new LimitWindowRecord(
            kind, null, null, end - UsageWindows.FiveHour, end, false,
            peak, peak, end, 10, null, null, false,
            new Dictionary<string, ModelTokens> { ["opus"] = new(tokens, 0, 0, 0) },
            false, null));

    [Fact]
    public void Default_IsNewestFirst()
    {
        var rows = new[] { Row(T0), Row(T0 + TimeSpan.FromHours(5)), Row(T0 - TimeSpan.FromHours(5)) };

        var ordered = LimitWindowSort.Order(rows, LimitWindowSortState.Default);

        Assert.Equal(T0 + TimeSpan.FromHours(5), ordered[0].Record.End);
        Assert.Equal(T0 - TimeSpan.FromHours(5), ordered[^1].Record.End);
    }

    [Fact]
    public void Toggle_ReversesTheSameColumn_SwitchesToNaturalDirectionOnANewOne()
    {
        var state = LimitWindowSortState.Default;

        var reversed = state.Toggle((int)LimitWindowColumn.End);
        Assert.True(reversed.Ascending);

        var byKind = state.Toggle((int)LimitWindowColumn.Kind);
        Assert.Equal(LimitWindowColumn.Kind, byKind.Column);
        Assert.True(byKind.Ascending); // text reads A→Z first

        var byPeak = state.Toggle((int)LimitWindowColumn.Peak);
        Assert.False(byPeak.Ascending); // numbers read biggest-first

        Assert.Equal(state, state.Toggle(99)); // out-of-range leaves the state alone
    }

    [Fact]
    public void Capacity_SortsByTheNumberAndPinsCapacitylessRowsBelow()
    {
        var none = LimitWindowCapacity.RowFor(new LimitWindowRecord(
            "session", null, null, T0 - UsageWindows.FiveHour, T0, false,
            2, 2, T0, 1, null, null, false,
            new Dictionary<string, ModelTokens> { ["opus"] = new(1000, 0, 0, 0) },
            false, null)); // 2% peak — below the floor, no capacity
        var small = Row(T0 + TimeSpan.FromHours(5), peak: 40, tokens: 100_000);
        var large = Row(T0 + TimeSpan.FromHours(10), peak: 40, tokens: 900_000);

        var descending = LimitWindowSort.Order(
            [none, small, large], new LimitWindowSortState(LimitWindowColumn.Capacity, false));
        Assert.Same(large, descending[0]);
        Assert.Same(none, descending[^1]);

        var ascending = LimitWindowSort.Order(
            [none, small, large], new LimitWindowSortState(LimitWindowColumn.Capacity, true));
        Assert.Same(small, ascending[0]);
        Assert.Same(none, ascending[^1]); // "—" pins below in both directions
    }

    [Fact]
    public void Kind_SortsByTheDisplayLabelNotTheRawKind()
    {
        var session = Row(T0);
        var weekly = Row(T0, kind: "weekly_all");

        var ordered = LimitWindowSort.Order(
            [weekly, session], new LimitWindowSortState(LimitWindowColumn.Kind, true));

        // "Session (5-hour)" < "Weekly" alphabetically.
        Assert.Same(session, ordered[0]);
    }
}
