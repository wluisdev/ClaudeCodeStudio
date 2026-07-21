using System;
using ClaudeStudioShared;
using Xunit;

namespace ClaudeStudioTests;

public class UsageParserTests
{
    [Fact]
    public void ParseLines_returns_null_when_there_is_no_sessionId()
    {
        var lines = new[]
        {
            """{"type":"assistant","message":{"model":"claude-sonnet-5","usage":{"input_tokens":10}}}""",
        };

        Assert.Null(UsageParser.ParseLines(lines));
    }

    [Fact]
    public void ParseLines_returns_null_when_there_are_zero_assistant_turns_with_usage()
    {
        var lines = new[]
        {
            """{"sessionId":"s1"}""",
            """{"type":"user","message":{"content":"hi"}}""",
        };

        Assert.Null(UsageParser.ParseLines(lines));
    }

    [Fact]
    public void ParseLines_extracts_sessionId_and_cwd_from_the_first_line_that_has_them()
    {
        var lines = new[]
        {
            """{"sessionId":"s1","cwd":"/first"}""",
            """{"sessionId":"s2","cwd":"/second","type":"assistant","message":{"model":"claude-sonnet-5","usage":{"input_tokens":10}}}""",
        };

        var result = UsageParser.ParseLines(lines);

        Assert.Equal("s1", result!.SessionId);
        Assert.Equal("/first", result.Cwd);
    }

    [Fact]
    public void ParseLines_tracks_first_and_last_timestamp_across_out_of_order_lines()
    {
        var lines = new[]
        {
            """{"sessionId":"s1","timestamp":"2026-01-15T10:00:00Z"}""",
            """{"timestamp":"2026-01-10T10:00:00Z","type":"assistant","message":{"model":"claude-sonnet-5","usage":{"input_tokens":5}}}""",
            """{"timestamp":"2026-01-20T10:00:00Z"}""",
        };

        var result = UsageParser.ParseLines(lines);

        Assert.Equal(new DateTime(2026, 1, 10, 10, 0, 0, DateTimeKind.Utc), result!.FirstTimestamp.ToUniversalTime());
        Assert.Equal(new DateTime(2026, 1, 20, 10, 0, 0, DateTimeKind.Utc), result.LastTimestamp.ToUniversalTime());
    }

    [Fact]
    public void ParseLines_skips_malformed_json_lines_without_throwing()
    {
        var lines = new[]
        {
            """{"sessionId":"s1"}""",
            "{{{not valid json",
            """{"type":"assistant","message":{"model":"claude-sonnet-5","usage":{"input_tokens":10}}}""",
        };

        var result = UsageParser.ParseLines(lines);

        Assert.NotNull(result);
        Assert.Equal(1, result!.TurnCount);
    }

    [Fact]
    public void ParseLines_skips_blank_and_whitespace_only_lines()
    {
        var lines = new[]
        {
            """{"sessionId":"s1"}""",
            "",
            "   ",
            """{"type":"assistant","message":{"model":"claude-sonnet-5","usage":{"input_tokens":10}}}""",
        };

        var result = UsageParser.ParseLines(lines);

        Assert.NotNull(result);
        Assert.Equal(1, result!.TurnCount);
    }

    [Fact]
    public void ParseLines_records_custom_title()
    {
        var lines = new[]
        {
            """{"sessionId":"s1"}""",
            """{"type":"custom-title","customTitle":"My Title"}""",
            """{"type":"assistant","message":{"model":"claude-sonnet-5","usage":{"input_tokens":10}}}""",
        };

        var result = UsageParser.ParseLines(lines);

        Assert.Equal("My Title", result!.NativeCustomTitle);
    }

    [Fact]
    public void ParseLines_records_ai_title()
    {
        var lines = new[]
        {
            """{"sessionId":"s1"}""",
            """{"type":"ai-title","aiTitle":"AI Title"}""",
            """{"type":"assistant","message":{"model":"claude-sonnet-5","usage":{"input_tokens":10}}}""",
        };

        var result = UsageParser.ParseLines(lines);

        Assert.Equal("AI Title", result!.NativeAiTitle);
    }

    [Fact]
    public void ParseLines_captures_first_user_text_as_preview_and_ignores_later_user_lines()
    {
        var lines = new[]
        {
            """{"sessionId":"s1"}""",
            """{"type":"user","message":{"content":"first question"}}""",
            """{"type":"user","message":{"content":"second question"}}""",
            """{"type":"assistant","message":{"model":"claude-sonnet-5","usage":{"input_tokens":10}}}""",
        };

        var result = UsageParser.ParseLines(lines);

        Assert.Equal("first question", result!.Preview);
    }

    [Fact]
    public void ParseLines_preview_truncates_to_60_chars_and_newlines_become_spaces()
    {
        var longText = "line one\nline two is quite a bit longer than sixty characters total";
        var userLine = """{"type":"user","message":{"content":""" + JsonQuote(longText) + "}}";
        var lines = new[]
        {
            """{"sessionId":"s1"}""",
            userLine,
            """{"type":"assistant","message":{"model":"claude-sonnet-5","usage":{"input_tokens":10}}}""",
        };

        var result = UsageParser.ParseLines(lines);

        Assert.Equal(60, result!.Preview.Length);
        Assert.DoesNotContain('\n', result.Preview);
    }

    [Fact]
    public void ParseLines_preview_extracts_from_array_content_text_block()
    {
        var lines = new[]
        {
            """{"sessionId":"s1"}""",
            """{"type":"user","message":{"content":[{"type":"text","text":"from array"}]}}""",
            """{"type":"assistant","message":{"model":"claude-sonnet-5","usage":{"input_tokens":10}}}""",
        };

        var result = UsageParser.ParseLines(lines);

        Assert.Equal("from array", result!.Preview);
    }

    [Fact]
    public void ParseLines_skips_synthetic_assistant_turns_entirely()
    {
        var lines = new[]
        {
            """{"sessionId":"s1"}""",
            """{"type":"assistant","message":{"model":"<synthetic>","usage":{"input_tokens":999}}}""",
            """{"type":"assistant","message":{"model":"claude-sonnet-5","usage":{"input_tokens":10}}}""",
        };

        var result = UsageParser.ParseLines(lines);

        Assert.Equal(1, result!.TurnCount);
        Assert.Equal(10, result.InputTokens);
        Assert.Equal("claude-sonnet-5", result.Model);
    }

    [Fact]
    public void ParseLines_model_reflects_the_last_seen_non_synthetic_assistant_turn()
    {
        var lines = new[]
        {
            """{"sessionId":"s1"}""",
            """{"type":"assistant","message":{"model":"claude-haiku-4-5","usage":{"input_tokens":10}}}""",
            """{"type":"assistant","message":{"model":"claude-opus-4-8","usage":{"input_tokens":10}}}""",
        };

        var result = UsageParser.ParseLines(lines);

        Assert.Equal("claude-opus-4-8", result!.Model);
    }

    [Fact]
    public void ParseLines_assistant_line_without_usage_property_is_not_counted_as_a_turn()
    {
        var lines = new[]
        {
            """{"sessionId":"s1"}""",
            """{"type":"assistant","message":{"model":"claude-sonnet-5"}}""",
            """{"type":"assistant","message":{"model":"claude-sonnet-5","usage":{"input_tokens":10}}}""",
        };

        var result = UsageParser.ParseLines(lines);

        Assert.Equal(1, result!.TurnCount);
    }

    [Fact]
    public void ParseLines_missing_individual_usage_fields_default_to_zero()
    {
        var lines = new[]
        {
            """{"sessionId":"s1"}""",
            """{"type":"assistant","message":{"model":"claude-sonnet-5","usage":{"input_tokens":10}}}""",
        };

        var result = UsageParser.ParseLines(lines);

        Assert.Equal(0, result!.OutputTokens);
        Assert.Equal(0, result.CacheReadTokens);
    }

    [Fact]
    public void ParseLines_cache_creation_breakdown_splits_into_5m_and_1h_buckets()
    {
        var lines = new[]
        {
            """{"sessionId":"s1"}""",
            """{"type":"assistant","message":{"model":"claude-sonnet-5","usage":{"input_tokens":1,"cache_creation_input_tokens":40,"cache_creation":{"ephemeral_5m_input_tokens":30,"ephemeral_1h_input_tokens":10}}}}""",
        };

        var result = UsageParser.ParseLines(lines);

        Assert.Equal(40, result!.CacheCreationTokens);
        Assert.Equal(30, result.Cache5mTokens);
        Assert.Equal(10, result.Cache1hTokens);
    }

    [Fact]
    public void ParseLines_missing_cache_creation_breakdown_falls_back_to_5m()
    {
        var lines = new[]
        {
            """{"sessionId":"s1"}""",
            """{"type":"assistant","message":{"model":"claude-sonnet-5","usage":{"input_tokens":1,"cache_creation_input_tokens":25}}}""",
        };

        var result = UsageParser.ParseLines(lines);

        Assert.Equal(25, result!.Cache5mTokens);
        Assert.Equal(0, result.Cache1hTokens);
    }

    [Fact]
    public void ParseLines_empty_cache_creation_breakdown_object_does_not_fall_back()
    {
        // Presence of the "cache_creation" key, even an empty object, is
        // enough to skip the flat-5m fallback branch entirely.
        var lines = new[]
        {
            """{"sessionId":"s1"}""",
            """{"type":"assistant","message":{"model":"claude-sonnet-5","usage":{"input_tokens":1,"cache_creation_input_tokens":15,"cache_creation":{}}}}""",
        };

        var result = UsageParser.ParseLines(lines);

        Assert.Equal(0, result!.Cache5mTokens);
        Assert.Equal(0, result.Cache1hTokens);
    }

    [Fact]
    public void ParseLines_cache_5m_and_1h_accumulate_across_multiple_turns()
    {
        var lines = new[]
        {
            """{"sessionId":"s1"}""",
            """{"type":"assistant","message":{"model":"claude-sonnet-5","usage":{"input_tokens":1,"cache_creation":{"ephemeral_5m_input_tokens":10,"ephemeral_1h_input_tokens":5}}}}""",
            """{"type":"assistant","message":{"model":"claude-sonnet-5","usage":{"input_tokens":1,"cache_creation":{"ephemeral_5m_input_tokens":20,"ephemeral_1h_input_tokens":15}}}}""",
        };

        var result = UsageParser.ParseLines(lines);

        Assert.Equal(30, result!.Cache5mTokens);
        Assert.Equal(20, result.Cache1hTokens);
    }

    [Fact]
    public void ParseLines_multiple_turns_accumulate_tokens_and_turn_count()
    {
        var lines = new[]
        {
            """{"sessionId":"s1"}""",
            """{"type":"assistant","message":{"model":"claude-sonnet-5","usage":{"input_tokens":10,"output_tokens":1,"cache_read_input_tokens":2}}}""",
            """{"type":"assistant","message":{"model":"claude-sonnet-5","usage":{"input_tokens":20,"output_tokens":3,"cache_read_input_tokens":4}}}""",
            """{"type":"assistant","message":{"model":"claude-sonnet-5","usage":{"input_tokens":30,"output_tokens":5,"cache_read_input_tokens":6}}}""",
        };

        var result = UsageParser.ParseLines(lines);

        Assert.Equal(3, result!.TurnCount);
        Assert.Equal(60, result.InputTokens);
        Assert.Equal(9, result.OutputTokens);
        Assert.Equal(12, result.CacheReadTokens);
    }

    [Fact]
    public void ParseLines_default_model_is_claude_sonnet_5_when_no_turn_ever_sets_a_model()
    {
        var lines = new[]
        {
            """{"sessionId":"s1"}""",
            """{"type":"assistant","message":{"usage":{"input_tokens":10}}}""",
        };

        var result = UsageParser.ParseLines(lines);

        Assert.Equal("claude-sonnet-5", result!.Model);
        Assert.Equal(1, result.TurnCount);
    }

    [Fact]
    public void ParseLines_cost_is_computed_end_to_end_via_Pricing_Calculate()
    {
        var lines = new[]
        {
            """{"sessionId":"s1"}""",
            """{"type":"assistant","message":{"model":"claude-sonnet-5","usage":{"input_tokens":100000,"output_tokens":20000}}}""",
        };

        var result = UsageParser.ParseLines(lines);

        Assert.Equal(0.6m, result!.Cost);
    }

    private static string JsonQuote(string s) => System.Text.Json.JsonSerializer.Serialize(s);
}
