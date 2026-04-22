using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClaudeStatusBridge;

public static class StateMapper
{
    public const string StateWorking    = "working";
    public const string StateIdle       = "idle";
    public const string StateBlocked    = "blocked";
    public const string StateCompacting = "compacting";
    public const string StateError      = "error";
    public const string StateThinking   = "thinking";

    public record Mapped(
        string? State,
        string? EventName,
        string? ToolName,
        JsonNode? Ts,
        bool IsToolActivity,
        string? TaskId,
        string? TaskStatus);

    // Notification subtypes that mean Claude is genuinely waiting on the
    // user. Other subtypes (idle_prompt = AFK reminder, auth_success =
    // informational) carry no actionable state change.
    private static readonly HashSet<string> BlockingNotificationTypes = new()
    {
        "permission_prompt",
        "elicitation_dialog",
    };

    public static Mapped? Parse(string rawJsonLine)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(rawJsonLine);
        }
        catch (JsonException)
        {
            return null;
        }
        if (node is null) return null;

        var eventName = node["event"]?.GetValue<string>();
        var toolName  = node["tool_name"]?.GetValue<string>();
        var ts        = node["ts"];

        string? state = null;
        var isToolActivity = false;
        string? taskId = null;
        string? taskStatus = null;

        // Task lifecycle extraction.
        // - PostToolUse(TaskCreate): tool_response.task.id is the fresh ID;
        //   status defaults to "pending".
        // - PreToolUse(TaskUpdate): tool_input.taskId and tool_input.status
        //   describe the transition.
        if (toolName == "TaskCreate" && eventName == "PostToolUse")
        {
            taskId = node["tool_response"]?["task"]?["id"]?.GetValue<string>();
            taskStatus = "pending";
        }
        else if (toolName == "TaskUpdate" && eventName == "PreToolUse")
        {
            var input = node["tool_input"];
            taskId = input?["taskId"]?.GetValue<string>();
            taskStatus = input?["status"]?.GetValue<string>();
        }

        switch (eventName)
        {
            case "UserPromptSubmit":
                state = StateWorking;
                break;
            case "Stop":
                state = StateIdle;
                break;
            case "Notification":
                var notifType = node["notification_type"]?.GetValue<string>();
                if (notifType is not null && BlockingNotificationTypes.Contains(notifType))
                    state = StateBlocked;
                // Unknown or non-blocking notification_type: leave state null.
                break;
            case "PreCompact":
                state = StateCompacting;
                break;
            case "PostCompact":
                state = null;  // consumer restores prior state
                break;
            case "PreToolUse":
            case "PostToolUse":
                isToolActivity = true;
                if (eventName == "PostToolUse" && LooksLikeFailure(node))
                    state = StateError;
                break;
            case "PostToolUseFailure":
                isToolActivity = true;
                state = StateError;
                break;
            // SubagentStop: no state change, no tool activity; handled as a
            // metric signal by BridgeRunner.
        }

        return new Mapped(state, eventName, toolName, ts, isToolActivity, taskId, taskStatus);
    }

    public static string BuildDeviceLine(
        string state,
        int subagentCount,
        int tasksActive,
        int tasksCompleted,
        string? eventName,
        JsonNode? ts)
    {
        var doc = new JsonObject
        {
            ["state"] = state,
            ["subagent_count"] = subagentCount,
            ["tasks_active"] = tasksActive,
            ["tasks_completed"] = tasksCompleted,
        };
        if (eventName is not null) doc["event"] = eventName;
        if (ts is not null) doc["ts"] = JsonNode.Parse(ts.ToJsonString());
        return doc.ToJsonString();
    }

    private static bool LooksLikeFailure(JsonNode node)
    {
        var success = node["success"];
        if (success is not null && success.GetValueKind() == JsonValueKind.False)
            return true;
        return node["error"] is not null;
    }
}
