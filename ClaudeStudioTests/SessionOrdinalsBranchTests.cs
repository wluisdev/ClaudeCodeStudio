using ClaudeStudioShared;
using Xunit;

namespace ClaudeStudioTests;

// FindBranchBoundaryLineIndex mirrors app.js's msgCounter (mixed user+assistant,
// counts every non-meta text-bearing bubble). Each case targets exactly one
// documented rule from the branch/rewind ordinal bug class (rodadas 11/12).
public class SessionOrdinalsBranchTests
{
    [Fact]
    public void Basic_ordinal_across_user_and_assistant_lines()
    {
        var lines = new[]
        {
            """{"type":"user","message":{"content":"first"}}""",
            """{"type":"assistant","message":{"content":"second"}}""",
            """{"type":"user","message":{"content":"third"}}""",
        };

        Assert.Equal(0, SessionOrdinals.FindBranchBoundaryLineIndex(lines, 0));
        Assert.Equal(1, SessionOrdinals.FindBranchBoundaryLineIndex(lines, 1));
        Assert.Equal(2, SessionOrdinals.FindBranchBoundaryLineIndex(lines, 2));
    }

    [Fact]
    public void IsMeta_line_is_skipped_and_does_not_consume_an_ordinal()
    {
        var lines = new[]
        {
            """{"type":"user","message":{"content":"real one"}}""",
            """{"type":"assistant","isMeta":true,"message":{"content":"skill expansion"}}""",
            """{"type":"assistant","message":{"content":"real two"}}""",
        };

        // Ordinal 1 must land on the 3rd line ("real two"), not the isMeta one.
        Assert.Equal(2, SessionOrdinals.FindBranchBoundaryLineIndex(lines, 1));
    }

    [Fact]
    public void Assistant_line_with_only_tool_use_has_no_text_and_is_skipped()
    {
        var lines = new[]
        {
            """{"type":"assistant","message":{"content":[{"type":"tool_use","name":"Read","input":{}}]}}""",
            """{"type":"user","message":{"content":"after tool"}}""",
        };

        Assert.Equal(1, SessionOrdinals.FindBranchBoundaryLineIndex(lines, 0));
    }

    [Fact]
    public void User_line_with_only_tool_result_has_no_text_and_is_skipped()
    {
        var lines = new[]
        {
            """{"type":"user","message":{"content":[{"type":"tool_result","content":"ok"}]}}""",
            """{"type":"assistant","message":{"content":"reply"}}""",
        };

        Assert.Equal(1, SessionOrdinals.FindBranchBoundaryLineIndex(lines, 0));
    }

    [Fact]
    public void Multiple_text_blocks_in_one_line_count_as_a_single_ordinal()
    {
        var lines = new[]
        {
            """{"type":"assistant","message":{"content":[{"type":"text","text":"A "},{"type":"tool_use","name":"Read","input":{}},{"type":"text","text":"B"}]}}""",
            """{"type":"user","message":{"content":"next"}}""",
        };

        Assert.Equal(0, SessionOrdinals.FindBranchBoundaryLineIndex(lines, 0));
        Assert.Equal(1, SessionOrdinals.FindBranchBoundaryLineIndex(lines, 1));
    }

    [Fact]
    public void User_and_assistant_ordinals_are_mixed_together()
    {
        var lines = new[]
        {
            """{"type":"user","message":{"content":"a"}}""",
            """{"type":"assistant","message":{"content":"b"}}""",
            """{"type":"user","message":{"content":"c"}}""",
            """{"type":"assistant","message":{"content":"d"}}""",
        };

        // Ordinal 2 is the 3rd line overall ("c"), not the 2nd user line's own index.
        Assert.Equal(2, SessionOrdinals.FindBranchBoundaryLineIndex(lines, 2));
    }

    [Fact]
    public void Non_user_assistant_types_are_skipped()
    {
        var lines = new[]
        {
            """{"type":"file-history-snapshot"}""",
            """{"type":"system","subtype":"init"}""",
            """{"type":"user","message":{"content":"only real one"}}""",
        };

        Assert.Equal(2, SessionOrdinals.FindBranchBoundaryLineIndex(lines, 0));
    }

    [Fact]
    public void Malformed_json_line_is_skipped_without_throwing()
    {
        var lines = new[]
        {
            """{"type":"user","message":{"content":"first"}}""",
            "not-json-at-all{{{",
            """{"type":"assistant","message":{"content":"second"}}""",
        };

        Assert.Equal(2, SessionOrdinals.FindBranchBoundaryLineIndex(lines, 1));
    }

    [Fact]
    public void Line_missing_type_property_is_skipped()
    {
        var lines = new[]
        {
            """{"message":{"content":"no type"}}""",
            """{"type":"user","message":{"content":"real"}}""",
        };

        Assert.Equal(1, SessionOrdinals.FindBranchBoundaryLineIndex(lines, 0));
    }

    [Fact]
    public void Lines_missing_message_or_content_are_skipped()
    {
        var lines = new[]
        {
            """{"type":"user"}""",
            """{"type":"assistant","message":{}}""",
            """{"type":"user","message":{"content":"real"}}""",
        };

        Assert.Equal(2, SessionOrdinals.FindBranchBoundaryLineIndex(lines, 0));
    }

    [Fact]
    public void Blank_and_whitespace_only_lines_are_skipped()
    {
        var lines = new[]
        {
            """{"type":"user","message":{"content":"first"}}""",
            "",
            "   ",
            """{"type":"assistant","message":{"content":"second"}}""",
        };

        Assert.Equal(3, SessionOrdinals.FindBranchBoundaryLineIndex(lines, 1));
    }

    [Fact]
    public void Out_of_range_msgIndex_returns_null()
    {
        var lines = new[] { """{"type":"user","message":{"content":"only one"}}""" };

        Assert.Null(SessionOrdinals.FindBranchBoundaryLineIndex(lines, 5));
    }

    [Fact]
    public void Empty_transcript_returns_null()
    {
        Assert.Null(SessionOrdinals.FindBranchBoundaryLineIndex(System.Array.Empty<string>(), 0));
    }

    [Fact]
    public void String_and_array_content_forms_are_both_handled()
    {
        var lines = new[]
        {
            """{"type":"user","message":{"content":"plain string"}}""",
            """{"type":"assistant","message":{"content":[{"type":"text","text":"array form"}]}}""",
        };

        Assert.Equal(1, SessionOrdinals.FindBranchBoundaryLineIndex(lines, 1));
    }

    [Fact]
    public void Negative_msgIndex_returns_null()
    {
        var lines = new[]
        {
            """{"type":"user","message":{"content":"a"}}""",
            """{"type":"assistant","message":{"content":"b"}}""",
        };

        Assert.Null(SessionOrdinals.FindBranchBoundaryLineIndex(lines, -1));
    }
}
