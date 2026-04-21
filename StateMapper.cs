using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClaudeStatusBridge;

public static class StateMapper
{
    public static string? MapState(string? eventName) => eventName switch
    {
        "UserPromptSubmit" => "working",
        "Stop"             => "idle",
        "Notification"     => "blocked",
        _                  => null,
    };

    public static string? ToDeviceLine(string rawJsonLine)
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
        var state = MapState(eventName);
        if (state is null) return null;

        var outDoc = new JsonObject
        {
            ["state"] = state,
            ["event"] = eventName,
        };
        var ts = node["ts"];
        if (ts is not null) outDoc["ts"] = JsonNode.Parse(ts.ToJsonString());
        return outDoc.ToJsonString();
    }
}
