namespace ClaudeMon.Tests;

using ClaudeMon.Services;

public class ProjectDisplayTests
{
    [Fact]
    public void Resolve_LearnedPath_Wins()
    {
        var paths = new Dictionary<string, string> { ["c--Projects-ClaudeMon"] = @"C:\Projects\ClaudeMon" };
        Assert.Equal(@"C:\Projects\ClaudeMon", ProjectDisplay.Resolve("c--Projects-ClaudeMon", paths));
    }

    [Fact]
    public void Resolve_UnknownOrEmptyPath_FallsBackToRawKey()
    {
        var empty = new Dictionary<string, string>();
        Assert.Equal("c--Some-Project", ProjectDisplay.Resolve("c--Some-Project", empty));

        var blank = new Dictionary<string, string> { ["key"] = "" };
        Assert.Equal("key", ProjectDisplay.Resolve("key", blank));
    }
}
