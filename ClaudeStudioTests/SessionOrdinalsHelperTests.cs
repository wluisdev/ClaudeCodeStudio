using System.Text.Json;
using ClaudeStudioShared;
using Xunit;

namespace ClaudeStudioTests;

public class SessionOrdinalsHelperTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void IsMetaLine_true_when_isMeta_is_boolean_true()
    {
        Assert.True(SessionOrdinals.IsMetaLine(Parse("""{"isMeta":true}""")));
    }

    [Fact]
    public void IsMetaLine_false_when_isMeta_is_boolean_false()
    {
        Assert.False(SessionOrdinals.IsMetaLine(Parse("""{"isMeta":false}""")));
    }

    [Fact]
    public void IsMetaLine_false_when_property_is_missing()
    {
        Assert.False(SessionOrdinals.IsMetaLine(Parse("{}")));
    }

    [Fact]
    public void IsMetaLine_false_when_isMeta_is_a_string_not_a_bool()
    {
        // Locks in the exact ValueKind == JsonValueKind.True contract —
        // "true" the string must NOT be treated the same as true the bool.
        Assert.False(SessionOrdinals.IsMetaLine(Parse("""{"isMeta":"true"}""")));
    }

    [Fact]
    public void IsMetaLine_false_when_isMeta_is_a_number()
    {
        Assert.False(SessionOrdinals.IsMetaLine(Parse("""{"isMeta":1}""")));
    }

    [Fact]
    public void ExtractText_returns_plain_string_content_as_is()
    {
        Assert.Equal("hello", SessionOrdinals.ExtractText(Parse("\"hello\"")));
    }

    [Fact]
    public void ExtractText_returns_text_from_a_single_block()
    {
        var content = Parse("""[{"type":"text","text":"hello"}]""");
        Assert.Equal("hello", SessionOrdinals.ExtractText(content));
    }

    [Fact]
    public void ExtractText_concatenates_multiple_text_blocks()
    {
        var content = Parse("""[{"type":"text","text":"A "},{"type":"text","text":"B"}]""");
        Assert.Equal("A B", SessionOrdinals.ExtractText(content));
    }

    [Fact]
    public void ExtractText_returns_null_when_only_tool_blocks_are_present()
    {
        var content = Parse("""[{"type":"tool_use","name":"Read","input":{}}]""");
        Assert.Null(SessionOrdinals.ExtractText(content));
    }

    [Fact]
    public void ExtractText_returns_empty_string_for_an_explicit_empty_text_block()
    {
        // A real, documented edge case: an empty text block is still a text
        // block ("" is present, distinct from "no text block at all" = null).
        var content = Parse("""[{"type":"text","text":""}]""");
        Assert.Equal("", SessionOrdinals.ExtractText(content));
    }

    [Fact]
    public void ExtractText_returns_null_for_content_that_is_neither_string_nor_array()
    {
        Assert.Null(SessionOrdinals.ExtractText(Parse("42")));
    }

    // ── IsHiddenReplayLine ─────────────────────────────────────

    [Fact]
    public void IsHiddenReplayLine_true_for_isMeta()
    {
        Assert.True(SessionOrdinals.IsHiddenReplayLine(Parse("""{"isMeta":true}""")));
    }

    [Fact]
    public void IsHiddenReplayLine_true_for_isCompactSummary()
    {
        // The /compact continuation summary — hidden so it doesn't render as a
        // giant user bubble AND doesn't consume a ⎇/⟲ ordinal.
        Assert.True(SessionOrdinals.IsHiddenReplayLine(
            Parse("""{"type":"user","isCompactSummary":true,"message":{"content":"This session is being continued…"}}""")));
    }

    [Fact]
    public void IsHiddenReplayLine_false_when_isCompactSummary_is_string_not_bool()
    {
        Assert.False(SessionOrdinals.IsHiddenReplayLine(
            Parse("""{"type":"user","isCompactSummary":"true","message":{"content":"x"}}""")));
    }

    [Fact]
    public void IsHiddenReplayLine_true_for_local_command_stdout_echo()
    {
        Assert.True(SessionOrdinals.IsHiddenReplayLine(
            Parse("""{"type":"user","message":{"content":"<local-command-stdout>Compacted </local-command-stdout>"}}""")));
    }

    [Fact]
    public void IsHiddenReplayLine_true_for_local_command_caveat_echo()
    {
        Assert.True(SessionOrdinals.IsHiddenReplayLine(
            Parse("""{"type":"user","message":{"content":"<local-command-caveat>Caveat…</local-command-caveat>"}}""")));
    }

    [Fact]
    public void IsHiddenReplayLine_false_for_a_slash_command_invocation()
    {
        // <command-name>/… stays visible (rendered as a chip), so it must NOT be
        // hidden — its response would otherwise be left with no visible trigger.
        Assert.False(SessionOrdinals.IsHiddenReplayLine(
            Parse("""{"type":"user","message":{"content":"<command-name>/compact</command-name>"}}""")));
    }

    [Fact]
    public void IsHiddenReplayLine_false_for_a_normal_user_message()
    {
        Assert.False(SessionOrdinals.IsHiddenReplayLine(
            Parse("""{"type":"user","message":{"content":"hello"}}""")));
    }

    [Fact]
    public void IsHiddenReplayLine_true_for_synthetic_no_response_requested()
    {
        // The CLI's post-/compact non-answer — pure noise on replay.
        Assert.True(SessionOrdinals.IsHiddenReplayLine(
            Parse("""{"type":"assistant","message":{"model":"<synthetic>","content":[{"type":"text","text":"No response requested."}]}}""")));
    }

    [Fact]
    public void IsHiddenReplayLine_false_for_synthetic_with_real_content()
    {
        // /cost, /context are also <synthetic> but carry real output — only the
        // exact "No response requested." sentinel is hidden.
        Assert.False(SessionOrdinals.IsHiddenReplayLine(
            Parse("""{"type":"assistant","message":{"model":"<synthetic>","content":[{"type":"text","text":"Total cost: $0.42"}]}}""")));
    }

    [Fact]
    public void IsHiddenReplayLine_false_for_a_real_assistant_with_the_same_text()
    {
        // A genuine (non-synthetic) assistant turn must never be hidden, even if
        // its text happens to match the sentinel.
        Assert.False(SessionOrdinals.IsHiddenReplayLine(
            Parse("""{"type":"assistant","message":{"model":"claude-sonnet-5","content":[{"type":"text","text":"No response requested."}]}}""")));
    }

    // ── Ordinals skip the hidden lines but keep command chips ───

    // A transcript with a compact summary and a stdout echo interleaved: both
    // must be skipped, while the /compact command line stays a visible bubble.
    private static readonly string[] MixedTranscript =
    {
        """{"type":"user","uuid":"u0","message":{"role":"user","content":"hi"}}""",
        """{"type":"assistant","message":{"role":"assistant","content":"hello"}}""",
        """{"type":"user","uuid":"u2","isCompactSummary":true,"message":{"role":"user","content":"summary"}}""",
        """{"type":"user","uuid":"u3","message":{"role":"user","content":"<command-name>/compact</command-name>"}}""",
        """{"type":"user","uuid":"u4","message":{"role":"user","content":"<local-command-stdout>Compacted</local-command-stdout>"}}""",
        """{"type":"user","uuid":"u5","message":{"role":"user","content":"next"}}""",
    };

    [Theory]
    [InlineData(0, 0)] // "hi"
    [InlineData(1, 1)] // "hello"
    [InlineData(2, 3)] // /compact chip (summary at index 2 was skipped)
    [InlineData(3, 5)] // "next" (stdout echo at index 4 was skipped)
    public void FindBranchBoundaryLineIndex_skips_hidden_lines_counts_command(int msgIndex, int expectedLine)
    {
        Assert.Equal(expectedLine, SessionOrdinals.FindBranchBoundaryLineIndex(MixedTranscript, msgIndex));
    }

    [Fact]
    public void FindBranchBoundaryLineIndex_returns_null_past_the_last_visible_bubble()
    {
        // Four visible bubbles (0-3); asking for a 5th returns null.
        Assert.Null(SessionOrdinals.FindBranchBoundaryLineIndex(MixedTranscript, 4));
    }

    [Theory]
    [InlineData(0, "u0")]
    [InlineData(1, "u3")] // the /compact command line is a counted user bubble
    [InlineData(2, "u5")]
    public void FindRewindUserUuid_skips_hidden_user_lines_keeps_command(int msgIndex, string expectedUuid)
    {
        var (uuid, total) = SessionOrdinals.FindRewindUserUuid(MixedTranscript, msgIndex);
        Assert.Equal(expectedUuid, uuid);
        Assert.Equal(3, total); // u0, u3, u5 — the summary (u2) and stdout (u4) don't count
    }
}
