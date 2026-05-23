namespace ClaudeMon.Services;

using System.Text.Json;
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

            if (oauth.IsExpired)
            {
                return CredentialResult.Failure(
                    "OAuth token has expired. Run 'claude' to re-authenticate.");
            }

            return CredentialResult.Success(oauth);
        }
        catch (JsonException ex)
        {
            return CredentialResult.Failure($"Failed to parse credentials file: {ex.Message}");
        }
        catch (IOException ex)
        {
            return CredentialResult.Failure($"Failed to read credentials file: {ex.Message}");
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
