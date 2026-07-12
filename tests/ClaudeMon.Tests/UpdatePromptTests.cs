namespace ClaudeMon.Tests;

using ClaudeMon.Services;

public class UpdatePromptTests
{
    [Fact]
    public void AutomaticCheck_NothingIgnored_Prompts()
    {
        Assert.True(UpdatePrompt.ShouldPrompt(manual: false, "0.12.0", ignoredVersion: null));
    }

    [Fact]
    public void AutomaticCheck_IgnoredVersion_DoesNotPrompt()
    {
        Assert.False(UpdatePrompt.ShouldPrompt(manual: false, "0.12.0", ignoredVersion: "0.12.0"));
    }

    [Fact]
    public void AutomaticCheck_NewerThanIgnoredVersion_PromptsAgain()
    {
        Assert.True(UpdatePrompt.ShouldPrompt(manual: false, "0.13.0", ignoredVersion: "0.12.0"));
    }

    [Fact]
    public void ManualCheck_AlwaysPrompts_EvenForIgnoredVersion()
    {
        Assert.True(UpdatePrompt.ShouldPrompt(manual: true, "0.12.0", ignoredVersion: "0.12.0"));
    }
}
