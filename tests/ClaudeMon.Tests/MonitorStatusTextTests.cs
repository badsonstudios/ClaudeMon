namespace ClaudeMon.Tests;

using ClaudeMon.Monitoring;

public class MonitorStatusTextTests
{
    [Theory]
    [InlineData(MonitorStatus.Connected, "Connected")]
    [InlineData(MonitorStatus.RateLimited, "Rate limited")]
    [InlineData(MonitorStatus.AuthError, "Sign-in expired")]
    [InlineData(MonitorStatus.Offline, "Offline")]
    [InlineData(MonitorStatus.Initializing, "Connecting…")]
    public void Describe_ReturnsExpectedLabel(MonitorStatus status, string expected)
    {
        Assert.Equal(expected, MonitorStatusText.Describe(status));
    }

    [Fact]
    public void SignInExpired_IsActionableAndMentionsClaudeCode()
    {
        Assert.False(string.IsNullOrWhiteSpace(MonitorStatusText.SignInExpired));
        Assert.Contains("Claude Code", MonitorStatusText.SignInExpired);
    }
}
