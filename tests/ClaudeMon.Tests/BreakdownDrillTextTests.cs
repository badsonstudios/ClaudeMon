namespace ClaudeMon.Tests;

using ClaudeMon.UI;

/// <summary>
/// The headings above the two "Usage &amp; costs" tables (#112) — the only thing on screen that
/// says whether a table is showing everything or one row's counterparts, so the wording (and the
/// shortening that keeps it on one line) is pinned here.
/// </summary>
public class BreakdownDrillTextTests
{
    [Fact]
    public void Sections_WithoutADrillDown_AreThePlainHeadings()
    {
        Assert.Equal("By model", BreakdownDrillText.ModelSection(null));
        Assert.Equal("By project", BreakdownDrillText.ProjectSection(null));
    }

    [Fact]
    public void ChartSection_SaysItIsNotFilteredWhileADrillDownRuns()
    {
        // The chart is whole-timeframe by design, so the one case that matters is the drilled one:
        // its heading has to contradict the narrower question still showing on the Tables tab.
        Assert.Equal("Cost per day", BreakdownDrillText.ChartSection(null));
        Assert.Equal("Cost per day — everything, not just claude-fable-5",
            BreakdownDrillText.ChartSection("claude-fable-5"));
    }

    [Fact]
    public void ChartSection_ShortensALongName()
    {
        var heading = BreakdownDrillText.ChartSection(new string('x', 200));

        Assert.Contains("…", heading);
        Assert.True(heading.Length < 100);
    }

    [Fact]
    public void Sections_NameWhatTheyAreShowing()
    {
        Assert.Equal(@"Models used in C:\Projects\ClaudeMon",
            BreakdownDrillText.ModelSection(@"C:\Projects\ClaudeMon"));
        Assert.Equal("Projects using claude-fable-5",
            BreakdownDrillText.ProjectSection("claude-fable-5"));
    }

    [Fact]
    public void Shorten_LeavesNamesThatFitAlone()
    {
        var name = new string('x', BreakdownDrillText.MaxNameLength);
        Assert.Equal(name, BreakdownDrillText.Shorten(name));
    }

    [Fact]
    public void Shorten_ElidesTheMiddleAndKeepsTheLeafDirectory()
    {
        var path = @"C:\Users\someone\src\a-very-long-directory-name\claudemon";

        var shortened = BreakdownDrillText.Shorten(path);

        Assert.Equal(BreakdownDrillText.MaxNameLength, shortened.Length);
        Assert.StartsWith(@"C:\Users", shortened);
        Assert.EndsWith("claudemon", shortened);
        Assert.Contains('…', shortened);
    }

    [Theory]
    [InlineData(20)]
    [InlineData(21)]
    [InlineData(22)]
    [InlineData(23)]
    public void Shorten_NeverSplitsASurrogatePair(int lead)
    {
        // A directory name can hold astral characters; half a surrogate pair renders as a
        // replacement box. The lead length walks both cut points across an emoji.
        var value = new string('a', lead) + "😀" + new string('b', 60) + "😀" + new string('c', lead);

        var shortened = BreakdownDrillText.Shorten(value);

        for (var i = 0; i < shortened.Length; i++)
        {
            if (char.IsHighSurrogate(shortened[i]))
            {
                Assert.True(i + 1 < shortened.Length && char.IsLowSurrogate(shortened[i + 1]),
                    $"lone high surrogate at {i} in '{shortened}'");
                i++;
            }
            else
            {
                Assert.False(char.IsLowSurrogate(shortened[i]), $"lone low surrogate at {i}");
            }
        }
    }

    [Fact]
    public void Shorten_AppliesInsideTheHeading()
    {
        var heading = BreakdownDrillText.ModelSection(new string('x', 200));

        Assert.Equal("Models used in ".Length + BreakdownDrillText.MaxNameLength, heading.Length);
    }
}
