namespace ClaudeMon.Models;

using System.Text.Json.Serialization;

/// <summary>
/// Claude Code's <c>~/.claude/.credentials.json</c>, as much of it as ClaudeMon knows about.
/// The file belongs to the CLI, not to us; ClaudeMon only ever consumes
/// <see cref="ClaudeAiOauth"/>, which is all
/// <see cref="ClaudeMon.Services.CredentialReader.Read"/> hands back.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="OrganizationUuid"/> is mapped on purpose and read by nobody. Its job is to make
/// the one fact that shapes <see cref="ClaudeMon.Services.CredentialReader.WriteBack"/> visible
/// at a glance: <c>claudeAiOauth</c> is <em>not</em> the whole file, so a token write-back has to
/// merge into what is already on disk rather than serialize a fresh object over it.
/// </para>
/// <para>
/// It is not, however, what keeps that field alive across a write-back — this record is never
/// serialized. <c>WriteBack</c> edits the parsed JSON tree in place, so every member survives
/// whether or not it appears here, including ones a future Claude Code release adds that this
/// type has never heard of. So deleting this property would lose no data. Serializing this
/// record over the file would, and that is the mistake the property is here to forestall.
/// The same holds for <see cref="OAuthCredential.Scopes"/>,
/// <see cref="OAuthCredential.SubscriptionType"/> and <see cref="OAuthCredential.RateLimitTier"/>,
/// which are likewise mapped for shape and unread today.
/// </para>
/// </remarks>
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

    /// <summary>
    /// True if the token has already expired or will expire within <paramref name="skew"/>.
    /// Used to refresh proactively a little ahead of the hard expiry.
    /// </summary>
    public bool WillExpireWithin(TimeSpan skew) => ExpiresAtUtc - DateTimeOffset.UtcNow < skew;

    public bool HasRefreshToken => !string.IsNullOrWhiteSpace(RefreshToken);
}
