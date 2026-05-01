namespace ClaudeStatusBridge;

/// <summary>
/// Per-Claude-session state tracked by the bridge. One instance per
/// subscribed session. Holds everything needed to dedupe outgoing
/// snapshots and to detect state transitions like working → thinking
/// or "task plan ended."
///
/// Pre-v0.2.0 the bridge maintained a single instance of this state
/// directly on BridgeRunner. The refactor to per-session state is the
/// foundation for the multi-session subscription manager that lets one
/// bridge fan out N concurrent Claude sessions to N display slots.
/// </summary>
internal sealed class SessionState
{
    public string SessionId { get; }

    /// <summary>The state currently rendered for this session.</summary>
    public string? CurrentState;

    /// <summary>State stashed when entering compacting, restored on PostCompact.</summary>
    public string? PreCompactState;

    public int SubagentCount;
    public readonly Dictionary<string, string> Tasks = new();

    public DateTimeOffset LastToolActivity = DateTimeOffset.Now;
    public DateTimeOffset LastStateChange  = DateTimeOffset.Now;

    /// <summary>Wall-clock timestamp of the most recent successful emit.</summary>
    public DateTimeOffset LastEmit = DateTimeOffset.Now;

    /// <summary>
    /// Last-emitted snapshot fields used for dedup so the bridge only
    /// writes to serial when something actually changed.
    /// </summary>
    public string? LastEmittedState;
    public int LastEmittedSubagentCount   = -1;
    public int LastEmittedTasksActive     = -1;
    public int LastEmittedTasksCompleted  = -1;
    public string? LastEmittedClientSlot;

    public SessionState(string sessionId) { SessionId = sessionId; }

    public int TasksActive    => Tasks.Values.Count(s => s == "pending" || s == "in_progress");
    public int TasksCompleted => Tasks.Values.Count(s => s == "completed");

    /// <summary>
    /// Force the next emit to look like a fresh first-emit (e.g., after a
    /// device reconnect, so the firmware gets the current state pushed
    /// rather than waiting for the next state change).
    /// </summary>
    public void InvalidateLastEmit()
    {
        LastEmittedState           = null;
        LastEmittedSubagentCount   = -1;
        LastEmittedTasksActive     = -1;
        LastEmittedTasksCompleted  = -1;
        LastEmittedClientSlot      = null;
    }
}
