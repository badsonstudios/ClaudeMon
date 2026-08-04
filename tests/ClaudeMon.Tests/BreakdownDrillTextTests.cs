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

    [Fact]
    public void Shorten_AppliesInsideTheHeading()
    {
        var heading = BreakdownDrillText.ModelSection(new string('x', 200));

        Assert.Equal("Models used in ".Length + BreakdownDrillText.MaxNameLength, heading.Length);
    }
}
