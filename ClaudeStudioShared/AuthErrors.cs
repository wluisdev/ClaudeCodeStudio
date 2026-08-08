namespace ClaudeStudioShared;

/// <summary>
/// Recognizes the CLI's "you are not authenticated any more" failures.
/// </summary>
/// <remarks>
/// The pre-flight check in the tool window cannot see these coming: it reads
/// <c>oauthAccount</c> from <c>~/.claude.json</c>, which holds profile fields only —
/// no token, no expiry (the tokens live in the Windows Credential Manager). An
/// expired session therefore still looks signed in, the agent spawns, and the CLI
/// is the first thing to notice, reporting it as a plain error string.
/// <para>
/// Matching English message text is the only signal available, so the patterns stay
/// deliberately loose — two stable tokens rather than a whole sentence — and callers
/// must keep the original text visible, so a wrong match is still readable. The ⌘
/// "Re-authenticate" entry exists as the manual way out when wording drifts past
/// everything here.
/// </para>
/// </remarks>
public static class AuthErrors
{
    private static readonly string[] Phrases =
    {
        "run /login",
        "please log in",
        "invalid api key",
        "authentication failed",
        "failed to authenticate",
        "unauthorized",
    };

    /// <summary>
    /// True when <paramref name="text"/> reads like an authentication failure rather
    /// than an ordinary turn error.
    /// </summary>
    public static bool IsAuthFailure(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;

        string t = text!.ToLowerInvariant();

        // "expired" alone is far too common (rate limits, caches, temp files), so it
        // only counts next to something that names the credential itself.
        if (t.Contains("expired") &&
            (t.Contains("oauth") || t.Contains("authenticate") || t.Contains("session") || t.Contains("token")))
        {
            return true;
        }

        foreach (string phrase in Phrases)
        {
            if (t.Contains(phrase)) return true;
        }

        return false;
    }
}
