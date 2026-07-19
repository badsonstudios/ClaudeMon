namespace ClaudeMon.Services;

using System.Text.Json;
using ClaudeMon.Models;

/// <summary>
/// Parses single lines of a Claude Code transcript (JSONL) into
/// <see cref="LocalUsageEntry"/> records. Only assistant messages that carry
/// usage numbers count; everything else — other event types, synthetic
/// (non-API) messages, malformed JSON — parses to null and is skipped.
/// </summary>
internal static class JsonlUsageParser
{
    // Claude Code writes this placeholder model on locally injected messages
    // (errors, interruptions) that never hit the API and carry no real usage.
    private const string SyntheticModel = "<synthetic>";

    /// <summary>
    /// Parses one transcript line. Returns null for anything that is not a
    /// well-formed assistant message with usage — callers just skip nulls.
    /// </summary>
    public static LocalUsageEntry? ParseLine(string line)
    {
        // Tolerate a UTF-8 BOM on a file's first line — JsonDocument would
        // otherwise reject the whole line over one invisible character.
        line = line.TrimStart('\uFEFF');
        if (string.IsNullOrWhiteSpace(line))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;

            if (!root.TryGetProperty("type", out var type)
                || type.ValueKind != JsonValueKind.String
                || type.GetString() != "assistant")
                return null;

            if (!root.TryGetProperty("timestamp", out var ts)
                || ts.ValueKind != JsonValueKind.String
                || !DateTimeOffset.TryParse(ts.GetString(), null,
                    System.Globalization.DateTimeStyles.AssumeUniversal
                    | System.Globalization.DateTimeStyles.AdjustToUniversal,
                    out var timestamp))
                return null;

            if (!root.TryGetProperty("message", out var message)
                || message.ValueKind != JsonValueKind.Object
                || !message.TryGetProperty("usage", out var usage)
                || usage.ValueKind != JsonValueKind.Object)
                return null;

            var model = StringOrNull(message, "model");
            if (string.IsNullOrEmpty(model) || model == SyntheticModel)
                return null;

            // Dedupe key: streaming writes several lines per assistant message,
            // each repeating the same message id + request id and the same
            // usage. Prefer the pair; fall back to whichever id is present
            // (message ids are globally unique on their own) so a line missing
            // one id still dedupes instead of over-counting its duplicates.
            var messageId = StringOrNull(message, "id");
            var requestId = StringOrNull(root, "requestId");
            var dedupeKey = messageId is not null && requestId is not null
                ? $"{messageId}:{requestId}"
                : messageId ?? requestId;

            // Only the top-level usage numbers — usage.iterations mirrors these
            // totals per internal iteration and must never be summed on top.
            var input = TokenCount(usage, "input_tokens");
            var output = TokenCount(usage, "output_tokens");
            var cacheRead = TokenCount(usage, "cache_read_input_tokens");

            // Cache writes split by TTL when the breakdown is present; otherwise
            // the total is treated as 5-minute (the cheaper rate — fine for an
            // estimate, and the common case).
            long cache5m, cache1h;
            if (usage.TryGetProperty("cache_creation", out var breakdown)
                && breakdown.ValueKind == JsonValueKind.Object)
            {
                cache5m = TokenCount(breakdown, "ephemeral_5m_input_tokens");
                cache1h = TokenCount(breakdown, "ephemeral_1h_input_tokens");
            }
            else
            {
                cache5m = TokenCount(usage, "cache_creation_input_tokens");
                cache1h = 0;
            }

            return new LocalUsageEntry(
                timestamp, model, dedupeKey, input, output, cache5m, cache1h, cacheRead);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? StringOrNull(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;

    private static long TokenCount(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var el)
        && el.ValueKind == JsonValueKind.Number
        && el.TryGetInt64(out var value) && value > 0
            ? value
            : 0;
}