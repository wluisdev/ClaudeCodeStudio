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
}
