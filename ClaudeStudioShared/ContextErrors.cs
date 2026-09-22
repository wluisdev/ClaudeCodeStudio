namespace ClaudeStudioShared;

/// <summary>
/// Recognizes the CLI's "the conversation no longer fits in the model's context
/// window" failure, reported as a plain error string.
/// </summary>
/// <remarks>
/// Once a session grows past the model's prompt limit, every path that carries its
/// transcript forward (a normal next turn, <c>--continue</c>/auto-resume, or an
/// explicit <c>--resume</c>/fork) fails on the way in, before any real work: the
/// history alone is over the ceiling, so the size of the new message is irrelevant.
/// The only reliable recovery is a fresh session, so a matched error is routed to a
/// dedicated card instead of a dead-end bubble, and auto-resume is suppressed for the
/// next send so the dead session is not dragged back in a refail loop.
/// <para>
/// Matching the English message text is the only signal available. The phrase is
/// specific enough to stand alone (it names the prompt, not any service a tool might
/// have called), so a single decisive token is enough and stays narrow, and callers
/// keep the original text visible in case the wording ever drifts.
/// </para>
/// </remarks>
public static class ContextErrors
{
    /// <summary>
    /// The exact wording the CLI uses when the assembled prompt exceeds the model's
    /// context window. Specific enough to match on its own.
    /// </summary>
    private const string PromptTooLong = "prompt is too long";

    /// <summary>
    /// True when <paramref name="text"/> reads like the session's own context
    /// overflowing, rather than an ordinary turn error.
    /// </summary>
    public static bool IsContextOverflow(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;

        return text!.ToLowerInvariant().Contains(PromptTooLong);
    }
}
