namespace ClaudeStudioShared;

public class ChatRequest
{
    public string Message { get; set; } = "";
    public string Model { get; set; } = "claude-sonnet-5";
    public string? Effort { get; set; }
    public string PermissionMode { get; set; } = "ask";
    public bool ResetSession { get; set; }
    public string? ResumeSessionId { get; set; }
    public string? WorkingDirectory { get; set; }
    public bool AutoResume { get; set; }
    public PermissionResponse? PermissionResponse { get; set; }

    // Answer to an AskUserQuestion control_request. The agent correlates the
    // tool_use_id back to the pending control_request's request_id and writes a
    // control_response to claude.stdin (the `--permission-prompt-tool stdio`
    // channel), which becomes the tool's real tool_result.
    public AskAnswer? AskAnswer { get; set; }

    // Reply to a diagnostics_request: the formatted VS Error List entries for the
    // edited file, which the agent forwards back to the blocked PostToolUse hook
    // (it surfaces them to claude as additionalContext).
    public DiagnosticsResponse? DiagnosticsResponse { get; set; }

    // Hard cancel: dispose the current claude.exe session so SendMessageAsync's
    // read loop unblocks and emits `done`. Without this, the extension's read
    // loop stays pending on ReadLineAsync forever (claude keeps streaming),
    // and the next AskStreamingAsync call hits "stream in use" exception.
    public bool CancelTurn { get; set; }

    // Context usage probe: sends a control_request{subtype:"get_context_usage"}
    // to the running claude and forwards the response back as a
    // "context-usage-result" chunk (Text = the inner usage JSON).
    public bool ContextUsage { get; set; }

    // Session title generation (V18): non-null → sends a control_request
    // {subtype:"generate_session_title", description:<this>, persist:false} and
    // forwards the generated title back as a "session-title-result" chunk.
    public string? SessionTitleDescription { get; set; }

    // MCP live-session probes (V20): status list and per-server reconnect,
    // forwarded back as "mcp-status-result" / "mcp-reconnect-result" chunks.
    public bool McpStatus { get; set; }
    public string? McpReconnectServer { get; set; }

    // Side question (V19): answered with the session's context but kept out of
    // the main transcript. Forwarded back as a "side-question-result" chunk.
    public string? SideQuestion { get; set; }

    // Native file rewind: ask the running claude (with checkpointing enabled) to
    // restore files to the state before a given user message. The agent sends a
    // control_request{subtype:"rewind_files"} on claude.stdin and forwards the
    // control_response back as a "rewind-result" chunk.
    public RewindRequest? RewindRequest { get; set; }

    // User-configurable claude.exe settings (V7) the agent writes into the
    // settings.json passed via --settings. Null = use claude's own defaults.
    public ClaudeSettings? ClaudeSettings { get; set; }

    // Explicit claude.exe location (D7), for installs not on PATH. Null →
    // FindClaudeExe searches PATH and the standard install locations.
    public string? CliPath { get; set; }

    // Known custom title of the session being resumed (U2) → spawn claude with
    // --name so the native custom-title line is re-persisted. Spawn-time hint
    // only: deliberately NOT part of the session key (a rename alone must not
    // force a respawn).
    public string? SessionName { get; set; }
}

public class ClaudeSettings
{
    // false → write attribution{commit:"",pr:""} to drop the Co-Authored-By /
    // PR attribution trailers. true (default) → leave claude's default on.
    public bool CoAuthoredBy { get; set; } = true;
    // Transcript retention in days; null = claude default (30).
    public int? CleanupPeriodDays { get; set; }
    // false → write autoCompactEnabled:false. true (default) → leave default on.
    public bool AutoCompact { get; set; } = true;

    // V6 permission rules — claude-style rule strings ("Bash(git *)", "Read").
    // Written to the generated settings.json (permissions{allow,ask,deny}) for
    // new sessions AND evaluated per-turn by the agent's permission pipe to
    // auto-decide hook prompts (deny > ask > allow).
    public List<string>? PermissionAllow { get; set; }
    public List<string>? PermissionAsk { get; set; }
    public List<string>? PermissionDeny { get; set; }
}

public class RewindRequest
{
    // The uuid of the target user message's JSONL entry (= user_message_id the
    // rewind_files control_request expects).
    public string UserMessageId { get; set; } = "";
    // Dry run only computes the diff stats (canRewind/filesChanged/insertions/
    // deletions) without touching the files; false actually reverts them.
    public bool DryRun { get; set; }
}

public class ChatChunk
{
    public string Type { get; set; } = "";
    public string Text { get; set; } = "";
    public string? Tool { get; set; }
    public string? ToolInput { get; set; }
    public string? ToolId { get; set; }
    public string? Cwd { get; set; }
}

public class PermissionResponse
{
    public string ToolUseId { get; set; } = "";
    public bool Allow { get; set; }
    public string? Reason { get; set; }

    // When set, adds the tool name to the agent's session allowlist so future
    // calls of this same tool auto-approve without prompting the UI.
    public string? AllowSession { get; set; }
}

public class DiagnosticsResponse
{
    // Correlates with the diagnostics_request (we reuse the tool_use_id).
    public string RequestId { get; set; } = "";
    // Formatted diagnostics block, or empty when the file has no errors/warnings.
    public string Text { get; set; } = "";
}

public class AskAnswer
{
    public string ToolUseId { get; set; } = "";
    // JSON object mapping each question text to the chosen answer string, e.g.
    // {"Which color?":"Red"}. Merged into the tool input's `answers` field of
    // the control_response. Multi-select answers are pre-joined with ", ".
    public string AnswersJson { get; set; } = "";
    // User dismissed the card without answering → respond deny so claude doesn't
    // hang waiting for the control_response.
    public bool Dismissed { get; set; }
}
