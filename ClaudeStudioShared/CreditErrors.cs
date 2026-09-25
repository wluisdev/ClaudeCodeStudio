namespace ClaudeStudioShared;

/// <summary>
/// Recognizes the CLI's "this model isn't in your plan and needs paid usage
/// credits" refusal, reported as a plain error string (e.g. "Fable 5.1 requires
/// usage credits. Switch to another model, or manage usage credits at ...").
/// </summary>
/// <remarks>
/// Unlike a context overflow, the session is still alive: the turn failed on the
/// way in because the chosen model needs credits the account doesn't have. The
/// fix is to pick a different model (or add credits), so a matched error is routed
/// to a dedicated card instead of a raw bubble, and the original text is kept
/// visible (it carries the account's own manage-credits link).
/// </remarks>
public static class CreditErrors
{
    /// <summary>
    /// The phrase the CLI uses when the selected model requires usage credits.
    /// Specific enough to match on its own (names credits, not any service a tool
    /// might have called).
    /// </summary>
    private const string RequiresCredits = "requires usage credits";

    /// <summary>
    /// True when <paramref name="text"/> reads like the selected model needing
    /// paid usage credits, rather than an ordinary turn error.
    /// </summary>
    public static bool IsOutOfCredits(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;

        return text!.ToLowerInvariant().Contains(RequiresCredits);
    }
}
