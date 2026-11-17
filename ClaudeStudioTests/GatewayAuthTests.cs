using ClaudeStudioShared;
using Xunit;

namespace ClaudeStudioTests;

public class GatewayAuthTests
{
    [Theory]
    [InlineData("{\"env\":{\"ANTHROPIC_BASE_URL\":\"https://gateway.example\",\"ANTHROPIC_AUTH_TOKEN\":\"test-token\"}}")]
    [InlineData("{\"env\":{\"ANTHROPIC_API_KEY\":\"test-key\"}}")]
    public void Accepts_api_credentials_from_user_settings(string settingsJson)
    {
        Assert.True(GatewayAuth.HasApiCredentials(settingsJson));
    }

    [Theory]
    [InlineData("test-token", null)]
    [InlineData(null, "test-key")]
    public void Accepts_api_credentials_from_process_environment(string? authToken, string? apiKey)
    {
        Assert.True(GatewayAuth.HasApiCredentials(null, authToken, apiKey));
    }

    [Theory]
    [InlineData("{\"env\":{\"ANTHROPIC_AUTH_TOKEN\":\" \"}}")]
    [InlineData("{\"env\":{\"ANTHROPIC_BASE_URL\":\"https://gateway.example\"}}")]
    [InlineData("{\"env\":{\"OTHER_API_KEY\":\"test-key\"}}")]
    [InlineData("not json")]
    [InlineData(null)]
    public void Rejects_missing_or_empty_api_credentials(string? settingsJson)
    {
        Assert.False(GatewayAuth.HasApiCredentials(settingsJson));
    }
}
