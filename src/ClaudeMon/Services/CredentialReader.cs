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
    /// the CLI or VS Code extension — never sees a half-written file. The refreshed
    /// token still serves the current poll from memory regardless of the outcome.
    /// <para>
    /// Refresh tokens rotate on every refresh, so if another client rotated the
    /// on-disk token between our <see cref="Read"/> and this write, our tokens are
    /// derived from a superseded lineage and overwriting would clobber the newer
    /// token. Pass the refresh token we refreshed <em>from</em> as
    /// <paramref name="expectedPreviousRefreshToken"/>: the write is skipped
    /// (<see cref="WriteBackOutcome.SupersededByAnotherClient"/>) when the on-disk
    /// token no longer matches it, so the next poll re-reads and uses the current
    /// token. Pass <c>null</c> to write unconditionally.
    /// </para>
    /// </summary>
    public WriteBackOutcome WriteBack(OAuthCredential refreshed, string? expectedPreviousRefreshToken = null)
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
                return WriteBackOutcome.Failed;
            }

            // Lost-update guard: bail if another client rotated the refresh token since we read it,
            // rather than clobbering the newer token with ours. Matching our own new token means it's
            // already persisted (e.g. a retry), so that still writes.
            if (expectedPreviousRefreshToken is not null)
            {
                var onDisk = oauth["refreshToken"] is JsonValue v && v.TryGetValue<string>(out var rt) ? rt : null;
                if (onDisk != expectedPreviousRefreshToken && onDisk != refreshed.RefreshToken)
                {
                    return WriteBackOutcome.SupersededByAnotherClient;
                }
            }

            oauth["accessToken"] = refreshed.AccessToken;
            oauth["refreshToken"] = refreshed.RefreshToken;
            oauth["expiresAt"] = refreshed.ExpiresAt;

            File.WriteAllText(tempPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

            // Atomic replace; preserves the destination file's existing ACLs (so a
            // user-only credentials file stays user-only). The file always exists
            // here — we just read a credential out of it — so there is no create path.
            File.Replace(tempPath, _credentialPath, destinationBackupFileName: null);

            return WriteBackOutcome.Written;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            TryDeleteTemp(tempPath);
            return WriteBackOutcome.Failed;
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

/// <summary>Outcome of <see cref="CredentialReader.WriteBack"/>.</summary>
public enum WriteBackOutcome
{
    /// <summary>The refreshed tokens were written to the credentials file.</summary>
    Written,

    /// <summary>Another client rotated the on-disk refresh token first; the file was left untouched.</summary>
    SupersededByAnotherClient,

    /// <summary>The write failed (file missing/locked, malformed, or access denied); the file is unchanged.</summary>
    Failed,
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
