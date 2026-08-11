namespace ClaudeMon.Tests;

using ClaudeMon.Services;

public class AppUserAgentTests
{
    [Theory]
    [InlineData(0, 26, 0, 0, "0.26.0")]
    [InlineData(1, 2, 3, 4, "1.2.3")]
    public void FormatVersion_DropsTheFourthComponent(
        int major, int minor, int build, int revision, string expected)
    {
        Assert.Equal(expected, AppUserAgent.FormatVersion(new Version(major, minor, build, revision)));
    }

    // A missing or 2-part version must still yield a valid product token, never "ClaudeMon/-1"
    // (Version.Build is -1 when unspecified), which would throw when added to the header.
    [Fact]
    public void FormatVersion_MissingOrPartialVersion_StillWellFormed()
    {
        Assert.Equal("0.0.0", AppUserAgent.FormatVersion(null));
        Assert.Equal("1.2.0", AppUserAgent.FormatVersion(new Version(1, 2)));
    }

    // Every caller sends this one value, so its shape is asserted once here rather than
    // re-derived in each service's tests.
    [Fact]
    public void Header_IsClaudeMonAtTheRealAssemblyVersion()
    {
        var assemblyVersion = typeof(ClaudeApiClient).Assembly.GetName().Version;
        Assert.NotNull(assemblyVersion);

        Assert.Equal("ClaudeMon", AppUserAgent.Header.Product?.Name);
        // Pinned to the real assembly version, so a broken version lookup can't pass as "0.0.0".
        Assert.Equal(AppUserAgent.FormatVersion(assemblyVersion), AppUserAgent.Header.Product?.Version);
        Assert.NotEqual("0.0.0", AppUserAgent.Header.Product?.Version);
    }
}
