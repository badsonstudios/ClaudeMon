namespace ClaudeMon.Services;

/// <summary>
/// Decides whether a discovered update should surface the update dialog. A manual check always
/// prompts — asking is explicit intent, even for a version the user chose to skip; an automatic
/// check prompts unless the user picked "Skip this version" for exactly this version. A newer
/// release than the skipped one prompts again (the skip is per-version, not "stop reminding me").
/// </summary>
internal static class UpdatePrompt
{
    public static bool ShouldPrompt(bool manual, string version, string? ignoredVersion) =>
        manual || !string.Equals(version, ignoredVersion, StringComparison.OrdinalIgnoreCase);
}
