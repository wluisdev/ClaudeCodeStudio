using ClaudeStudioShared;
using Xunit;

namespace ClaudeStudioTests;

public class ContextErrorsTests
{
    // The exact string the CLI returns when the assembled prompt exceeds the
    // model's context window — the only signal that tells the tool window to show
    // the "start a new session" card instead of a dead-end error bubble.
    [Fact]
    public void Matches_the_prompt_too_long_message()
    {
        Assert.True(ContextErrors.IsContextOverflow("Prompt is too long"));
    }

    [Theory]
    [InlineData("PROMPT IS TOO LONG")]
    [InlineData("Error: prompt is too long")]
    [InlineData("The prompt is too long for the current model")]
    public void Is_case_insensitive_and_tolerates_surrounding_text(string text)
    {
        Assert.True(ContextErrors.IsContextOverflow(text));
    }

    // These reach the same error path but are ordinary turn failures, not a full
    // context window — routing them to the "new session" card would send the user
    // to throw away a healthy session for the wrong reason.
    [Theory]
    [InlineData("Rate limit exceeded, your quota resets in 3 hours")]
    [InlineData("Edit failed: string not found in file")]
    [InlineData("Tool use was interrupted by the user")]
    [InlineData("Request failed: 500 Internal Server Error")]
    public void Leaves_ordinary_errors_alone(string text)
    {
        Assert.False(ContextErrors.IsContextOverflow(text));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Treats_empty_input_as_not_an_overflow(string? text)
    {
        Assert.False(ContextErrors.IsContextOverflow(text));
    }
}
