using System;
using System.Text.Json;

namespace ClaudeStudioShared;

/// <summary>
/// Detects API-key authentication configured for Claude Code without exposing the
/// credential values to callers.
/// </summary>
public static class GatewayAuth
{
    public static bool HasApiCredentials(
        string? userSettingsJson,
        string? authToken = null,
        string? apiKey = null)
    {
        if (HasValue(authToken) || HasValue(apiKey)) return true;
        if (string.IsNullOrWhiteSpace(userSettingsJson)) return false;

        try
        {
            using var doc = JsonDocument.Parse(userSettingsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("env", out var env) ||
                env.ValueKind != JsonValueKind.Object)
                return false;

            return HasString(env, "ANTHROPIC_AUTH_TOKEN") || HasString(env, "ANTHROPIC_API_KEY");
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool HasString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String &&
        HasValue(value.GetString());

    private static bool HasValue(string? value) => !string.IsNullOrWhiteSpace(value);
}
