namespace ClaudeStudioShared;

public class ChatRequest
{
    public string Message { get; set; } = "";
    public string Model { get; set; } = "claude-sonnet-4-6";
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

    // Hard cancel: dispose the current claude.exe session so SendMessageAsync's
    // read loop unblocks and emits `done`. Without this, the extension's read
    // loop stays pending on ReadLineAsync forever (claude keeps streaming),
    // and the next AskStreamingAsync call hits "stream in use" exception.
    public bool CancelTurn { get; set; }
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
