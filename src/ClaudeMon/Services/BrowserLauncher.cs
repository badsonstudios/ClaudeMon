namespace ClaudeMon.Services;

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

/// <summary>
/// Opens URLs in the default browser, but only ever http(s) ones — a URL that reaches a
/// shell-execute must never carry an arbitrary scheme, even when it came from a trusted
/// HTTPS response (extracted from TrayApplication.OpenUpdatePage so the update dialog can
/// share the same guard).
/// </summary>
internal static class BrowserLauncher
{
    /// <summary>
    /// True when <paramref name="url"/> is an absolute http or https URL — the only kind
    /// <see cref="TryOpenHttp"/> will shell out to. Pure, so the policy is unit-testable
    /// apart from the Process.Start side effect.
    /// </summary>
    public static bool IsSafeHttpUrl(string? url, [NotNullWhen(true)] out Uri? uri)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            return true;

        uri = null;
        return false;
    }

    /// <summary>
    /// Opens <paramref name="url"/> in the default browser if it passes
    /// <see cref="IsSafeHttpUrl"/>; anything else is silently ignored. Best-effort — a shell
    /// that can't open the URL is not an error the caller can act on.
    /// </summary>
    public static void TryOpenHttp(string? url)
    {
        if (!IsSafeHttpUrl(url, out var uri))
            return;

        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch
        {
            // Best-effort: if the shell can't open the URL there's nothing useful to do.
        }
    }
}
