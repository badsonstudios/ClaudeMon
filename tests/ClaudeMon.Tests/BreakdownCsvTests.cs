namespace ClaudeMon.Tests;

using ClaudeMon.Models;
using ClaudeMon.Services;

public class BreakdownCsvTests
{
    private static BreakdownRow Row(
        string key, string display, long input = 100, long output = 200,
        long cw = 10, long cr = 1000, double cost = 1.25, bool unpriced = false) =>
        new(key, display, input, output, cw, cr, cost, unpriced);

    private static LocalUsageBreakdown Breakdown(
        IReadOnlyList<BreakdownRow>? models = null, IReadOnlyList<BreakdownRow>? projects = null) =>
        new(new DateOnly(2026, 7, 13), new DateOnly(2026, 7, 19),
            models ?? [Row("claude-fable-5", "claude-fable-5")],
            projects ?? [Row("proj-a", @"C:\Projects\A")],
            Row("total", "Total", cost: 2.5));

    [Fact]
    public void Compose_HeaderAndSectionsAndTotals()
    {
        var lines = BreakdownCsv.Compose(Breakdown()).TrimEnd().Split('\n').Select(l => l.TrimEnd('\r')).ToArray();

        Assert.Equal(BreakdownCsv.Header, lines[0]);
        Assert.Equal(4, lines.Length);
        Assert.StartsWith("model,claude-fable-5,100,200,10,1000,1310,1.25,false", lines[1]);
        Assert.StartsWith(@"project,C:\Projects\A,", lines[2]);
        // Totals row has an empty name.
        Assert.StartsWith("total,,", lines[3]);
        Assert.Contains(",2.5,", lines[3]);
    }

    [Fact]
    public void Compose_InvariantDecimals()
    {
        var csv = BreakdownCsv.Compose(Breakdown(
            models: [Row("m", "m", cost: 41.2372)]));

        Assert.Contains(",41.2372,", csv);
        Assert.DoesNotContain(",41,2372,", csv);
    }

    [Fact]
    public void Compose_UnpricedRow_FlaggedWithCostFloor()
    {
        var csv = BreakdownCsv.Compose(Breakdown(
            models: [Row("m", "m", cost: 3.0, unpriced: true)]));

        Assert.Contains(",3.0,true", csv);
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("has,comma", "\"has,comma\"")]
    [InlineData("has\"quote", "\"has\"\"quote\"")]
    [InlineData("line\nbreak", "\"line\nbreak\"")]
    public void EscapeField_Rfc4180(string value, string expected)
    {
        Assert.Equal(expected, BreakdownCsv.EscapeField(value));
    }

    [Theory]
    [InlineData("=cmd|whatever", "'=cmd|whatever")]
    [InlineData("+1234", "'+1234")]
    [InlineData("@import", "'@import")]
    [InlineData("-leading-dash", "'-leading-dash")]
    public void EscapeField_FormulaTriggers_NeutralizedForExcel(string value, string expected)
    {
        Assert.Equal(expected, BreakdownCsv.EscapeField(value));
    }

    [Fact]
    public void Compose_ProjectPathWithComma_Quoted()
    {
        var csv = BreakdownCsv.Compose(Breakdown(
            projects: [Row("p", @"C:\Odd, Path\Proj")]));

        Assert.Contains("\"C:\\Odd, Path\\Proj\"", csv);
    }
}
