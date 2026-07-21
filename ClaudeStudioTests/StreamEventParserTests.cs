using System.Linq;
using System.Text.Json;
using ClaudeStudioShared;
using Xunit;

namespace ClaudeStudioTests;

// One [Fact] per row of the branch-by-branch mapping table in the #22a fatia 3
// plan — each targets exactly one classification rule from SendMessageAsync's
// original if/else chain (ClaudeStudioAgent/Program.cs).
public class StreamEventParserTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    private static StreamEventResult Process(
        string json, StreamEventState? state = null, long elapsedMs = 0,
        string? workingDirectory = null, string expectedCliPermissionMode = "default", string requestedPermissionMode = "ask")
        => StreamEventParser.Process(Parse(json), state ?? new StreamEventState(), elapsedMs, workingDirectory, expectedCliPermissionMode, requestedPermissionMode);

    // ---- system/informational ----

    [Fact]
    public void System_informational_with_text_emits_system_info_chunk()
    {
        var result = Process("""{"type":"system","subtype":"informational","content":"Unknown command: /teste"}""");

        var chunk = Assert.Single(result.Chunks);
        Assert.Equal("system-info", chunk.Type);
        Assert.Equal("Unknown command: /teste", chunk.Text);
    }

    [Fact]
    public void System_informational_with_empty_content_emits_nothing()
    {
        var result = Process("""{"type":"system","subtype":"informational","content":""}""");
        Assert.Empty(result.Chunks);
    }

    // ---- system/status ----

    [Fact]
    public void System_status_compacting_emits_compacting_start()
    {
        var result = Process("""{"type":"system","subtype":"status","status":"compacting"}""");

        var chunk = Assert.Single(result.Chunks);
        Assert.Equal("compacting", chunk.Type);
        Assert.Equal("start", chunk.Text);
    }

    [Fact]
    public void System_status_with_compact_result_emits_compacting_stop()
    {
        var result = Process("""{"type":"system","subtype":"status","status":"idle","compact_result":{}}""");

        var chunk = Assert.Single(result.Chunks);
        Assert.Equal("compacting", chunk.Type);
        Assert.Equal("stop", chunk.Text);
    }

    [Fact]
    public void System_status_requesting_emits_nothing()
    {
        // Regression lock: "requesting" fires on every ordinary turn and must
        // never be mistaken for a compaction transition (real bug found and
        // fixed earlier in this session).
        var result = Process("""{"type":"system","subtype":"status","status":"requesting"}""");
        Assert.Empty(result.Chunks);
    }

    [Fact]
    public void System_status_unknown_value_emits_nothing()
    {
        var result = Process("""{"type":"system","subtype":"status","status":"something-else"}""");
        Assert.Empty(result.Chunks);
    }

    // ---- system/compact_boundary ----

    [Fact]
    public void System_compact_boundary_with_metadata_emits_chunk()
    {
        var result = Process("""{"type":"system","subtype":"compact_boundary","compact_metadata":{"trigger":"auto"}}""");

        var chunk = Assert.Single(result.Chunks);
        Assert.Equal("compact-boundary", chunk.Type);
        Assert.Contains("trigger", chunk.Text);
    }

    [Fact]
    public void System_compact_boundary_without_metadata_emits_nothing()
    {
        var result = Process("""{"type":"system","subtype":"compact_boundary"}""");
        Assert.Empty(result.Chunks);
    }

    // ---- system/init ----

    [Fact]
    public void System_init_new_session_id_emits_session_chunk_and_updates_state()
    {
        var state = new StreamEventState();
        var result = Process("""{"type":"system","subtype":"init","session_id":"abc123","permissionMode":"default"}""", state);

        Assert.Contains(result.Chunks, c => c.Type == "session" && c.Text == "abc123");
        Assert.Equal("abc123", state.SessionId);
    }

    [Fact]
    public void System_init_same_session_id_emits_no_session_chunk()
    {
        var state = new StreamEventState { SessionId = "abc123" };
        var result = Process("""{"type":"system","subtype":"init","session_id":"abc123","permissionMode":"default"}""", state);

        Assert.DoesNotContain(result.Chunks, c => c.Type == "session");
    }

    [Fact]
    public void System_init_permission_mode_mismatch_emits_warn_chunk()
    {
        var result = Process(
            """{"type":"system","subtype":"init","permissionMode":"default"}""",
            expectedCliPermissionMode: "bypassPermissions", requestedPermissionMode: "yolo");

        var warn = Assert.Single(result.Chunks, c => c.Type == "warn");
        Assert.Contains("yolo", warn.Text);
        Assert.Contains("bypassPermissions", warn.Text);
        Assert.Contains("default", warn.Text);
    }

    [Fact]
    public void System_init_permission_mode_match_emits_no_warn()
    {
        var result = Process(
            """{"type":"system","subtype":"init","permissionMode":"default"}""",
            expectedCliPermissionMode: "default", requestedPermissionMode: "ask");

        Assert.DoesNotContain(result.Chunks, c => c.Type == "warn");
    }

    [Fact]
    public void System_init_always_emits_timing_chunk()
    {
        var result = Process("""{"type":"system","subtype":"init"}""", elapsedMs: 42);

        var timing = Assert.Single(result.Chunks);
        Assert.Equal("timing", timing.Type);
        Assert.Equal("claude init: 42ms", timing.Text);
    }

    // ---- assistant: subagent routing ----

    [Fact]
    public void Assistant_with_parent_tool_use_id_routes_to_subagent_chunks_only()
    {
        var result = Process("""
            {"type":"assistant","parent_tool_use_id":"parent1","subagent_type":"Explore","task_description":"desc",
             "message":{"model":"claude-sonnet-5","content":[{"type":"thinking","thinking":"reasoning"},{"type":"tool_use","name":"Read","id":"tu1","input":{}}]}}
            """);

        Assert.All(result.Chunks, c => Assert.StartsWith("subagent-", c.Type));
        Assert.DoesNotContain(result.Chunks, c => c.Type == "model-used" || c.Type == "tool_use");
        Assert.Contains(result.Chunks, c => c.Type == "subagent-thinking" && c.ParentToolId == "parent1" && c.SubagentType == "Explore");
        Assert.Contains(result.Chunks, c => c.Type == "subagent-tool_use" && c.Tool == "Read");
    }

    // ---- assistant: synthetic text vs normal text ----

    [Fact]
    public void Assistant_synthetic_text_emits_chunk()
    {
        var result = Process("""{"type":"assistant","message":{"model":"<synthetic>","content":[{"type":"text","text":"cost info"}]}}""");

        var chunk = Assert.Single(result.Chunks);
        Assert.Equal("chunk", chunk.Type);
        Assert.Equal("cost info", chunk.Text);
    }

    [Fact]
    public void Assistant_normal_non_synthetic_text_emits_no_chunk()
    {
        // Normal text already arrives via stream_event deltas — emitting it
        // again here would double the text in the UI.
        var result = Process("""{"type":"assistant","message":{"model":"claude-sonnet-5","content":[{"type":"text","text":"hello"}]}}""");
        Assert.DoesNotContain(result.Chunks, c => c.Type == "chunk");
    }

    [Fact]
    public void Assistant_tool_use_emits_chunk_regardless_of_synthetic()
    {
        // model matches state.LastActiveModel so this stays isolated to the
        // tool_use chunk, without a model-used chunk also firing (that's
        // covered separately below).
        var state = new StreamEventState { LastActiveModel = "claude-sonnet-5" };
        var result = Process("""{"type":"assistant","message":{"model":"claude-sonnet-5","content":[{"type":"tool_use","name":"Read","id":"tu1","input":{"file":"a.txt"}}]}}""", state);

        var chunk = Assert.Single(result.Chunks);
        Assert.Equal("tool_use", chunk.Type);
        Assert.Equal("Read", chunk.Tool);
        Assert.Equal("tu1", chunk.ToolId);
    }

    // ---- assistant: model-used (#14 fallback-model tracking) ----

    [Fact]
    public void Assistant_model_change_emits_model_used_and_updates_state()
    {
        var state = new StreamEventState { LastActiveModel = "claude-haiku-4-5" };
        var result = Process("""{"type":"assistant","message":{"model":"claude-sonnet-5","content":[]}}""", state);

        Assert.Contains(result.Chunks, c => c.Type == "model-used" && c.Text == "claude-sonnet-5");
        Assert.Equal("claude-sonnet-5", state.LastActiveModel);
    }

    [Fact]
    public void Assistant_model_unchanged_emits_no_model_used_chunk()
    {
        var state = new StreamEventState { LastActiveModel = "claude-sonnet-5" };
        var result = Process("""{"type":"assistant","message":{"model":"claude-sonnet-5","content":[]}}""", state);

        Assert.DoesNotContain(result.Chunks, c => c.Type == "model-used");
    }

    [Fact]
    public void Assistant_synthetic_model_never_emits_model_used()
    {
        var state = new StreamEventState { LastActiveModel = "claude-sonnet-5" };
        var result = Process("""{"type":"assistant","message":{"model":"<synthetic>","content":[]}}""", state);

        Assert.DoesNotContain(result.Chunks, c => c.Type == "model-used");
        Assert.Equal("claude-sonnet-5", state.LastActiveModel);
    }

    // ---- assistant: usage ----

    [Fact]
    public void Assistant_usage_emits_tokens_live_chunk()
    {
        var result = Process("""
            {"type":"assistant","message":{"model":"claude-sonnet-5",
             "usage":{"input_tokens":10,"output_tokens":5,"cache_read_input_tokens":1,"cache_creation_input_tokens":2},"content":[]}}
            """);

        var chunk = Assert.Single(result.Chunks, c => c.Type == "tokens-live");
        Assert.Equal("12/5/1", chunk.Text);
    }

    // ---- stream_event/content_block_start ----

    [Fact]
    public void ContentBlockStart_thinking_when_not_active_emits_thinking_start()
    {
        var state = new StreamEventState();
        var result = Process("""{"type":"stream_event","event":{"type":"content_block_start","content_block":{"type":"thinking"}}}""", state);

        var chunk = Assert.Single(result.Chunks);
        Assert.Equal("thinking", chunk.Type);
        Assert.Equal("start", chunk.Text);
        Assert.True(state.ThinkingActive);
    }

    [Fact]
    public void ContentBlockStart_thinking_when_already_active_emits_nothing()
    {
        var state = new StreamEventState { ThinkingActive = true };
        var result = Process("""{"type":"stream_event","event":{"type":"content_block_start","content_block":{"type":"thinking"}}}""", state);

        Assert.Empty(result.Chunks);
    }

    [Fact]
    public void ContentBlockStart_non_thinking_block_emits_nothing()
    {
        var result = Process("""{"type":"stream_event","event":{"type":"content_block_start","content_block":{"type":"text"}}}""");
        Assert.Empty(result.Chunks);
    }

    // ---- stream_event/content_block_stop ----

    [Fact]
    public void ContentBlockStop_while_thinking_active_emits_thinking_stop_and_resets()
    {
        var state = new StreamEventState { ThinkingActive = true };
        var result = Process("""{"type":"stream_event","event":{"type":"content_block_stop"}}""", state);

        var chunk = Assert.Single(result.Chunks);
        Assert.Equal("thinking", chunk.Type);
        Assert.Equal("stop", chunk.Text);
        Assert.False(state.ThinkingActive);
    }

    [Fact]
    public void ContentBlockStop_while_not_thinking_emits_nothing()
    {
        var state = new StreamEventState { ThinkingActive = false };
        var result = Process("""{"type":"stream_event","event":{"type":"content_block_stop"}}""", state);

        Assert.Empty(result.Chunks);
    }

    // ---- stream_event/content_block_delta (text_delta) ----

    [Fact]
    public void ContentBlockDelta_text_emits_chunk()
    {
        var result = Process("""{"type":"stream_event","event":{"type":"content_block_delta","delta":{"type":"text_delta","text":"hello"}}}""");

        var chunk = Assert.Single(result.Chunks);
        Assert.Equal("chunk", chunk.Type);
        Assert.Equal("hello", chunk.Text);
    }

    [Fact]
    public void ContentBlockDelta_text_while_thinking_active_emits_stop_before_chunk_in_order()
    {
        var state = new StreamEventState { ThinkingActive = true };
        var result = Process("""{"type":"stream_event","event":{"type":"content_block_delta","delta":{"type":"text_delta","text":"hello"}}}""", state);

        Assert.Equal(2, result.Chunks.Count);
        Assert.Equal("thinking", result.Chunks[0].Type);
        Assert.Equal("stop", result.Chunks[0].Text);
        Assert.Equal("chunk", result.Chunks[1].Type);
        Assert.False(state.ThinkingActive);
    }

    [Fact]
    public void ContentBlockDelta_empty_text_emits_nothing()
    {
        var result = Process("""{"type":"stream_event","event":{"type":"content_block_delta","delta":{"type":"text_delta","text":""}}}""");
        Assert.Empty(result.Chunks);
    }

    [Fact]
    public void ContentBlockDelta_non_text_delta_type_emits_nothing()
    {
        var result = Process("""{"type":"stream_event","event":{"type":"content_block_delta","delta":{"type":"input_json_delta"}}}""");
        Assert.Empty(result.Chunks);
    }

    // ---- stream_event/message_delta and message_start ----

    [Fact]
    public void MessageDelta_with_usage_emits_tokens_live_chunk()
    {
        var result = Process("""{"type":"stream_event","event":{"type":"message_delta","usage":{"input_tokens":1,"output_tokens":2}}}""");

        var chunk = Assert.Single(result.Chunks);
        Assert.Equal("tokens-live", chunk.Type);
    }

    [Fact]
    public void MessageStart_with_usage_emits_tokens_live_chunk()
    {
        var result = Process("""{"type":"stream_event","event":{"type":"message_start","message":{"usage":{"input_tokens":1,"output_tokens":2}}}}""");

        var chunk = Assert.Single(result.Chunks);
        Assert.Equal("tokens-live", chunk.Type);
    }

    // ---- user/isReplay ----

    [Fact]
    public void User_isReplay_emits_user_ack_only()
    {
        var result = Process("""{"type":"user","isReplay":true,"message":{"content":[{"type":"tool_result","content":"ignored"}]}}""");

        var chunk = Assert.Single(result.Chunks);
        Assert.Equal("user-ack", chunk.Type);
    }

    // ---- user/subagent tool_result ----

    [Fact]
    public void User_subagent_tool_result_emits_subagent_tool_result_chunk()
    {
        var result = Process("""
            {"type":"user","parent_tool_use_id":"parent1","subagent_type":"Explore","task_description":"desc",
             "message":{"content":[{"type":"tool_result","tool_use_id":"tu1","content":"ok"}]}}
            """);

        var chunk = Assert.Single(result.Chunks);
        Assert.Equal("subagent-tool_result", chunk.Type);
        Assert.Equal("tu1", chunk.ToolId);
        Assert.Equal("parent1", chunk.ParentToolId);
        Assert.Equal("Explore", chunk.SubagentType);
    }

    [Fact]
    public void User_subagent_tool_result_with_is_error_emits_subagent_tool_error_chunk()
    {
        var result = Process("""
            {"type":"user","parent_tool_use_id":"parent1",
             "message":{"content":[{"type":"tool_result","tool_use_id":"tu1","content":"boom","is_error":true}]}}
            """);

        var chunk = Assert.Single(result.Chunks);
        Assert.Equal("subagent-tool_error", chunk.Type);
    }

    // ---- user/generic tool_result ----

    [Fact]
    public void User_tool_result_emits_tool_result_chunk()
    {
        var result = Process("""{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"tu1","content":"ok"}]}}""");

        var chunk = Assert.Single(result.Chunks);
        Assert.Equal("tool_result", chunk.Type);
        Assert.Equal("tu1", chunk.ToolId);
    }

    [Fact]
    public void User_tool_result_with_is_error_emits_tool_error_chunk()
    {
        var result = Process("""{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"tu1","content":"boom","is_error":true}]}}""");

        var chunk = Assert.Single(result.Chunks);
        Assert.Equal("tool_error", chunk.Type);
    }

    [Fact]
    public void User_missing_message_emits_nothing()
    {
        var result = Process("""{"type":"user"}""");
        Assert.Empty(result.Chunks);
    }

    [Fact]
    public void User_content_not_an_array_emits_nothing()
    {
        var result = Process("""{"type":"user","message":{"content":"plain string"}}""");
        Assert.Empty(result.Chunks);
    }

    // ---- control_request ----

    [Fact]
    public void ControlRequest_AskUserQuestion_registers_pending_ask_without_a_chunk()
    {
        var result = Process("""{"type":"control_request","request_id":"req1","request":{"subtype":"can_use_tool","tool_name":"AskUserQuestion","tool_use_id":"tu1","input":{"question":"?"}}}""");

        Assert.Empty(result.Chunks);
        Assert.NotNull(result.PendingAsk);
        Assert.Equal(("tu1", "req1", """{"question":"?"}"""), result.PendingAsk!.Value);
    }

    [Fact]
    public void ControlRequest_ExitPlanMode_registers_pending_control_perm_and_emits_permission_request_chunk()
    {
        var result = Process(
            """{"type":"control_request","request_id":"req1","request":{"subtype":"can_use_tool","tool_name":"ExitPlanMode","tool_use_id":"tu1","input":{}}}""",
            workingDirectory: @"C:\repo");

        Assert.NotNull(result.PendingControlPerm);
        Assert.Equal("tu1", result.PendingControlPerm!.Value.ToolUseId);

        var chunk = Assert.Single(result.Chunks);
        Assert.Equal("permission_request", chunk.Type);
        Assert.Equal("ExitPlanMode", chunk.Tool);
        Assert.Equal(@"C:\repo", chunk.Cwd);
    }

    [Fact]
    public void ControlRequest_generic_tool_emits_timing_chunk_and_requests_auto_allow()
    {
        var result = Process("""{"type":"control_request","request_id":"req1","request":{"subtype":"can_use_tool","tool_name":"Bash","tool_use_id":"tu1","input":{}}}""");

        var chunk = Assert.Single(result.Chunks);
        Assert.Equal("timing", chunk.Type);
        Assert.Contains("Bash", chunk.Text);
        Assert.NotNull(result.ControlAllowRequest);
        Assert.Equal("req1", result.ControlAllowRequest!.Value.RequestId);
    }

    [Fact]
    public void ControlRequest_non_can_use_tool_subtype_emits_nothing()
    {
        var result = Process("""{"type":"control_request","request_id":"req1","request":{"subtype":"something_else"}}""");

        Assert.Empty(result.Chunks);
        Assert.Null(result.PendingAsk);
        Assert.Null(result.ControlAllowRequest);
    }

    // ---- rate_limit_event ----

    [Fact]
    public void RateLimitEvent_status_allowed_no_overage_emits_nothing()
    {
        var result = Process("""{"type":"rate_limit_event","rate_limit_info":{"status":"allowed","isUsingOverage":false}}""");
        Assert.Empty(result.Chunks);
    }

    [Fact]
    public void RateLimitEvent_status_not_allowed_emits_chunk()
    {
        var result = Process("""{"type":"rate_limit_event","rate_limit_info":{"status":"warning","isUsingOverage":false}}""");

        var chunk = Assert.Single(result.Chunks);
        Assert.Equal("rate-limit", chunk.Type);
    }

    [Fact]
    public void RateLimitEvent_overage_true_emits_chunk_even_when_status_allowed()
    {
        // Condition is OR, not AND — overage alone is enough even on the
        // otherwise-happy "allowed" status.
        var result = Process("""{"type":"rate_limit_event","rate_limit_info":{"status":"allowed","isUsingOverage":true}}""");

        var chunk = Assert.Single(result.Chunks);
        Assert.Equal("rate-limit", chunk.Type);
    }

    // ---- result ----

    [Fact]
    public void Result_always_sets_outcome_done()
    {
        var result = Process("""{"type":"result"}""");
        Assert.Equal(StreamEventOutcome.Done, result.Outcome);
    }

    [Fact]
    public void Result_always_emits_timing_chunk_first()
    {
        var result = Process("""{"type":"result"}""", elapsedMs: 999);

        Assert.Equal("timing", result.Chunks[0].Type);
        Assert.Equal("result received: 999ms", result.Chunks[0].Text);
    }

    [Fact]
    public void Result_new_session_id_emits_session_chunk_and_updates_state()
    {
        var state = new StreamEventState { SessionId = "old" };
        var result = Process("""{"type":"result","session_id":"new"}""", state);

        Assert.Contains(result.Chunks, c => c.Type == "session" && c.Text == "new");
        Assert.Equal("new", state.SessionId);
    }

    [Fact]
    public void Result_same_session_id_emits_no_session_chunk()
    {
        var state = new StreamEventState { SessionId = "same" };
        var result = Process("""{"type":"result","session_id":"same"}""", state);

        Assert.DoesNotContain(result.Chunks, c => c.Type == "session");
    }

    [Fact]
    public void Result_usage_emits_tokens_chunk_and_fills_final_tokens()
    {
        var result = Process("""{"type":"result","usage":{"input_tokens":100,"output_tokens":50,"cache_creation_input_tokens":10,"cache_read_input_tokens":5}}""");

        var chunk = Assert.Single(result.Chunks, c => c.Type == "tokens");
        Assert.Equal("110/50/5", chunk.Text);
        Assert.Equal((110, 50, 10, 5), result.FinalTokens);
    }

    [Fact]
    public void Result_no_usage_leaves_final_tokens_null()
    {
        var result = Process("""{"type":"result"}""");
        Assert.Null(result.FinalTokens);
    }

    [Fact]
    public void Result_error_max_budget_usd_emits_budget_exceeded_chunk()
    {
        var result = Process("""{"type":"result","subtype":"error_max_budget_usd","errors":["Max budget of $1 reached"]}""");

        var chunk = Assert.Single(result.Chunks, c => c.Type == "budget-exceeded");
        Assert.Equal("Max budget of $1 reached", chunk.Text);
    }

    [Fact]
    public void Result_is_error_emits_error_chunk()
    {
        var result = Process("""{"type":"result","is_error":true,"result":"Something failed"}""");

        var chunk = Assert.Single(result.Chunks, c => c.Type == "error");
        Assert.Equal("Something failed", chunk.Text);
    }

    [Fact]
    public void Result_budget_and_is_error_both_present_only_budget_branch_fires()
    {
        // if/else if — mutually exclusive today even if a payload somehow set both.
        var result = Process("""{"type":"result","subtype":"error_max_budget_usd","errors":["budget msg"],"is_error":true,"result":"error msg"}""");

        Assert.Contains(result.Chunks, c => c.Type == "budget-exceeded");
        Assert.DoesNotContain(result.Chunks, c => c.Type == "error");
    }

    [Fact]
    public void Result_neither_budget_nor_error_emits_no_extra_chunk()
    {
        var result = Process("""{"type":"result"}""");

        Assert.DoesNotContain(result.Chunks, c => c.Type == "budget-exceeded" || c.Type == "error");
    }

    // ---- unknown / missing type ----

    [Fact]
    public void Unknown_type_emits_nothing_and_continues()
    {
        var result = Process("""{"type":"something_unknown"}""");

        Assert.Empty(result.Chunks);
        Assert.Equal(StreamEventOutcome.Continue, result.Outcome);
    }

    [Fact]
    public void Missing_type_property_emits_nothing_and_continues()
    {
        var result = Process("{}");

        Assert.Empty(result.Chunks);
        Assert.Equal(StreamEventOutcome.Continue, result.Outcome);
    }
}
