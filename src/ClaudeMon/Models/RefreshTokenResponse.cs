namespace ClaudeMon.Models;

using System.Text.Json.Serialization;

/// <summary>
/// Response body from the OAuth token endpoint when refreshing an access token
/// (<c>grant_type=refresh_token</c>). The refresh token is rotated, so
/// <see cref="RefreshToken"/> is normally a fresh value that must be persisted.
/// </summary>
public record RefreshTokenResponse(
    [property: JsonPropertyName("access_token")] string? AccessToken,
    [property: JsonPropertyName("refresh_token")] string? RefreshToken,
    [property: JsonPropertyName("expires_in")] long ExpiresIn
);