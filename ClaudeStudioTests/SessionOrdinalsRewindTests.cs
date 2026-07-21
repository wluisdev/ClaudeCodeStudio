using ClaudeStudioShared;
using Xunit;

namespace ClaudeStudioTests;

// FindRewindUserUuid mirrors app.js's userMsgCounter (user-only, no early
// stop — the whole file is always walked before bounds-checking, matching
// today's HandleRewindAsync). Deliberately a different ordinal scheme than
// the branch boundary above — same isMeta/text rules, but assistant lines
// never enter the list at all, and a real (non-meta, text-bearing) user line
// can still be excluded for lacking a uuid.
public class SessionOrdinalsRewindTests
{
    [Fact]
    public void Basic_nth_user_uuid()
    {
        var lines = new[]
        {
            """{"type":"user","uuid":"u1","message":{"content":"first"}}""",
            """{"type":"user","uuid":"u2","message":{"content":"second"}}""",
        };

        Assert.Equal("u1", SessionOrdinals.FindRewindUserUuid(lines, 0).uuid);
        Assert.Equal("u2", SessionOrdinals.FindRewindUserUuid(lines, 1).uuid);
    }

    [Fact]
    public void Assistant_lines_are_ignored_entirely()
    {
        var lines = new[]
        {
            """{"type":"user","uuid":"u1","message":{"content":"a"}}""",
            """{"type":"assistant","message":{"content":"reply"}}""",
            """{"type":"user","uuid":"u2","message":{"content":"b"}}""",
        };

        Assert.Equal("u2", SessionOrdinals.FindRewindUserUuid(lines, 1).uuid);
    }

    [Fact]
    public void IsMeta_user_line_is_skipped()
    {
        var lines = new[]
        {
            """{"type":"user","uuid":"u1","message":{"content":"real"}}""",
            """{"type":"user","isMeta":true,"uuid":"u2","message":{"content":"caveat"}}""",
            """{"type":"user","uuid":"u3","message":{"content":"real2"}}""",
        };

        // Index 1 must land on "u3", not the isMeta "u2".
        Assert.Equal("u3", SessionOrdinals.FindRewindUserUuid(lines, 1).uuid);
    }

    [Fact]
    public void Tool_result_only_user_line_is_skipped()
    {
        // This is the key regression scenario: a type:"user" line that's
        // actually a tool_result echo, not a real chat message.
        var lines = new[]
        {
            """{"type":"user","uuid":"u1","message":{"content":"real"}}""",
            """{"type":"user","uuid":"u2","message":{"content":[{"type":"tool_result","content":"ok"}]}}""",
            """{"type":"user","uuid":"u3","message":{"content":"real2"}}""",
        };

        Assert.Equal("u3", SessionOrdinals.FindRewindUserUuid(lines, 1).uuid);
    }

    [Fact]
    public void User_line_missing_uuid_is_excluded_without_leaving_a_gap()
    {
        var lines = new[]
        {
            """{"type":"user","message":{"content":"real, no uuid field"}}""",
            """{"type":"user","uuid":"abc","message":{"content":"second real"}}""",
        };

        Assert.Equal("abc", SessionOrdinals.FindRewindUserUuid(lines, 0).uuid);
    }

    [Fact]
    public void Multiple_text_blocks_in_one_line_count_as_a_single_entry()
    {
        var lines = new[]
        {
            """{"type":"user","uuid":"u1","message":{"content":[{"type":"text","text":"A "},{"type":"text","text":"B"}]}}""",
        };

        Assert.Equal("u1", SessionOrdinals.FindRewindUserUuid(lines, 0).uuid);
    }

    [Fact]
    public void Malformed_json_line_is_skipped_without_throwing()
    {
        var lines = new[]
        {
            """{"type":"user","uuid":"u1","message":{"content":"first"}}""",
            "{{{not valid json",
            """{"type":"user","uuid":"u2","message":{"content":"second"}}""",
        };

        Assert.Equal("u2", SessionOrdinals.FindRewindUserUuid(lines, 1).uuid);
    }

    [Fact]
    public void Out_of_range_returns_null_uuid_with_correct_total_count()
    {
        var lines = new[] { """{"type":"user","uuid":"u1","message":{"content":"only one"}}""" };

        var (uuid, totalCount) = SessionOrdinals.FindRewindUserUuid(lines, 3);

        Assert.Null(uuid);
        Assert.Equal(1, totalCount);
    }

    [Fact]
    public void Negative_msgIndex_returns_null()
    {
        var lines = new[] { """{"type":"user","uuid":"u1","message":{"content":"a"}}""" };

        Assert.Null(SessionOrdinals.FindRewindUserUuid(lines, -1).uuid);
    }

    [Fact]
    public void No_user_lines_at_all_returns_null()
    {
        var lines = new[] { """{"type":"assistant","message":{"content":"only assistant"}}""" };

        var (uuid, totalCount) = SessionOrdinals.FindRewindUserUuid(lines, 0);

        Assert.Null(uuid);
        Assert.Equal(0, totalCount);
    }
}
