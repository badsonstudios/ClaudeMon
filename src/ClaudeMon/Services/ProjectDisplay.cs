namespace ClaudeMon.Services;

/// <summary>
/// Maps a project aggregation key (the directory name under
/// ~/.claude/projects, e.g. "c--Projects-ClaudeMon") to what the breakdown UI
/// shows. The dashed encoding is lossy — a '-' in a real folder name is
/// indistinguishable from a path separator — so decoding is never attempted;
/// instead the real working directory learned from the transcripts' "cwd"
/// field is preferred, with the raw key as the honest fallback.
/// </summary>
public static class ProjectDisplay
{
    public static string Resolve(string projectKey, IReadOnlyDictionary<string, string> learnedPaths) =>
        learnedPaths.TryGetValue(projectKey, out var path) && path.Length > 0
            ? path
            : projectKey;
}
