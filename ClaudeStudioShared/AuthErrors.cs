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
    /// <summary>
    /// Specific enough to stand alone: both name Claude Code's own credentials rather
    /// than any service a tool might have called.
    /// </summary>
    private static readonly string[] Decisive = { "run /login", "invalid api key" };

    /// <summary>Words that say a credential went stale.</summary>
    private static readonly string[] Stale = { "expired", "could not be refreshed" };

    /// <summary>Words that say the failure is about signing in.</summary>
    private static readonly string[] Credential =
    {
        "oauth", "credential", "authenticate", "sign in", "log in", "login",
    };

    /// <summary>
    /// True when <paramref name="text"/> reads like the user's own session failing,
    /// rather than an ordinary turn error.
    /// </summary>
    /// <remarks>
    /// Deliberately narrow, because this runs over every error chunk and two of those
    /// carry arbitrary third-party text: the CLI's result string when a turn fails,
    /// and claude's raw stderr on an unexpected exit. Bare <c>unauthorized</c> and
    /// <c>authentication failed</c> used to match on their own, so an MCP server
    /// answering <c>401 Unauthorized</c>, or a curl inside a Bash turn, raised a
    /// "Session expired — sign in again" card for a failure that had nothing to do
    /// with the user's credentials, and demoted the real error to a hint line.
    /// <para>
    /// The asymmetry justifies the strictness: a miss costs nothing much — the error
    /// shows normally and ⌘ → Re-authenticate is still one click away — while a false
    /// positive sends the user to fix the wrong thing.
    /// </para>
    /// </remarks>
    public static bool IsAuthFailure(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;

        string t = text!.ToLowerInvariant();

        foreach (string phrase in Decisive)
        {
            if (t.Contains(phrase)) return true;
        }

        // Neither half is safe alone: "expired" shows up in caches and rate limits,
        // and "authenticate" shows up in any tool that talks to an API.
        return Any(t, Stale) && Any(t, Credential);
    }

    private static bool Any(string text, string[] needles)
    {
        foreach (string n in needles)
        {
            if (text.Contains(n)) return true;
        }

        return false;
    }
}
