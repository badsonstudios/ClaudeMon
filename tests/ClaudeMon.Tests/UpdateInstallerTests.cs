namespace ClaudeMon.Tests;

using System.Net;
using System.Security.Cryptography;
using System.Text;
using ClaudeMon.Services;

public class UpdateInstallerTests : IDisposable
{
    private const string InstallerUrl = "https://example.com/ClaudeMon-Setup-0.0.0-test.exe";
    private const string ChecksumUrl = "https://example.com/ClaudeMon-Setup-0.0.0-test.exe.sha256";

    private static readonly byte[] InstallerBytes = Encoding.ASCII.GetBytes("fake installer payload");
    private static readonly string InstallerSha256 =
        Convert.ToHexString(SHA256.HashData(InstallerBytes)).ToLowerInvariant();

    private readonly MockHttpHandler _handler;
    private readonly UpdateInstaller _installer;
    private readonly string _downloadPath =
        Path.Combine(Path.GetTempPath(), "ClaudeMon-Setup-0.0.0-test.exe");

    public UpdateInstallerTests()
    {
        _handler = new MockHttpHandler();
        _installer = new UpdateInstaller(new HttpClient(_handler));
    }

    public void Dispose()
    {
        _installer.Dispose();
        _handler.Dispose();
        if (File.Exists(_downloadPath))
            File.Delete(_downloadPath);
    }

    [Fact]
    public async Task Download_ValidChecksum_ReturnsInstallerPath()
    {
        _handler.Map(InstallerUrl, HttpStatusCode.OK, InstallerBytes);
        _handler.Map(ChecksumUrl, HttpStatusCode.OK,
            Encoding.ASCII.GetBytes($"{InstallerSha256}  ClaudeMon-Setup-0.0.0-test.exe\n"));

        var result = await _installer.DownloadAndVerifyAsync(
            InstallerUrl, ChecksumUrl, progress: null, CancellationToken.None);

        Assert.Null(result.ErrorMessage);
        Assert.Equal(_downloadPath, result.InstallerPath);
        Assert.Equal(InstallerBytes, await File.ReadAllBytesAsync(_downloadPath));
    }

    [Fact]
    public async Task Download_ChecksumMismatch_FailsAndDeletesTheDownload()
    {
        _handler.Map(InstallerUrl, HttpStatusCode.OK, InstallerBytes);
        _handler.Map(ChecksumUrl, HttpStatusCode.OK,
            Encoding.ASCII.GetBytes(new string('a', 64) + "  ClaudeMon-Setup-0.0.0-test.exe\n"));

        var result = await _installer.DownloadAndVerifyAsync(
            InstallerUrl, ChecksumUrl, progress: null, CancellationToken.None);

        Assert.Null(result.InstallerPath);
        Assert.NotNull(result.ErrorMessage);
        // A file that failed verification must never be left around to be run later.
        Assert.False(File.Exists(_downloadPath));
    }

    [Fact]
    public async Task Download_UnparseableChecksumAsset_FailsBeforeDownloading()
    {
        _handler.Map(InstallerUrl, HttpStatusCode.OK, InstallerBytes);
        _handler.Map(ChecksumUrl, HttpStatusCode.OK, Encoding.ASCII.GetBytes("not a checksum"));

        var result = await _installer.DownloadAndVerifyAsync(
            InstallerUrl, ChecksumUrl, progress: null, CancellationToken.None);

        Assert.Null(result.InstallerPath);
        Assert.NotNull(result.ErrorMessage);
        Assert.False(File.Exists(_downloadPath));
    }

    [Fact]
    public async Task Download_HttpFailure_ReportsErrorWithoutThrowing()
    {
        _handler.Map(InstallerUrl, HttpStatusCode.NotFound, []);
        _handler.Map(ChecksumUrl, HttpStatusCode.OK,
            Encoding.ASCII.GetBytes(InstallerSha256));

        var result = await _installer.DownloadAndVerifyAsync(
            InstallerUrl, ChecksumUrl, progress: null, CancellationToken.None);

        Assert.Null(result.InstallerPath);
        Assert.NotNull(result.ErrorMessage);
    }

    [Theory]
    [InlineData("http://example.com/ClaudeMon-Setup-0.0.0-test.exe", ChecksumUrl)] // http installer
    [InlineData("file:///C:/evil/ClaudeMon-Setup-0.0.0-test.exe", ChecksumUrl)]    // non-web scheme
    [InlineData(InstallerUrl, "http://example.com/sum.sha256")]                    // http checksum
    [InlineData("not a url at all", ChecksumUrl)]                                  // malformed (must not throw)
    public async Task Download_NonHttpsUrls_AreRejected(string installerUrl, string checksumUrl)
    {
        // This path ends in executing the download — anything but absolute HTTPS is refused
        // outright, before any request is made.
        var result = await _installer.DownloadAndVerifyAsync(
            installerUrl, checksumUrl, progress: null, CancellationToken.None);

        Assert.Null(result.InstallerPath);
        Assert.NotNull(result.ErrorMessage);
        Assert.False(File.Exists(_downloadPath));
    }

    [Fact]
    public async Task Download_UnexpectedInstallerFileName_IsRejected()
    {
        // The temp-file name comes from the URL; anything that isn't a ClaudeMon-Setup-*.exe
        // (a mislabelled asset, a redirect gone wrong) must be refused outright.
        var result = await _installer.DownloadAndVerifyAsync(
            "https://example.com/evil.bin", ChecksumUrl, progress: null, CancellationToken.None);

        Assert.Null(result.InstallerPath);
        Assert.NotNull(result.ErrorMessage);
    }

    [Fact]
    public async Task Download_Cancellation_PropagatesAndCleansUp()
    {
        _handler.Map(InstallerUrl, HttpStatusCode.OK, InstallerBytes);
        _handler.Map(ChecksumUrl, HttpStatusCode.OK,
            Encoding.ASCII.GetBytes(InstallerSha256));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _installer.DownloadAndVerifyAsync(InstallerUrl, ChecksumUrl, null, cts.Token));

        Assert.False(File.Exists(_downloadPath));
    }

    [Fact]
    public async Task Download_ReportsProgress()
    {
        _handler.Map(InstallerUrl, HttpStatusCode.OK, InstallerBytes);
        _handler.Map(ChecksumUrl, HttpStatusCode.OK,
            Encoding.ASCII.GetBytes(InstallerSha256));

        // A plain collector rather than Progress<T> (which posts via a sync context and
        // wouldn't have run by the time the await returns).
        var reports = new List<double>();
        var progress = new CollectingProgress(reports);

        var result = await _installer.DownloadAndVerifyAsync(
            InstallerUrl, ChecksumUrl, progress, CancellationToken.None);

        Assert.NotNull(result.InstallerPath);
        Assert.NotEmpty(reports);
        Assert.Equal(1.0, reports[^1]);
    }

    [Theory]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", true)]
    [InlineData("0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF", true)] // case-insensitive hex
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef  ClaudeMon-Setup-0.6.0.exe", true)] // sha256sum format
    [InlineData("abc123", false)]              // too short
    [InlineData("not-hex-at-all", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData(null, false)]
    public void TryParseChecksum_HandlesFormats(string? text, bool expected)
    {
        var ok = UpdateInstaller.TryParseChecksum(text, out var hash);

        Assert.Equal(expected, ok);
        if (expected)
            Assert.Equal(64, hash.Length);
    }

    [Theory]
    [InlineData(true, "/MERGETASKS=\"startup\"")]
    [InlineData(false, "/MERGETASKS=\"!startup\"")]
    public void BuildInstallerArguments_PinsStartupTaskToCurrentState(bool enabled, string expectedTask)
    {
        // Load-bearing (issue #63): without the explicit /MERGETASKS a silent install applies
        // the task's checked-by-default and re-enables run-at-startup for users who turned it off.
        var args = UpdateInstaller.BuildInstallerArguments(enabled);

        Assert.Contains("/VERYSILENT", args);
        Assert.Contains("/SUPPRESSMSGBOXES", args);
        Assert.Contains("/NORESTART", args);
        Assert.Contains(expectedTask, args);
    }

    private sealed class CollectingProgress(List<double> reports) : IProgress<double>
    {
        public void Report(double value) => reports.Add(value);
    }

    private sealed class MockHttpHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, (HttpStatusCode Status, byte[] Body)> _responses = [];

        public void Map(string url, HttpStatusCode status, byte[] body) =>
            _responses[url] = (status, body);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var url = request.RequestUri!.AbsoluteUri;
            if (!_responses.TryGetValue(url, out var mapped))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

            var response = new HttpResponseMessage(mapped.Status)
            {
                Content = new ByteArrayContent(mapped.Body),
            };
            return Task.FromResult(response);
        }
    }
}
