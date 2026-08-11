namespace ClaudeMon.Services;

using System.Net.Http.Headers;

/// <summary>
/// The single place ClaudeMon's HTTP <c>User-Agent</c> is built. Every outbound request the app
/// makes — the usage API, the OAuth token refresh, the GitHub update check, the installer
/// download — sends the same <c>ClaudeMon/&lt;version&gt;</c> token, so one install's traffic
/// reads as one app rather than three differently-versioned ones (#141). GitHub's API rejects
/// requests that send no User-Agent at all, so this is load-bearing there, not decoration.
/// </summary>
/// <remarks>
/// Deliberately honest — not <c>claude-cli/…</c>. Live testing (#136) found the usage endpoint's
/// rate limit is keyed on the token, not the User-Agent, so impersonating the CLI would buy no
/// reliability, only misattribution.
/// </remarks>
internal static class AppUserAgent
{
    /// <summary>
    /// The shared header value. <see cref="ProductInfoHeaderValue"/> is immutable, so the one
    /// instance is safe to add to any number of requests from any thread.
    /// </summary>
    internal static readonly ProductInfoHeaderValue Header =
        new("ClaudeMon", FormatVersion(typeof(AppUserAgent).Assembly.GetName().Version));

    /// <summary>
    /// Renders the assembly version as the 3-part User-Agent product version ("0.26.0"); the
    /// assembly's 4th component is always 0 and only adds noise.
    /// </summary>
    internal static string FormatVersion(Version? version)
    {
        var v = version ?? new Version(0, 0);
        return $"{v.Major}.{v.Minor}.{Math.Max(v.Build, 0)}";
    }
}
