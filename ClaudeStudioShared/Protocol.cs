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
}

public class ChatChunk
{
    public string Type { get; set; } = "";
    public string Text { get; set; } = "";
    public string? Tool { get; set; }
    public string? ToolInput { get; set; }
    public string? ToolId { get; set; }
}

public class PermissionResponse
{
    public string ToolUseId { get; set; } = "";
    public bool Allow { get; set; }
    public string? Reason { get; set; }
}
