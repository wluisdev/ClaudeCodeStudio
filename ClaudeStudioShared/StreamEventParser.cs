using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace ClaudeStudioShared;

public sealed class StreamEventState
{
    public string? SessionId;
    public string? LastActiveModel;
    public bool ThinkingActive;

    // Text of the last synthetic assistant message emitted this turn. The CLI
    // surfaces some API errors (e.g. "Prompt is too long") BOTH as a synthetic
    // assistant message and as a terminal is_error result carrying the same
    // string; without this, both get appended to the same bubble and the text
    // renders doubled. Set when a synthetic text chunk is emitted, checked in
    // the result is_error branch, cleared at each result (turn boundary).
    public string? LastSyntheticText;
}

public enum StreamEventOutcome { Continue, Done }

public sealed class StreamEventResult
{
    public List<ChatChunk> Chunks { get; } = new();
    public StreamEventOutcome Outcome { get; set; } = StreamEventOutcome.Continue;
    public (string ToolUseId, string RequestId, string InputJson)? PendingAsk { get; set; }
    public (string ToolUseId, string RequestId, string InputJson)? PendingControlPerm { get; set; }
    public (string RequestId, string InputJson)? ControlAllowRequest { get; set; }
    public (int Input, int Output, int CacheCreate, int CacheRead)? FinalTokens { get; set; }
}

// Pure classification of one stream-json line into the ChatChunks it produces,
// relocated out of ClaudeSession.SendMessageAsync (ClaudeStudioAgent/Program.cs)
// so the ~26 type/subtype branches are unit-testable without spawning a real
// claude.exe process. All I/O (stdin writes, Console, Stopwatch, perf log)
// stays in the caller — this only classifies and returns data.
public static class StreamEventParser
{
    // elapsedMs is the caller's Stopwatch reading at the moment this line
    // arrived — used only to format the "claude init"/"result received"
    // timing chunks in the exact stdout position they occupy today (as
    // regular Chunks entries, not a side channel), so the caller doesn't
    // need to special-case ordering around this method's return value.
    public static StreamEventResult Process(
        JsonElement evt, StreamEventState state, long elapsedMs,
        string? workingDirectory, string expectedCliPermissionMode, string requestedPermissionMode)
    {
        var result = new StreamEventResult();

        if (!evt.TryGetProperty("type", out var typeProp)) return result;
        var type = typeProp.GetString();

        if (type == "system")
            ProcessSystem(evt, state, elapsedMs, expectedCliPermissionMode, requestedPermissionMode, result);
        else if (type == "assistant")
            ProcessAssistant(evt, state, result);
        else if (type == "stream_event")
            ProcessStreamEvent(evt, state, result);
        else if (type == "user")
            ProcessUser(evt, result);
        else if (type == "control_request")
            ProcessControlRequest(evt, workingDirectory, result);
        else if (type == "rate_limit_event")
            ProcessRateLimitEvent(evt, result);
        else if (type == "result")
            ProcessResult(evt, state, elapsedMs, result);

        return result;
    }

    private static void ProcessSystem(
        JsonElement evt, StreamEventState state, long elapsedMs,
        string expectedCliPermissionMode, string requestedPermissionMode, StreamEventResult result)
    {
        if (!evt.TryGetProperty("subtype", out var subProp)) return;
        var subtypeStr = subProp.GetString();

        if (subtypeStr == "informational")
        {
            var infoText = evt.TryGetProperty("content", out var infoProp) && infoProp.ValueKind == JsonValueKind.String
                ? infoProp.GetString() : null;
            if (!string.IsNullOrEmpty(infoText))
                result.Chunks.Add(new ChatChunk { Type = "system-info", Text = infoText! });
            return;
        }
        if (subtypeStr == "status")
        {
            // Not exclusively about compaction — a "requesting" status fires on
            // every ordinary turn (unrelated, presumably "calling the API now").
            // Only react to the two compaction-specific transitions.
            var statusVal = evt.TryGetProperty("status", out var stEl) && stEl.ValueKind == JsonValueKind.String
                ? stEl.GetString() : null;
            if (statusVal == "compacting")
                result.Chunks.Add(new ChatChunk { Type = "compacting", Text = "start" });
            else if (evt.TryGetProperty("compact_result", out _))
                result.Chunks.Add(new ChatChunk { Type = "compacting", Text = "stop" });
            return;
        }
        if (subtypeStr == "compact_boundary")
        {
            if (evt.TryGetProperty("compact_metadata", out var compactMetaEl))
                result.Chunks.Add(new ChatChunk { Type = "compact-boundary", Text = compactMetaEl.GetRawText() });
            return;
        }
        if (subtypeStr != "init") return;

        if (evt.TryGetProperty("session_id", out var sidProp))
        {
            var sid = sidProp.GetString();
            if (!string.IsNullOrEmpty(sid) && sid != state.SessionId)
            {
                state.SessionId = sid;
                result.Chunks.Add(new ChatChunk { Type = "session", Text = sid! });
            }
        }

        if (evt.TryGetProperty("permissionMode", out var pmEl) && pmEl.ValueKind == JsonValueKind.String)
        {
            var actualPm = pmEl.GetString();
            if (!string.IsNullOrEmpty(actualPm) && actualPm != expectedCliPermissionMode)
            {
                result.Chunks.Add(new ChatChunk
                {
                    Type = "warn",
                    Text = $"permission mode mismatch: requested \"{requestedPermissionMode}\" (expected claude to report \"{expectedCliPermissionMode}\") but it reports \"{actualPm}\" — permissions may not behave as expected"
                });
            }
        }

        result.Chunks.Add(new ChatChunk { Type = "timing", Text = $"claude init: {elapsedMs}ms" });
    }

    private static void ProcessAssistant(JsonElement evt, StreamEventState state, StreamEventResult result)
    {
        var msgObj = evt.GetProperty("message");

        // A subagent (Task/Agent) message — parent_tool_use_id is only set on
        // these, never on the main conversation's own assistant messages.
        // Routed entirely separately and never falls through to the normal
        // handling below.
        if (evt.TryGetProperty("parent_tool_use_id", out var parentIdProp) &&
            parentIdProp.ValueKind == JsonValueKind.String)
        {
            AppendSubagentAssistantChunks(result.Chunks, parentIdProp.GetString() ?? "", evt, msgObj);
            return;
        }

        // Synthetic responses (slash commands like /cost, /context) come as a
        // single complete assistant message with text content and no preceding
        // stream_event deltas. Detect by model == "<synthetic>" and emit the
        // text directly as a chunk.
        bool hasModel = msgObj.TryGetProperty("model", out var modelEl);
        bool isSynthetic = hasModel && modelEl.GetString() == "<synthetic>";

        // --fallback-model can silently swap in a different model when the
        // primary is overloaded. LastActiveModel starts at the requested
        // model, so this only fires on an actual deviation (engaged) or a
        // later match against the pre-deviation value (recovered) — never on
        // the ordinary, unchanged case.
        if (hasModel && !isSynthetic)
        {
            var actualModel = modelEl.GetString();
            if (!string.IsNullOrEmpty(actualModel) && actualModel != state.LastActiveModel)
            {
                state.LastActiveModel = actualModel;
                result.Chunks.Add(new ChatChunk { Type = "model-used", Text = actualModel! });
            }
        }

        if (msgObj.TryGetProperty("usage", out var usageLive))
            result.Chunks.Add(BuildTokensLiveChunk(usageLive));

        var content = msgObj.GetProperty("content");
        if (content.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in content.EnumerateArray())
            {
                if (!item.TryGetProperty("type", out var itemType)) continue;
                var itemTypeStr = itemType.GetString();

                if (itemTypeStr == "tool_use")
                {
                    var name = item.TryGetProperty("name", out var nameProp) ? nameProp.GetString() ?? "" : "";
                    var id = item.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
                    string? inputJson = item.TryGetProperty("input", out var inputProp) ? inputProp.GetRawText() : null;

                    result.Chunks.Add(new ChatChunk
                    {
                        Type = "tool_use",
                        Tool = name,
                        ToolInput = inputJson,
                        ToolId = id
                    });
                }
                else if (itemTypeStr == "text" && isSynthetic)
                {
                    var text = item.TryGetProperty("text", out var tp) ? tp.GetString() : null;
                    if (!string.IsNullOrEmpty(text))
                    {
                        result.Chunks.Add(new ChatChunk { Type = "chunk", Text = text! });
                        // Remember it so the terminal is_error result doesn't
                        // re-append the same string and double it in the bubble.
                        state.LastSyntheticText = text;
                    }
                }
            }
        }
    }

    private static void ProcessStreamEvent(JsonElement evt, StreamEventState state, StreamEventResult result)
    {
        if (!evt.TryGetProperty("event", out var streamEvt)) return;
        if (!streamEvt.TryGetProperty("type", out var evtType)) return;
        var evtTypeStr = evtType.GetString();

        if (evtTypeStr == "content_block_start")
        {
            if (state.ThinkingActive) return; // already showing, nothing new to signal
            if (streamEvt.TryGetProperty("content_block", out var cb)
                && cb.TryGetProperty("type", out var cbType)
                && cbType.GetString() == "thinking")
            {
                state.ThinkingActive = true;
                result.Chunks.Add(new ChatChunk { Type = "thinking", Text = "start" });
            }
        }
        else if (evtTypeStr == "content_block_stop")
        {
            // Fires for every block (thinking, text, tool_use) — only acts when
            // a thinking block was left open (e.g. followed directly by a
            // tool_use with no text in between).
            if (state.ThinkingActive)
            {
                state.ThinkingActive = false;
                result.Chunks.Add(new ChatChunk { Type = "thinking", Text = "stop" });
            }
        }
        else if (evtTypeStr == "content_block_delta")
        {
            if (!streamEvt.TryGetProperty("delta", out var delta)) return;
            if (!delta.TryGetProperty("type", out var deltaType)) return;
            if (deltaType.GetString() != "text_delta") return;
            if (!delta.TryGetProperty("text", out var deltaText)) return;

            var text = deltaText.GetString();
            if (string.IsNullOrEmpty(text)) return;

            if (state.ThinkingActive)
            {
                state.ThinkingActive = false;
                result.Chunks.Add(new ChatChunk { Type = "thinking", Text = "stop" });
            }

            result.Chunks.Add(new ChatChunk { Type = "chunk", Text = text! });
        }
        else if (evtTypeStr == "message_delta" && streamEvt.TryGetProperty("usage", out var deltaUsage))
        {
            result.Chunks.Add(BuildTokensLiveChunk(deltaUsage));
        }
        else if (evtTypeStr == "message_start"
            && streamEvt.TryGetProperty("message", out var startMsg)
            && startMsg.TryGetProperty("usage", out var startUsage))
        {
            result.Chunks.Add(BuildTokensLiveChunk(startUsage));
        }
    }

    private static void ProcessUser(JsonElement evt, StreamEventResult result)
    {
        // --replay-user-messages echoes our own message back with isReplay:true
        // — a delivery ack, not a tool_result carrier.
        if (evt.TryGetProperty("isReplay", out var replayProp) && replayProp.ValueKind == JsonValueKind.True)
        {
            result.Chunks.Add(new ChatChunk { Type = "user-ack" });
            return;
        }

        // A subagent's own tool_result, tagged the same way its tool_use was
        // (see the "assistant" handling above).
        if (evt.TryGetProperty("parent_tool_use_id", out var userParentIdProp) &&
            userParentIdProp.ValueKind == JsonValueKind.String)
        {
            var subParentId = userParentIdProp.GetString() ?? "";
            var subagentType = evt.TryGetProperty("subagent_type", out var stEl2) ? stEl2.GetString() : null;
            var taskDescription = evt.TryGetProperty("task_description", out var tdEl2) ? tdEl2.GetString() : null;

            if (evt.TryGetProperty("message", out var subUserMsg) &&
                subUserMsg.TryGetProperty("content", out var subUserContent) &&
                subUserContent.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in subUserContent.EnumerateArray())
                {
                    if (!item.TryGetProperty("type", out var itemType) || itemType.GetString() != "tool_result") continue;

                    SummarizeToolResultItem(item, out var subId, out var subSummary, out var subIsError);

                    result.Chunks.Add(new ChatChunk
                    {
                        Type = subIsError ? "subagent-tool_error" : "subagent-tool_result",
                        Text = subSummary ?? "",
                        ToolId = subId,
                        ParentToolId = subParentId,
                        SubagentType = subagentType,
                        TaskDescription = taskDescription
                    });
                }
            }
            return;
        }

        // tool_result content emitted by claude when a tool finished
        if (!evt.TryGetProperty("message", out var userMsgObj)) return;
        if (!userMsgObj.TryGetProperty("content", out var userContent)) return;
        if (userContent.ValueKind != JsonValueKind.Array) return;

        foreach (var item in userContent.EnumerateArray())
        {
            if (!item.TryGetProperty("type", out var itemType)) continue;
            if (itemType.GetString() != "tool_result") continue;

            SummarizeToolResultItem(item, out var id, out var summary, out var isError);

            result.Chunks.Add(new ChatChunk
            {
                Type = isError ? "tool_error" : "tool_result",
                Text = summary ?? "",
                ToolId = id
            });
        }
    }

    private static void ProcessControlRequest(JsonElement evt, string? workingDirectory, StreamEventResult result)
    {
        // Bidirectional control channel (--permission-prompt-tool stdio).
        // claude blocks waiting for our control_response, so every one MUST be
        // answered (here, or by the caller via ControlAllowRequest) or the
        // turn hangs.
        var reqId = evt.TryGetProperty("request_id", out var ridProp) ? ridProp.GetString() : null;
        if (reqId == null || !evt.TryGetProperty("request", out var reqObj)
            || !reqObj.TryGetProperty("subtype", out var subEl) || subEl.GetString() != "can_use_tool")
            return;

        var toolName = reqObj.TryGetProperty("tool_name", out var tnEl) ? tnEl.GetString() : null;
        var toolUseId = reqObj.TryGetProperty("tool_use_id", out var tuEl) ? tuEl.GetString() : null;
        var inputRaw = reqObj.TryGetProperty("input", out var inEl) ? inEl.GetRawText() : "{}";

        if (toolName == "AskUserQuestion" && toolUseId != null)
        {
            // The card is already rendered from the preceding tool_use chunk.
            // Just remember the request so the caller can answer it when the
            // user picks. claude waits on stdin.
            result.PendingAsk = (toolUseId, reqId, inputRaw);
        }
        else if (toolName == "ExitPlanMode" && toolUseId != null)
        {
            // Plan approval gate — surfaced as a permission modal rather than
            // auto-allowed.
            result.PendingControlPerm = (toolUseId, reqId, inputRaw);
            result.Chunks.Add(new ChatChunk
            {
                Type = "permission_request",
                Tool = "ExitPlanMode",
                ToolInput = inputRaw,
                ToolId = toolUseId,
                Cwd = workingDirectory
            });
        }
        else
        {
            // Non-interactive tool routed through the prompt tool. Happens
            // legitimately (claude consults can_use_tool for e.g. compound Bash
            // even after the hook allowed it) — the caller auto-allows so
            // claude never hangs.
            result.Chunks.Add(new ChatChunk { Type = "timing", Text = $"auto-allowing control_request for tool '{toolName}'" });
            result.ControlAllowRequest = (reqId, inputRaw);
        }
    }

    private static void ProcessRateLimitEvent(JsonElement evt, StreamEventResult result)
    {
        // Fires on every turn (not just near the limit), always carrying the
        // current status. Only forward when there's something to actually tell
        // the user; the UI-side throttle collapses repeats of the same status.
        if (!evt.TryGetProperty("rate_limit_info", out var rlInfo)) return;

        var rlStatus = rlInfo.TryGetProperty("status", out var rlStatusEl) ? rlStatusEl.GetString() : null;
        var rlOverage = rlInfo.TryGetProperty("isUsingOverage", out var rlOverageEl) && rlOverageEl.ValueKind == JsonValueKind.True;
        if (rlOverage || !string.Equals(rlStatus, "allowed", StringComparison.OrdinalIgnoreCase))
            result.Chunks.Add(new ChatChunk { Type = "rate-limit", Text = rlInfo.GetRawText() });
    }

    private static void ProcessResult(JsonElement evt, StreamEventState state, long elapsedMs, StreamEventResult result)
    {
        result.Outcome = StreamEventOutcome.Done;
        result.Chunks.Add(new ChatChunk { Type = "timing", Text = $"result received: {elapsedMs}ms" });

        // session id is stable across turns once set, but the result event
        // re-emits it — refresh + re-broadcast in case the caller missed it.
        if (evt.TryGetProperty("session_id", out var sidProp))
        {
            var newSid = sidProp.GetString();
            if (!string.IsNullOrEmpty(newSid) && newSid != state.SessionId)
            {
                state.SessionId = newSid;
                result.Chunks.Add(new ChatChunk { Type = "session", Text = newSid! });
            }
        }

        if (evt.TryGetProperty("usage", out var usage))
        {
            var inputTok = usage.TryGetProperty("input_tokens", out var inp) ? inp.GetInt32() : 0;
            var outputTok = usage.TryGetProperty("output_tokens", out var out_) ? out_.GetInt32() : 0;
            var cacheCreate = usage.TryGetProperty("cache_creation_input_tokens", out var cc) ? cc.GetInt32() : 0;
            var cacheRead = usage.TryGetProperty("cache_read_input_tokens", out var cr) ? cr.GetInt32() : 0;
            var finalInputTok = inputTok + cacheCreate;
            result.FinalTokens = (finalInputTok, outputTok, cacheCreate, cacheRead);
            result.Chunks.Add(new ChatChunk { Type = "tokens", Text = $"{finalInputTok}/{outputTok}/{cacheRead}" });
        }

        // This terminal subtype carries no "result" string (unlike a normal
        // error), so it needs its own check rather than falling into the
        // generic is_error branch below.
        if (evt.TryGetProperty("subtype", out var resultSubtype) && resultSubtype.GetString() == "error_max_budget_usd")
        {
            string? budgetMsg = null;
            if (evt.TryGetProperty("errors", out var errsEl) && errsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var e in errsEl.EnumerateArray())
                {
                    if (e.ValueKind == JsonValueKind.String) { budgetMsg = e.GetString(); break; }
                }
            }
            result.Chunks.Add(new ChatChunk { Type = "budget-exceeded", Text = budgetMsg ?? "Max budget reached" });
        }
        else if (evt.TryGetProperty("is_error", out var isErrProp) && isErrProp.GetBoolean() &&
            evt.TryGetProperty("result", out var resultProp))
        {
            var errText = resultProp.GetString() ?? "";
            // The CLI surfaces some API errors (e.g. "Prompt is too long") BOTH
            // as a synthetic assistant message AND as this terminal is_error
            // result. When the synthetic already rendered the text this turn,
            // emitting it again doubled it in the bubble. Skip the duplicate;
            // still emit when the result is the only carrier of the error.
            if (errText != state.LastSyntheticText)
                result.Chunks.Add(new ChatChunk { Type = "error", Text = errText });
        }

        // Turn boundary — don't let this turn's synthetic text suppress a
        // coincidentally-identical is_error result in a later turn.
        state.LastSyntheticText = null;
    }

    // A subagent's content array can carry thinking/text/tool_use blocks
    // (never tool_result — that arrives as its own "user"-type event, handled
    // in ProcessUser). Mirrors the shape of the main-conversation "assistant"
    // handling, tagged so the caller can route it to the right nested trace
    // instead of the main chat.
    private static void AppendSubagentAssistantChunks(List<ChatChunk> chunks, string parentId, JsonElement evt, JsonElement msgObj)
    {
        var subagentType = evt.TryGetProperty("subagent_type", out var stEl) ? stEl.GetString() : null;
        var taskDescription = evt.TryGetProperty("task_description", out var tdEl) ? tdEl.GetString() : null;

        if (!msgObj.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            return;

        foreach (var item in content.EnumerateArray())
        {
            if (!item.TryGetProperty("type", out var itemType)) continue;
            var itemTypeStr = itemType.GetString();

            if (itemTypeStr == "thinking")
            {
                var thinking = item.TryGetProperty("thinking", out var thEl) ? thEl.GetString() : null;
                if (string.IsNullOrEmpty(thinking)) continue;
                chunks.Add(new ChatChunk
                {
                    Type = "subagent-thinking",
                    Text = thinking!,
                    ParentToolId = parentId,
                    SubagentType = subagentType,
                    TaskDescription = taskDescription
                });
            }
            else if (itemTypeStr == "text")
            {
                var text = item.TryGetProperty("text", out var txEl) ? txEl.GetString() : null;
                if (string.IsNullOrEmpty(text)) continue;
                chunks.Add(new ChatChunk
                {
                    Type = "subagent-text",
                    Text = text!,
                    ParentToolId = parentId,
                    SubagentType = subagentType,
                    TaskDescription = taskDescription
                });
            }
            else if (itemTypeStr == "tool_use")
            {
                var name = item.TryGetProperty("name", out var nameProp) ? nameProp.GetString() ?? "" : "";
                var id = item.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
                string? inputJson = item.TryGetProperty("input", out var inputProp) ? inputProp.GetRawText() : null;
                chunks.Add(new ChatChunk
                {
                    Type = "subagent-tool_use",
                    Tool = name,
                    ToolInput = inputJson,
                    ToolId = id,
                    ParentToolId = parentId,
                    SubagentType = subagentType,
                    TaskDescription = taskDescription
                });
            }
        }
    }

    // Shared by the main tool_result loop and the subagent one — extracts the
    // tool_use_id, a truncated text summary, and the error flag from a
    // {"type":"tool_result", ...} content item.
    private static void SummarizeToolResultItem(JsonElement item, out string? id, out string? summary, out bool isError)
    {
        id = item.TryGetProperty("tool_use_id", out var idProp) ? idProp.GetString() : null;
        summary = null;

        if (item.TryGetProperty("content", out var contentProp))
        {
            if (contentProp.ValueKind == JsonValueKind.String)
            {
                summary = contentProp.GetString();
            }
            else if (contentProp.ValueKind == JsonValueKind.Array)
            {
                var sb = new StringBuilder();
                foreach (var c in contentProp.EnumerateArray())
                {
                    if (c.TryGetProperty("type", out var ct) && ct.GetString() == "text" &&
                        c.TryGetProperty("text", out var tt))
                    {
                        if (sb.Length > 0) sb.Append('\n');
                        sb.Append(tt.GetString());
                    }
                }
                summary = sb.ToString();
            }
        }

        if (summary != null && summary.Length > 240)
            summary = summary.Substring(0, 237) + "...";

        isError = item.TryGetProperty("is_error", out var errProp) && errProp.GetBoolean();
    }

    private static ChatChunk BuildTokensLiveChunk(JsonElement usage)
    {
        var inTok = usage.TryGetProperty("input_tokens", out var i) ? i.GetInt32() : 0;
        var outTok = usage.TryGetProperty("output_tokens", out var o) ? o.GetInt32() : 0;
        var cacheRead = usage.TryGetProperty("cache_read_input_tokens", out var cr) ? cr.GetInt32() : 0;
        var cacheCreate = usage.TryGetProperty("cache_creation_input_tokens", out var cc) ? cc.GetInt32() : 0;
        return new ChatChunk { Type = "tokens-live", Text = $"{inTok + cacheCreate}/{outTok}/{cacheRead}" };
    }
}
