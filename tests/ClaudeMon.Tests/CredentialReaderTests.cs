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
    public void Read_ExpiredToken_ReturnsCredentialForCallerToRefresh()
    {
        var path = WriteTempFile("""
        {
            "claudeAiOauth": {
                "accessToken": "sk-ant-oat01-expired",
                "refreshToken": "sk-ant-ort01-refresh",
                "expiresAt": 1000000000000
            }
        }
        """);

        var reader = new CredentialReader(path);
        var result = reader.Read();

        // Expiry is no longer a read failure: the caller decides whether to refresh.
        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Credential);
        Assert.True(result.Credential.IsExpired);
        Assert.True(result.Credential.HasRefreshToken);
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
    public void WriteBack_UpdatesTokensAndExpiry_PreservingOtherFields()
    {
        var path = WriteTempFile("""
        {
            "claudeAiOauth": {
                "accessToken": "old-access",
                "refreshToken": "old-refresh",
                "expiresAt": 1000000000000,
                "scopes": ["user:inference", "user:profile"],
                "subscriptionType": "max",
                "rateLimitTier": "default_claude_max_5x"
            },
            "organizationUuid": "keep-this-org"
        }
        """);
        var reader = new CredentialReader(path);

        var refreshed = new ClaudeMon.Models.OAuthCredential(
            AccessToken: "new-access",
            RefreshToken: "new-refresh",
            ExpiresAt: 9999999999999,
            Scopes: null,
            SubscriptionType: null,
            RateLimitTier: null);

        var ok = reader.WriteBack(refreshed);
        Assert.True(ok);

        // Re-read and confirm the three fields changed while everything else stayed.
        var result = reader.Read();
        Assert.True(result.IsSuccess);
        Assert.Equal("new-access", result.Credential!.AccessToken);
        Assert.Equal("new-refresh", result.Credential.RefreshToken);
        Assert.Equal(9999999999999, result.Credential.ExpiresAt);
        Assert.Equal("max", result.Credential.SubscriptionType);
        Assert.Equal("default_claude_max_5x", result.Credential.RateLimitTier);
        Assert.NotNull(result.Credential.Scopes);
        Assert.Equal(2, result.Credential.Scopes!.Length);

        // Untouched top-level field survives the rewrite.
        var raw = File.ReadAllText(path);
        Assert.Contains("keep-this-org", raw);
    }

    [Fact]
    public void WriteBack_MissingFile_ReturnsFalse()
    {
        var reader = new CredentialReader(Path.Combine(_tempDir, "nonexistent.json"));

        var refreshed = new ClaudeMon.Models.OAuthCredential(
            "a", "b", 9999999999999, null, null, null);

        Assert.False(reader.WriteBack(refreshed));
    }

    [Fact]
    public void WriteBack_MissingOAuthSection_ReturnsFalseAndLeavesFileIntact()
    {
        var path = WriteTempFile("""
        {
            "organizationUuid": "only-this"
        }
        """);
        var reader = new CredentialReader(path);

        var refreshed = new ClaudeMon.Models.OAuthCredential(
            "a", "b", 9999999999999, null, null, null);

        Assert.False(reader.WriteBack(refreshed));
        // File untouched; no temp litter left next to it.
        Assert.Contains("only-this", File.ReadAllText(path));
        Assert.Empty(Directory.GetFiles(_tempDir, "*.tmp"));
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
