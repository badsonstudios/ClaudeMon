namespace ClaudeMon.Tests;

using ClaudeMon.Services;

public class JsonlUsageParserTests
{
    // Builds a realistic assistant transcript line. Field shapes mirror what
    // Claude Code actually writes: top-level type/requestId/timestamp, usage
    // nested under message with snake_case token fields.
    private static string AssistantLine(
        string model = "claude-fable-5",
        string? msgId = "msg_abc",
        string? reqId = "req_xyz",
        long input = 10,
        long output = 20,
        long? cacheTotal = 100,
        string? cacheBreakdown = null,
        long cacheRead = 1000,
        string timestamp = "2026-07-19T14:30:00.000Z",
        string extraUsage = "")
    {
        var idPart = msgId is null ? "" : $"\"id\":\"{msgId}\",";
        var reqPart = reqId is null ? "" : $"\"requestId\":\"{reqId}\",";
        var cachePart = cacheTotal is null ? "" : $"\"cache_creation_input_tokens\":{cacheTotal},";
        var breakdownPart = cacheBreakdown is null ? "" : $"\"cache_creation\":{cacheBreakdown},";
        return "{\"type\":\"assistant\"," + reqPart
            + $"\"timestamp\":\"{timestamp}\",\"message\":{{" + idPart
            + $"\"model\":\"{model}\",\"usage\":{{\"input_tokens\":{input},\"output_tokens\":{output},"
            + cachePart + breakdownPart
            + $"\"cache_read_input_tokens\":{cacheRead}" + extraUsage + "}}}";
    }

    [Fact]
    public void ParseLine_AssistantWithUsage_ExtractsAllFields()
    {
        var entry = JsonlUsageParser.ParseLine(AssistantLine());

        Assert.NotNull(entry);
        Assert.Equal("claude-fable-5", entry.Model);
        Assert.Equal("msg_abc:req_xyz", entry.DedupeKey);
        Assert.Equal(10, entry.InputTokens);
        Assert.Equal(20, entry.OutputTokens);
        Assert.Equal(100, entry.CacheWrite5mTokens);
        Assert.Equal(0, entry.CacheWrite1hTokens);
        Assert.Equal(1000, entry.CacheReadTokens);
        Assert.Equal(new DateTimeOffset(2026, 7, 19, 14, 30, 0, TimeSpan.Zero), entry.Timestamp);
    }

    [Fact]
    public void ParseLine_CacheCreationBreakdown_SplitsFiveMinuteAndOneHour()
    {
        var line = AssistantLine(
            cacheTotal: 300,
            cacheBreakdown: """{"ephemeral_5m_input_tokens":120,"ephemeral_1h_input_tokens":180}""");

        var entry = JsonlUsageParser.ParseLine(line);

        Assert.NotNull(entry);
        Assert.Equal(120, entry.CacheWrite5mTokens);
        Assert.Equal(180, entry.CacheWrite1hTokens);
    }

    [Fact]
    public void ParseLine_NoCacheCreationObject_FallsBackToTotalAsFiveMinute()
    {
        var entry = JsonlUsageParser.ParseLine(AssistantLine(cacheTotal: 250));

        Assert.NotNull(entry);
        Assert.Equal(250, entry.CacheWrite5mTokens);
        Assert.Equal(0, entry.CacheWrite1hTokens);
    }

    [Fact]
    public void ParseLine_IterationsArray_NotDoubleCounted()
    {
        // usage.iterations mirrors the top-level totals per internal iteration —
        // only the top-level numbers may count.
        var line = AssistantLine(
            input: 10, output: 20,
            extraUsage: ""","iterations":[{"input_tokens":10,"output_tokens":20},{"input_tokens":10,"output_tokens":20}]""");

        var entry = JsonlUsageParser.ParseLine(line);

        Assert.NotNull(entry);
        Assert.Equal(10, entry.InputTokens);
        Assert.Equal(20, entry.OutputTokens);
    }

    [Theory]
    [InlineData("user")]
    [InlineData("summary")]
    [InlineData("system")]
    public void ParseLine_NonAssistantType_ReturnsNull(string type)
    {
        var line = $"{{\"type\":\"{type}\",\"timestamp\":\"2026-07-19T14:30:00Z\",\"message\":{{\"usage\":{{\"input_tokens\":5}}}}}}";
        Assert.Null(JsonlUsageParser.ParseLine(line));
    }

    [Fact]
    public void ParseLine_MissingUsage_ReturnsNull()
    {
        var line = """{"type":"assistant","timestamp":"2026-07-19T14:30:00Z","message":{"model":"claude-fable-5"}}""";
        Assert.Null(JsonlUsageParser.ParseLine(line));
    }

    [Fact]
    public void ParseLine_SyntheticModel_ReturnsNull()
    {
        Assert.Null(JsonlUsageParser.ParseLine(AssistantLine(model: "<synthetic>")));
    }

    [Fact]
    public void ParseLine_MissingTimestamp_ReturnsNull()
    {
        var line = """{"type":"assistant","message":{"model":"claude-fable-5","usage":{"input_tokens":5}}}""";
        Assert.Null(JsonlUsageParser.ParseLine(line));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("{\"type\":\"assistant\",\"truncated")]
    [InlineData("[1,2,3]")]
    [InlineData("42")]
    public void ParseLine_MalformedOrNonObject_ReturnsNull(string line)
    {
        Assert.Null(JsonlUsageParser.ParseLine(line));
    }

    [Fact]
    public void ParseLine_OneIdMissing_FallsBackToTheOther()
    {
        // Streaming duplicates repeat the same ids — a line with only one id
        // must still dedupe by it rather than over-counting its duplicates.
        var noMsgId = JsonlUsageParser.ParseLine(AssistantLine(msgId: null));
        var noReqId = JsonlUsageParser.ParseLine(AssistantLine(reqId: null));

        Assert.NotNull(noMsgId);
        Assert.Equal("req_xyz", noMsgId.DedupeKey);
        Assert.NotNull(noReqId);
        Assert.Equal("msg_abc", noReqId.DedupeKey);
    }

    [Fact]
    public void ParseLine_BothIdsMissing_YieldsNullDedupeKey()
    {
        var entry = JsonlUsageParser.ParseLine(AssistantLine(msgId: null, reqId: null));

        Assert.NotNull(entry);
        Assert.Null(entry.DedupeKey);
    }

    [Fact]
    public void ParseLine_Timestamp_ParsedAsUtc()
    {
        // A zone-less timestamp is assumed UTC (Claude Code writes trailing-Z
        // ISO-8601, but be tolerant of the variant without the Z).
        var entry = JsonlUsageParser.ParseLine(AssistantLine(timestamp: "2026-07-19T14:30:00"));

        Assert.NotNull(entry);
        Assert.Equal(TimeSpan.Zero, entry.Timestamp.Offset);
        Assert.Equal(new DateTimeOffset(2026, 7, 19, 14, 30, 0, TimeSpan.Zero), entry.Timestamp);
    }

    [Fact]
    public void ParseLine_Cwd_ExtractedWhenPresentNullWhenAbsent()
    {
        var withCwd = JsonlUsageParser.ParseLine(
            """{"type":"assistant","requestId":"req_1","cwd":"C:\\Projects\\ClaudeMon","timestamp":"2026-07-19T14:30:00Z","message":{"id":"msg_1","model":"claude-fable-5","usage":{"input_tokens":5}}}""");
        Assert.NotNull(withCwd);
        Assert.Equal(@"C:\Projects\ClaudeMon", withCwd.Cwd);

        var without = JsonlUsageParser.ParseLine(AssistantLine());
        Assert.NotNull(without);
        Assert.Null(without.Cwd);
    }

    [Fact]
    public void ParseLine_MissingTokenFields_DefaultToZero()
    {
        var line = """{"type":"assistant","requestId":"req_1","timestamp":"2026-07-19T14:30:00Z","message":{"id":"msg_1","model":"claude-fable-5","usage":{"output_tokens":7}}}""";

        var entry = JsonlUsageParser.ParseLine(line);

        Assert.NotNull(entry);
        Assert.Equal(0, entry.InputTokens);
        Assert.Equal(7, entry.OutputTokens);
        Assert.Equal(0, entry.CacheWrite5mTokens);
        Assert.Equal(0, entry.CacheReadTokens);
        Assert.Equal(7, entry.TotalTokens);
    }
}
