namespace ClaudeMon.Services;

using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ClaudeMon.Models;

/// <summary>Per-token list prices for one model, in USD per million tokens.</summary>
public sealed record ModelPricing(
    [property: JsonPropertyName("input")] double InputPerMTok,
    [property: JsonPropertyName("output")] double OutputPerMTok,
    [property: JsonPropertyName("cacheWrite5m")] double CacheWrite5mPerMTok,
    [property: JsonPropertyName("cacheWrite1h")] double CacheWrite1hPerMTok,
    [property: JsonPropertyName("cacheRead")] double CacheReadPerMTok)
{
    public double CostUsd(LocalUsageEntry e) =>
        (e.InputTokens * InputPerMTok
         + e.OutputTokens * OutputPerMTok
         + e.CacheWrite5mTokens * CacheWrite5mPerMTok
         + e.CacheWrite1hTokens * CacheWrite1hPerMTok
         + e.CacheReadTokens * CacheReadPerMTok) / 1_000_000.0;
}

/// <summary>
/// The bundled model-pricing table (Resources/model-pricing.json, embedded so it
/// can't go missing at runtime). Model ids from transcripts are resolved by
/// normalizing away provider prefixes and date suffixes, then exact match, then
/// longest prefix at a '-' boundary — so "claude-opus-4-8-fast" finds
/// "claude-opus-4-8" but "claude-sonnet-4-6" is not swallowed by
/// "claude-sonnet-4". Unknown models resolve to null; callers show tokens with
/// no cost rather than guessing.
/// </summary>
public sealed class PricingTable
{
    private static readonly Regex DateSuffix = new(@"-20\d{6}$", RegexOptions.Compiled);

    private readonly Dictionary<string, ModelPricing> _models;

    public PricingTable(Dictionary<string, ModelPricing> models)
    {
        _models = new Dictionary<string, ModelPricing>(models, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Loads the embedded table; a load failure yields an empty table (every
    /// model unpriced) rather than an exception, so a bad resource can never
    /// keep the app from starting.
    /// </summary>
    public static PricingTable LoadEmbedded(Logger? logger = null)
    {
        try
        {
            var assembly = typeof(PricingTable).Assembly;
            var name = assembly.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("model-pricing.json", StringComparison.OrdinalIgnoreCase));
            if (name is null)
                throw new InvalidOperationException("embedded pricing resource not found");

            using var stream = assembly.GetManifestResourceStream(name)!;
            var file = JsonSerializer.Deserialize<PricingFile>(stream);
            // file.Models is null (not empty) when the JSON lacks a "models"
            // property — guard it, or the ctor throws past the catch filter.
            if (file?.Models is null)
                throw new InvalidOperationException("pricing resource has no 'models' table");
            return new PricingTable(file.Models);
        }
        catch (Exception ex) when (ex is InvalidOperationException or JsonException or IOException)
        {
            logger?.Warn($"Could not load the bundled pricing table: {ex.Message}. Costs will show as unavailable.");
            return new PricingTable(new Dictionary<string, ModelPricing>());
        }
    }

    /// <summary>Resolves a transcript model id to its pricing, or null when unknown.</summary>
    public ModelPricing? Resolve(string modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
            return null;

        var id = Normalize(modelId);
        if (_models.TryGetValue(id, out var exact))
            return exact;

        ModelPricing? best = null;
        var bestLength = 0;
        foreach (var (key, pricing) in _models)
        {
            if (key.Length > bestLength
                && id.Length > key.Length
                && id[key.Length] == '-'
                && id.StartsWith(key, StringComparison.OrdinalIgnoreCase)
                && !IsVersionSuffix(id.AsSpan(key.Length + 1)))
            {
                best = pricing;
                bestLength = key.Length;
            }
        }

        return best;
    }

    // A purely numeric suffix ("claude-opus-4-9" against "claude-opus-4") means
    // a NEW model version, not a variant of the matched one — and versions
    // change price. Refusing the match makes an unknown version show tokens
    // with no cost instead of a confidently wrong number at the old rate.
    // Non-numeric suffixes ("-fast") are serving variants of the same model.
    private static bool IsVersionSuffix(ReadOnlySpan<char> suffix)
    {
        foreach (var c in suffix)
        {
            if (c != '-' && !char.IsAsciiDigit(c))
                return false;
        }
        return true;
    }

    // "anthropic.claude-opus-4-5-20251101@extra" → "claude-opus-4-5":
    // Bedrock's provider prefix, Vertex's @-suffix, and the API's date suffix
    // are all packaging around the same model id.
    internal static string Normalize(string modelId)
    {
        var id = modelId.Trim().ToLowerInvariant();

        if (id.StartsWith("anthropic.", StringComparison.Ordinal))
            id = id["anthropic.".Length..];

        var at = id.IndexOf('@');
        if (at >= 0)
            id = id[..at];

        return DateSuffix.Replace(id, "");
    }

    private sealed record PricingFile(
        [property: JsonPropertyName("models")] Dictionary<string, ModelPricing> Models);
}
