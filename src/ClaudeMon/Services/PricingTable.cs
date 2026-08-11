namespace ClaudeMon.Services;

using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ClaudeMon.Models;

/// <summary>
/// A rate set that applied to usage up to and including <see cref="Through"/>
/// (a UTC calendar day), for a model whose published price changed on a known
/// date — an introductory rate, say. Keeping the old rate alongside the new one
/// means tokens spent inside the window stay priced at what they actually cost
/// once the window closes, and the changeover happens on the date itself rather
/// than whenever the next release ships.
/// </summary>
public sealed record DatedRate(
    [property: JsonPropertyName("through")] DateOnly Through,
    [property: JsonPropertyName("input")] double InputPerMTok,
    [property: JsonPropertyName("output")] double OutputPerMTok,
    [property: JsonPropertyName("cacheWrite5m")] double CacheWrite5mPerMTok,
    [property: JsonPropertyName("cacheWrite1h")] double CacheWrite1hPerMTok,
    [property: JsonPropertyName("cacheRead")] double CacheReadPerMTok);

/// <summary>
/// Per-token list prices for one model, in USD per million tokens. The top-level
/// rates are the standing ones; <see cref="PriorRates"/> holds any earlier,
/// date-bounded rate sets (see <see cref="DatedRate"/>), and is normally absent.
/// </summary>
public sealed record ModelPricing(
    [property: JsonPropertyName("input")] double InputPerMTok,
    [property: JsonPropertyName("output")] double OutputPerMTok,
    [property: JsonPropertyName("cacheWrite5m")] double CacheWrite5mPerMTok,
    [property: JsonPropertyName("cacheWrite1h")] double CacheWrite1hPerMTok,
    [property: JsonPropertyName("cacheRead")] double CacheReadPerMTok,
    [property: JsonPropertyName("priorRates")] IReadOnlyList<DatedRate>? PriorRates = null)
{
    /// <summary>Cost of one entry, at the rates in force on the entry's own UTC day.</summary>
    public double CostUsd(LocalUsageEntry e)
    {
        var (input, output, write5m, write1h, read) = RatesOn(e.Timestamp);
        return (e.InputTokens * input
                + e.OutputTokens * output
                + e.CacheWrite5mTokens * write5m
                + e.CacheWrite1hTokens * write1h
                + e.CacheReadTokens * read) / 1_000_000.0;
    }

    // The narrowest (earliest-ending) window that still covers the day wins, so
    // successive rate changes can be listed in any order. Nothing covering it —
    // the usual case — falls through to the standing rates.
    private (double Input, double Output, double Write5m, double Write1h, double Read) RatesOn(
        DateTimeOffset when)
    {
        DatedRate? match = null;
        if (PriorRates is not null)
        {
            var day = DateOnly.FromDateTime(when.UtcDateTime);
            foreach (var rate in PriorRates)
            {
                if (day <= rate.Through && (match is null || rate.Through < match.Through))
                    match = rate;
            }
        }

        return match is null
            ? (InputPerMTok, OutputPerMTok, CacheWrite5mPerMTok, CacheWrite1hPerMTok, CacheReadPerMTok)
            : (match.InputPerMTok, match.OutputPerMTok, match.CacheWrite5mPerMTok,
               match.CacheWrite1hPerMTok, match.CacheReadPerMTok);
    }
}

/// <summary>
/// The bundled model-pricing table (Resources/model-pricing.json, embedded so it
/// can't go missing at runtime). Model ids from transcripts are resolved by
/// normalizing away provider prefixes, request-tier tags and date suffixes,
/// then exact match, then
/// longest prefix at a '-' boundary — so "claude-opus-4-8-fast" finds
/// "claude-opus-4-8" but "claude-sonnet-4-6" is not swallowed by
/// "claude-sonnet-4". Unknown models resolve to null; callers show tokens with
/// no cost rather than guessing.
/// </summary>
public sealed class PricingTable
{
    private static readonly Regex DateSuffix = new(@"-20\d{6}$", RegexOptions.Compiled);
    private static readonly Regex BracketedTag = new(@"\[[^\]]*\]", RegexOptions.Compiled);

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
    // Bedrock's provider prefix, Vertex's @-suffix, the API's date suffix, and
    // a bracketed request-tier tag ("claude-opus-5[1m]", the long-context tier)
    // are all packaging around the same model id. The 1M context window is
    // included at standard rates on every model that offers it, so the tier tag
    // carries no price of its own — dropping it is what keeps those tokens
    // priced instead of falling through to the unpriced path.
    internal static string Normalize(string modelId)
    {
        var id = modelId.Trim().ToLowerInvariant();

        if (id.StartsWith("anthropic.", StringComparison.Ordinal))
            id = id["anthropic.".Length..];

        var at = id.IndexOf('@');
        if (at >= 0)
            id = id[..at];

        id = BracketedTag.Replace(id, "");

        return DateSuffix.Replace(id, "");
    }

    private sealed record PricingFile(
        [property: JsonPropertyName("models")] Dictionary<string, ModelPricing> Models);
}
