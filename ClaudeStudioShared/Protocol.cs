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
