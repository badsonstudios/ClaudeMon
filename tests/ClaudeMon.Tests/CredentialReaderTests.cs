namespace ClaudeMon.Tests;

using ClaudeMon.Services;

public class CredentialReaderTests : IDisposable
{
    private readonly string _tempDir;

    public CredentialReaderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"claudemon-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    private string WriteTempFile(string content)
    {
        var path = Path.Combine(_tempDir, ".credentials.json");
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void Read_ValidCredentials_ReturnsSuccess()
    {
        var path = WriteTempFile("""
        {
            "claudeAiOauth": {
                "accessToken": "sk-ant-oat01-test-token",
                "refreshToken": "sk-ant-ort01-refresh",
                "expiresAt": 9999999999999,
                "scopes": ["user:inference"],
                "subscriptionType": "max",
                "rateLimitTier": "default_claude_max_5x"
            },
            "organizationUuid": "test-org-uuid"
        }
        """);

        var reader = new CredentialReader(path);
        var result = reader.Read();

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Credential);
        Assert.Equal("sk-ant-oat01-test-token", result.Credential.AccessToken);
        Assert.Equal("max", result.Credential.SubscriptionType);
        Assert.Equal("default_claude_max_5x", result.Credential.RateLimitTier);
    }

    [Fact]
    public void Read_MissingFile_ReturnsError()
    {
        var reader = new CredentialReader(Path.Combine(_tempDir, "nonexistent.json"));
        var result = reader.Read();

        Assert.False(result.IsSuccess);
        Assert.Contains("not found", result.Error);
    }

    [Fact]
    public void Read_ExpiredToken_ReturnsError()
    {
        var path = WriteTempFile("""
        {
            "claudeAiOauth": {
                "accessToken": "sk-ant-oat01-expired",
                "expiresAt": 1000000000000
            }
        }
        """);

        var reader = new CredentialReader(path);
        var result = reader.Read();

        Assert.False(result.IsSuccess);
        Assert.Contains("expired", result.Error);
    }

    [Fact]
    public void Read_EmptyAccessToken_ReturnsError()
    {
        var path = WriteTempFile("""
        {
            "claudeAiOauth": {
                "accessToken": "",
                "expiresAt": 9999999999999
            }
        }
        """);

        var reader = new CredentialReader(path);
        var result = reader.Read();

        Assert.False(result.IsSuccess);
        Assert.Contains("empty", result.Error);
    }

    [Fact]
    public void Read_MissingOAuthSection_ReturnsError()
    {
        var path = WriteTempFile("""
        {
            "organizationUuid": "test-uuid"
        }
        """);

        var reader = new CredentialReader(path);
        var result = reader.Read();

        Assert.False(result.IsSuccess);
        Assert.Contains("missing", result.Error);
    }

    [Fact]
    public void Read_InvalidJson_ReturnsError()
    {
        var path = WriteTempFile("this is not json");

        var reader = new CredentialReader(path);
        var result = reader.Read();

        Assert.False(result.IsSuccess);
        Assert.Contains("parse", result.Error);
    }

    [Fact]
    public void Read_ValidCredentials_ParsesScopes()
    {
        var path = WriteTempFile("""
        {
            "claudeAiOauth": {
                "accessToken": "test-token",
                "expiresAt": 9999999999999,
                "scopes": ["user:inference", "user:profile", "user:sessions:claude_code"]
            }
        }
        """);

        var reader = new CredentialReader(path);
        var result = reader.Read();

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Credential!.Scopes);
        Assert.Equal(3, result.Credential.Scopes.Length);
        Assert.Contains("user:inference", result.Credential.Scopes);
    }

    [Fact]
    public void IsExpired_FutureTimestamp_ReturnsFalse()
    {
        var path = WriteTempFile("""
        {
            "claudeAiOauth": {
                "accessToken": "test-token",
                "expiresAt": 9999999999999
            }
        }
        """);

        var reader = new CredentialReader(path);
        var result = reader.Read();

        Assert.True(result.IsSuccess);
        Assert.False(result.Credential!.IsExpired);
    }
}
