namespace ClaudeMon.Services;

using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;

/// <summary>
/// Downloads a release installer into <c>%TEMP%</c>, verifies it against the release's published
/// SHA-256 checksum, and launches it silently. Downloading with <see cref="HttpClient"/> (rather
/// than a browser) means the file carries no Mark-of-the-Web, so SmartScreen never prompts; the
/// installer's <c>PrivilegesRequired=lowest</c> means no UAC either. The app keeps running after
/// <see cref="LaunchInstaller"/> — the installer stops it, copies files, and relaunches the new
/// version itself (see the <c>[Code]</c> section of <c>installer/ClaudeMon.iss</c>).
/// </summary>
public sealed class UpdateInstaller : IDisposable
{
    private const string SetupFilePrefix = "ClaudeMon-Setup-";

    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;

    public UpdateInstaller(HttpClient? httpClient = null)
    {
        _ownsHttpClient = httpClient is null;
        // Generous timeout: this moves a ~10 MB installer, not a small JSON response.
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        // GitHub's asset host doesn't strictly require a User-Agent, but api.github.com URLs
        // do and sending one is never wrong; a default header covers both downloads.
        _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ClaudeMon", "updater"));
    }

    /// <summary>
    /// Downloads the installer and its checksum, verifies the SHA-256, and returns the local
    /// installer path. Any failure — network, disk, missing or mismatched checksum — yields a
    /// non-throwing <see cref="UpdateDownloadResult.ErrorMessage"/> result (mirroring
    /// <see cref="UpdateChecker"/>); a verification failure also deletes the download so a bad
    /// file can't linger. Cancellation propagates as <see cref="OperationCanceledException"/>.
    /// </summary>
    /// <param name="progress">Reports download progress in [0, 1]; nothing is reported when the
    /// response carries no Content-Length (the dialog then just shows its initial text).</param>
    public async Task<UpdateDownloadResult> DownloadAndVerifyAsync(
        string installerUrl, string checksumUrl,
        IProgress<double>? progress, CancellationToken cancellationToken)
    {
        // This path ends in executing the download, so hold it to at least the bar OpenUpdatePage
        // applies to merely opening a page: absolute HTTPS only, even though the URLs come from a
        // trusted HTTPS response. TryCreate also keeps the never-throws contract (a malformed
        // asset URL must fail like any other bad input, not escape the result-record pattern).
        if (!Uri.TryCreate(installerUrl, UriKind.Absolute, out var installerUri)
            || installerUri.Scheme != Uri.UriSchemeHttps
            || !Uri.TryCreate(checksumUrl, UriKind.Absolute, out var checksumUri)
            || checksumUri.Scheme != Uri.UriSchemeHttps)
            return UpdateDownloadResult.Failed("The release's download URLs are not valid HTTPS URLs.");

        var fileName = Path.GetFileName(installerUri.LocalPath);
        if (!fileName.StartsWith(SetupFilePrefix, StringComparison.OrdinalIgnoreCase)
            || !fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return UpdateDownloadResult.Failed("Unexpected installer file name.");

        var installerPath = Path.Combine(Path.GetTempPath(), fileName);
        try
        {
            var checksumText = await _httpClient.GetStringAsync(checksumUrl, cancellationToken);
            if (!TryParseChecksum(checksumText, out var expectedSha256))
                return UpdateDownloadResult.Failed("The release's checksum file could not be read.");

            await DownloadToFileAsync(installerUrl, installerPath, progress, cancellationToken);

            var actualSha256 = await ComputeSha256Async(installerPath, cancellationToken);
            if (!string.Equals(actualSha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(installerPath);
                return UpdateDownloadResult.Failed(
                    "The downloaded installer failed checksum verification.");
            }

            return UpdateDownloadResult.Downloaded(installerPath);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            TryDelete(installerPath);
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException
            or UnauthorizedAccessException)
        {
            TryDelete(installerPath);
            return UpdateDownloadResult.Failed(ex.Message);
        }
    }

    private async Task DownloadToFileAsync(
        string url, string destinationPath, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await _httpClient.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength;
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var destination = File.Create(destinationPath);

        var buffer = new byte[81920];
        long copied = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            copied += read;
            if (totalBytes > 0)
                progress?.Report(Math.Min(1.0, (double)copied / totalBytes.Value));
        }
    }

    /// <summary>
    /// Extracts the hash from a checksum asset: the first whitespace-separated token, which
    /// covers both a bare hash and the <c>sha256sum</c> "&lt;hash&gt;  &lt;filename&gt;" format
    /// that publish-release writes. Must be exactly 64 hex chars.
    /// </summary>
    internal static bool TryParseChecksum(string? text, out string sha256Hex)
    {
        sha256Hex = "";
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var token = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[0];
        if (token.Length != 64 || !token.All(Uri.IsHexDigit))
            return false;

        sha256Hex = token;
        return true;
    }

    internal static async Task<string> ComputeSha256Async(
        string filePath, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(filePath);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash);
    }

    /// <summary>
    /// The silent-install argument line. <c>/MERGETASKS</c> pins the installer's "run at Windows
    /// startup" task to the user's <i>current</i> registry state — without it a silent install
    /// would apply the task's checked-by-default and re-enable startup for users who turned it
    /// off (whether they unticked it during install or used the Settings toggle; both own the
    /// same HKCU Run value).
    /// </summary>
    internal static string BuildInstallerArguments(bool runAtStartupEnabled) =>
        "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /MERGETASKS=\""
        + (runAtStartupEnabled ? "startup" : "!startup") + "\"";

    /// <summary>
    /// Starts the verified installer, returning the process (so the caller can notice it exiting
    /// without having installed) or null — rather than throwing — if it can't be started, in
    /// which case the caller falls back to the release page. The app deliberately keeps running:
    /// the installer kills it once file copy begins and relaunches it afterward, which also
    /// releases the single-instance mutex for the relaunched copy.
    /// </summary>
    public static Process? LaunchInstaller(string installerPath, string arguments)
    {
        try
        {
            return Process.Start(new ProcessStartInfo(installerPath, arguments) { UseShellExecute = false });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException
            or FileNotFoundException)
        {
            return null;
        }
    }

    /// <summary>
    /// Best-effort deletion of installers left in <c>%TEMP%</c> by previous updates (the running
    /// installer can't delete itself). Called at startup, when any installer from the update
    /// that just completed is finished and unlocked; one still mid-run just fails to delete and
    /// is retried next launch.
    /// </summary>
    public static void CleanUpStaleDownloads()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(Path.GetTempPath(), SetupFilePrefix + "*.exe"))
                TryDelete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Enumerating temp failed — nothing worth surfacing.
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort cleanup; a lingering temp file is not worth surfacing.
        }
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
            _httpClient.Dispose();
    }
}

public record UpdateDownloadResult
{
    /// <summary>Local path of the verified installer; null when the download failed.</summary>
    public string? InstallerPath { get; private init; }

    public string? ErrorMessage { get; private init; }

    public static UpdateDownloadResult Downloaded(string installerPath) =>
        new() { InstallerPath = installerPath };

    public static UpdateDownloadResult Failed(string message) =>
        new() { ErrorMessage = message };
}
