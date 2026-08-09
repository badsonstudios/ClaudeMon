namespace ClaudeMon.Tests;

using System.ComponentModel;
using ClaudeMon.Services;

public class BrowserLauncherTests
{
    [Theory]
    [InlineData("https://github.com/badsonstudios/ClaudeMon/releases/tag/v0.16.0")]
    [InlineData("http://example.com/notes")]
    [InlineData("HTTPS://EXAMPLE.COM/PATH")] // scheme comparison is case-insensitive
    public void IsSafeHttpUrl_HttpAndHttps_Accepted(string url)
    {
        Assert.True(BrowserLauncher.IsSafeHttpUrl(url, out var uri));
        Assert.NotNull(uri);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    [InlineData("ftp://example.com/pub")]
    [InlineData("ms-settings:display")]
    [InlineData("releases/tag/v0.16.0")] // relative
    [InlineData("not a url")]
    [InlineData("")]
    [InlineData(null)]
    public void IsSafeHttpUrl_NonHttpSchemes_Rejected(string? url)
    {
        Assert.False(BrowserLauncher.IsSafeHttpUrl(url, out var uri));
        // The out uri is nulled on rejection so a caller can't accidentally use it.
        Assert.Null(uri);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData(null)]
    public void TryOpenHttp_RejectedUrl_DoesNotThrow(string? url)
    {
        // Must be a silent no-op — nothing is launched and nothing escapes.
        BrowserLauncher.TryOpenHttp(url);
    }

    // The overload below injects the shell-execute so these can assert what would have been
    // launched without actually opening a browser on the test machine.

    [Fact]
    public void TryOpenHttp_AcceptedUrl_ShellsOutToTheNormalizedUri()
    {
        var opened = new List<Uri>();

        BrowserLauncher.TryOpenHttp("https://example.com/a b", opened.Add);

        // The Uri is what gets handed to the shell, not the raw string — so it is already
        // escaped by the time it leaves this class.
        Assert.Equal("https://example.com/a%20b", Assert.Single(opened).AbsoluteUri);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    [InlineData("ms-settings:display")]
    [InlineData("")]
    [InlineData(null)]
    public void TryOpenHttp_RejectedUrl_NeverReachesTheShell(string? url)
    {
        var opened = new List<Uri>();

        BrowserLauncher.TryOpenHttp(url, opened.Add);

        Assert.Empty(opened);
    }

    [Fact]
    public void TryOpenHttp_ShellFailure_IsSwallowed()
    {
        // No default browser, a broken file association, a shell that refuses — none of it is
        // something the caller can act on, and none of it may escape a click handler.
        var ex = Record.Exception(() => BrowserLauncher.TryOpenHttp(
            "https://example.com/", _ => throw new Win32Exception("no browser")));

        Assert.Null(ex);
    }
}
