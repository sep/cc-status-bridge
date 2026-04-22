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

    public record Mapped(string? State, string? EventName, JsonNode? Ts, bool IsToolActivity);

    /// <summary>
    /// Parse a broker event line and decide what state it represents, if any.
    /// Returns a Mapped record; consumers can use State to update the
    /// rendered state (null means "no change") and IsToolActivity to reset
    /// the thinking timer.
    /// </summary>
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
        var ts = node["ts"];

        string? state = null;
        var isToolActivity = false;

        switch (eventName)
        {
            case "UserPromptSubmit":
                state = StateWorking;
                break;
            case "Stop":
                state = StateIdle;
                break;
            case "Notification":
                state = StateBlocked;
                break;
            case "PreCompact":
                state = StateCompacting;
                break;
            case "PostCompact":
                // Signal end-of-compaction; consumer restores prior state.
                state = null;
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
        }

        return new Mapped(state, eventName, ts, isToolActivity);
    }

    public static string BuildDeviceLine(string state, string? eventName, JsonNode? ts)
    {
        var doc = new JsonObject { ["state"] = state };
        if (eventName is not null) doc["event"] = eventName;
        if (ts is not null) doc["ts"] = JsonNode.Parse(ts.ToJsonString());
        return doc.ToJsonString();
    }

    private static bool LooksLikeFailure(JsonNode node)
    {
        // Some Claude Code builds flag errors on PostToolUse via a success
        // field or an explicit error payload; honor either.
        var success = node["success"];
        if (success is not null && success.GetValueKind() == JsonValueKind.False)
            return true;
        return node["error"] is not null;
    }
}
