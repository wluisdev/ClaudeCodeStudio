namespace ClaudeVsShared;

public class ChatRequest
{
    public string Message { get; set; } = "";
    public string Model { get; set; } = "claude-sonnet-4-6";
    public string? Effort { get; set; }
    public string PermissionMode { get; set; } = "yolo";
    public bool ResetSession { get; set; }
    public string? ResumeSessionId { get; set; }
    public string? WorkingDirectory { get; set; }
    public bool AutoResume { get; set; }
}

public class ChatChunk
{
    public string Type { get; set; } = "";
    public string Text { get; set; } = "";
}
