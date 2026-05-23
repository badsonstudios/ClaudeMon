namespace ClaudeMon.Models;

using System.Text.Json.Serialization;

public record CredentialFile(
    [property: JsonPropertyName("claudeAiOauth")] OAuthCredential? ClaudeAiOauth,
    [property: JsonPropertyName("organizationUuid")] string? OrganizationUuid
);

public record OAuthCredential(
    [property: JsonPropertyName("accessToken")] string AccessToken,
    [property: JsonPropertyName("refreshToken")] string? RefreshToken,
    [property: JsonPropertyName("expiresAt")] long ExpiresAt,
    [property: JsonPropertyName("scopes")] string[]? Scopes,
    [property: JsonPropertyName("subscriptionType")] string? SubscriptionType,
    [property: JsonPropertyName("rateLimitTier")] string? RateLimitTier
)
{
    public bool IsExpired => DateTimeOffset.FromUnixTimeMilliseconds(ExpiresAt) < DateTimeOffset.UtcNow;

    public DateTimeOffset ExpiresAtUtc => DateTimeOffset.FromUnixTimeMilliseconds(ExpiresAt);
}
