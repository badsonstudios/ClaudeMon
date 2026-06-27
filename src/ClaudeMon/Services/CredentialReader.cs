namespace ClaudeMon.Services;

using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeMon.Models;

public sealed class CredentialReader
{
    private readonly string _credentialPath;

    public CredentialReader(string? credentialPath = null)
    {
        _credentialPath = credentialPath
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".claude",
                ".credentials.json");
    }

    public CredentialResult Read()
    {
        if (!File.Exists(_credentialPath))
        {
            return CredentialResult.Failure(
                "Credentials file not found. Run 'claude' to authenticate.");
        }

        try
        {
            var json = File.ReadAllText(_credentialPath);
            var credentialFile = JsonSerializer.Deserialize<CredentialFile>(json);

            if (credentialFile?.ClaudeAiOauth is null)
            {
                return CredentialResult.Failure(
                    "Invalid credentials file: missing claudeAiOauth section.");
            }

            var oauth = credentialFile.ClaudeAiOauth;

            if (string.IsNullOrWhiteSpace(oauth.AccessToken))
            {
                return CredentialResult.Failure(
                    "Invalid credentials file: access token is empty.");
            }

            // An expired token is still returned: the caller decides whether to
            // refresh it (via the refresh token) before falling back to an
            // auth-error state. Only structural problems are treated as failures.
            return CredentialResult.Success(oauth);
        }
        catch (JsonException)
        {
            // Never interpolate the exception message: it can echo a fragment of the
            // file contents, which holds the on-disk access/refresh tokens.
            return CredentialResult.Failure("Failed to parse credentials file (invalid JSON).");
        }
        catch (IOException ex)
        {
            return CredentialResult.Failure($"Failed to read credentials file: {ex.Message}");
        }
    }

    /// <summary>
    /// Persists a refreshed access/refresh token (and its new expiry) back to the
    /// shared credentials file, updating only those three fields inside
    /// <c>claudeAiOauth</c> and preserving every other field. The write is atomic
    /// (temp file in the same directory, then replace) so a concurrent reader —
    /// the CLI or VS Code extension — never sees a half-written file. Failures are
    /// reported via the return value and otherwise swallowed: the refreshed token
    /// still serves the current poll from memory even if the write-back fails.
    /// </summary>
    public bool WriteBack(OAuthCredential refreshed)
    {
        // Unique temp name so a stale leftover can never be reused and concurrent
        // writers can't collide on it.
        var tempPath = $"{_credentialPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            var json = File.ReadAllText(_credentialPath);
            var root = JsonNode.Parse(json)?.AsObject();
            if (root?["claudeAiOauth"] is not JsonObject oauth)
            {
                return false;
            }

            oauth["accessToken"] = refreshed.AccessToken;
            oauth["refreshToken"] = refreshed.RefreshToken;
            oauth["expiresAt"] = refreshed.ExpiresAt;

            File.WriteAllText(tempPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

            // Atomic replace; preserves the destination file's existing ACLs (so a
            // user-only credentials file stays user-only). The file always exists
            // here — we just read a credential out of it — so there is no create path.
            File.Replace(tempPath, _credentialPath, destinationBackupFileName: null);

            return true;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            TryDeleteTemp(tempPath);
            return false;
        }
    }

    private static void TryDeleteTemp(string tempPath)
    {
        try
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort cleanup; leaving a temp file is not worth surfacing.
        }
    }
}

public record CredentialResult
{
    public bool IsSuccess { get; private init; }
    public OAuthCredential? Credential { get; private init; }
    public string? Error { get; private init; }

    public static CredentialResult Success(OAuthCredential credential) =>
        new() { IsSuccess = true, Credential = credential };

    public static CredentialResult Failure(string error) =>
        new() { IsSuccess = false, Error = error };
}
