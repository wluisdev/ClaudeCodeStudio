using ClaudeStudioShared;
using Xunit;

namespace ClaudeStudioTests;

public class AuthErrorsTests
{
    // The exact string that started this: an expired session sails past the
    // pre-flight IsSignedIn() check, so this text is the only thing that tells
    // the tool window to show the sign-in card instead of a dead-end bubble.
    [Fact]
    public void Matches_the_expired_oauth_session_message()
    {
        Assert.True(AuthErrors.IsAuthFailure(
            "Failed to authenticate: OAuth session expired and could not be refreshed"));
    }

    [Theory]
    [InlineData("Invalid API key · Please run /login")]
    [InlineData("Authentication failed")]
    [InlineData("Request failed: 401 Unauthorized")]
    [InlineData("Your token expired, please log in again")]
    public void Matches_the_other_shapes_the_cli_uses(string text)
    {
        Assert.True(AuthErrors.IsAuthFailure(text));
    }

    [Fact]
    public void Is_case_insensitive()
    {
        Assert.True(AuthErrors.IsAuthFailure("OAUTH SESSION EXPIRED"));
    }

    // "expired" on its own is the trap: plenty of ordinary turn errors carry it,
    // and hijacking one into a sign-in card would hide the real failure.
    [Theory]
    [InlineData("Rate limit exceeded, your quota resets in 3 hours")]
    [InlineData("The cache entry expired, retrying")]
    [InlineData("Edit failed: string not found in file")]
    [InlineData("Tool use was interrupted by the user")]
    public void Leaves_ordinary_errors_alone(string text)
    {
        Assert.False(AuthErrors.IsAuthFailure(text));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Treats_empty_input_as_not_an_auth_failure(string? text)
    {
        Assert.False(AuthErrors.IsAuthFailure(text));
    }
}
