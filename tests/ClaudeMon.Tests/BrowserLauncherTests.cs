namespace ClaudeMon.Tests;

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
}
