using ClaudeStudioShared;
using Xunit;

namespace ClaudeStudioTests;

public class CreditErrorsTests
{
    // The wording the CLI returns when the selected model needs paid usage
    // credits — the signal that routes to the "choose another model" card
    // instead of a raw error bubble.
    [Theory]
    [InlineData("Fable 5.1 requires usage credits. Switch to another model, or manage usage credits at claude.ai/settings/usage?from=cc_cli_limit_message, to continue.")]
    [InlineData("Fable 5 requires usage credits. Switch to another model, or manage usage credits at claude.ai/settings/usage, to continue.")]
    public void Matches_the_requires_usage_credits_message(string text)
    {
        Assert.True(CreditErrors.IsOutOfCredits(text));
    }

    [Theory]
    [InlineData("REQUIRES USAGE CREDITS")]
    [InlineData("This model Requires Usage Credits to run")]
    public void Is_case_insensitive_and_tolerates_surrounding_text(string text)
    {
        Assert.True(CreditErrors.IsOutOfCredits(text));
    }

    // Ordinary turn failures reach the same error path but must not be routed to
    // the credit card.
    [Theory]
    [InlineData("Prompt is too long")]
    [InlineData("Rate limit exceeded, your quota resets in 3 hours")]
    [InlineData("Edit failed: string not found in file")]
    [InlineData("Request failed: 500 Internal Server Error")]
    public void Leaves_ordinary_errors_alone(string text)
    {
        Assert.False(CreditErrors.IsOutOfCredits(text));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Treats_empty_input_as_not_a_credit_error(string? text)
    {
        Assert.False(CreditErrors.IsOutOfCredits(text));
    }
}
