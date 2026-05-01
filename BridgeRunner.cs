using System.Text.Json.Nodes;

namespace ClaudeStatusBridge;

/// <summary>
/// Owns the bridge's state machine and serial output. Subscribes to one
/// or more Claude session brokers, interprets lifecycle events into a
/// rendered-state lexicon plus per-session counters, and writes
/// snapshots to the serial port. State is now keyed by session_id in a
/// dictionary, so the multi-session subscription manager (next phase)
/// can fan out N concurrent sessions to N display slots without any
/// further structural changes here.
/// </summary>
public sealed class BridgeRunner
{
    private readonly BridgeOptions _options;
    private readonly BrokerClient _broker;
    private readonly SerialOutput _serial;

    private readonly object _lock = new();

    /// <summary>
    /// Per-session state, keyed by session_id. While the bridge is
    /// single-subscribe (pre-Phase-3) this dictionary has at most one
    /// entry; after Phase 3 it holds an entry per concurrently-subscribed
    /// session.
    /// </summary>
    private readonly Dictionary<string, SessionState> _sessions = new();

    private string? _currentSessionId;
    private string? _lastConfiguredJson;

    public BridgeRunner(BridgeOptions options, BrokerClient broker, SerialOutput serial)
    {
        _options = options;
        _broker = broker;
        _serial = serial;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        var thinkingTicker = Task.Run(() => ThinkingTickerAsync(ct), ct);
        var serialMonitor  = Task.Run(() => SerialMonitorAsync(ct), ct);
        try
        {
            await SubscriptionLoopAsync(ct);
        }
        finally
        {
            try { await thinkingTicker; } catch { }
            try { await serialMonitor; }  catch { }
        }
    }

    // ============================================================
    // Subscription loop (single-subscribe; Phase 3 replaces this)
    // ============================================================

    private async Task SubscriptionLoopAsync(CancellationToken ct)
    {
        string? lastEmptyReason = null;

        while (!ct.IsCancellationRequested)
        {
            var serialWasOpen = _serial.IsOpen;
            if (!_serial.IsOpen && !_serial.TryOpen())
                await SafeDelay(_options.SerialReopenDelayMs, ct);
            if (!serialWasOpen && _serial.IsOpen)
                lock (_lock) { SendConfigureIfChangedLocked(); }

            var endpoint = _broker.PickEndpoint();
            if (endpoint is null)
            {
                var pinned = _broker.ReadPinnedSessionId();
                var reason = pinned is null
                    ? "no broker found, scanning..."
                    : $"pinned session {Shorten(pinned)} has no active broker, waiting...";
                if (reason != lastEmptyReason)
                {
                    Log.Info($"[bridge] {reason}");
                    lastEmptyReason = reason;
                }
                await SafeDelay(_options.ReconnectDelayMs, ct);
                continue;
            }
            lastEmptyReason = null;

            Log.Info($"[bridge] subscribing to session {Shorten(endpoint.SessionId)} on port {endpoint.Port}");
            SwitchActiveSessionLocked(endpoint.SessionId);

            using var subCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var watchdog = StartSubscriptionWatchdog(endpoint, subCts);

            try
            {
                await foreach (var rawLine in _broker.StreamEvents(endpoint, subCts.Token))
                    HandleEvent(rawLine);
                if (!ct.IsCancellationRequested && !subCts.IsCancellationRequested)
                    Log.Info("[bridge] subscription ended");
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // watchdog asked us to switch
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Warn($"[bridge] broker error: {ex.Message}");
            }
            finally
            {
                subCts.Cancel();
                try { await watchdog; } catch { }
            }

            if (!ct.IsCancellationRequested)
                await SafeDelay(_options.ReconnectDelayMs, ct);
        }
    }

    private void SwitchActiveSessionLocked(string sessionId)
    {
        lock (_lock)
        {
            if (_currentSessionId == sessionId) return;
            _currentSessionId = sessionId;
            // Drop stale per-session state for sessions we are no longer
            // subscribed to (single-subscribe assumption; Phase 3 keeps
            // entries for all concurrently-subscribed sessions).
            var stale = _sessions.Keys.Where(k => k != sessionId).ToList();
            foreach (var s in stale) _sessions.Remove(s);
            // Ensure the freshly-active session has its own state entry.
            _ = SessionFor(sessionId);
        }
    }

    private SessionState SessionFor(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var s))
        {
            s = new SessionState(sessionId);
            _sessions[sessionId] = s;
        }
        return s;
    }

    // ============================================================
    // Event handling
    // ============================================================

    private void HandleEvent(string rawLine)
    {
        var parsed = StateMapper.Parse(rawLine);
        if (parsed is null) return;

        // Use the raw line's session, not _currentSessionId, so when
        // Phase 3 multi-subscribes we route events to the correct
        // SessionState without further changes.
        JsonNode? rawNode = null;
        try { rawNode = JsonNode.Parse(rawLine); } catch { /* tolerated */ }
        var eventSessionId = rawNode?["session_id"]?.GetValue<string>() ?? _currentSessionId;
        if (eventSessionId is null) return;

        lock (_lock)
        {
            // Re-check panel layout on every event so /claude-status:configure
            // takes effect immediately, not only on next serial reconnect.
            SendConfigureIfChangedLocked();

            // Forward any one-shot firmware command (e.g. identify) verbatim.
            if (parsed.FirmwareCommand is not null)
                ForwardFirmwareCommandLocked(parsed.FirmwareCommand);

            var session = SessionFor(eventSessionId);

            // --- subagent counter ---
            if (parsed.EventName == "PreToolUse" && parsed.ToolName == "Agent")
                session.SubagentCount++;
            else if (parsed.EventName == "SubagentStop")
                session.SubagentCount = Math.Max(0, session.SubagentCount - 1);

            // --- task counter ---
            if (parsed.TaskId is not null && parsed.TaskStatus is not null)
            {
                if (parsed.TaskStatus == "deleted")
                    session.Tasks.Remove(parsed.TaskId);
                else
                    session.Tasks[parsed.TaskId] = parsed.TaskStatus;
            }

            // --- state transitions ---
            if (parsed.IsToolActivity)
            {
                session.LastToolActivity = DateTimeOffset.Now;
                if (session.CurrentState != StateMapper.StateWorking)
                    SetStateLocked(session, StateMapper.StateWorking);
            }

            if (parsed.State is not null)
            {
                if (parsed.EventName == "PreCompact")
                    session.PreCompactState = session.CurrentState;
                SetStateLocked(session, parsed.State);
            }
            else if (parsed.EventName == "PostCompact")
            {
                var restore = session.PreCompactState ?? StateMapper.StateWorking;
                session.PreCompactState = null;
                SetStateLocked(session, restore);
            }

            EmitSnapshotIfChangedLocked(session, parsed.EventName, parsed.Ts);
        }
    }

    private void ForwardFirmwareCommandLocked(JsonNode cmd)
    {
        var cmdJson = cmd.ToJsonString();
        if (!_serial.IsOpen) _serial.TryOpen();
        if (_serial.WriteLine(cmdJson))
            Log.Info($"[bridge] fw-cmd -> {cmdJson}");
        else
            Log.Warn($"[bridge] fw-cmd dropped (serial closed): {cmdJson}");
    }

    private void SetStateLocked(SessionState session, string newState)
    {
        if (newState == session.CurrentState) return;
        session.CurrentState = newState;
        session.LastStateChange = DateTimeOffset.Now;
        if (newState == StateMapper.StateWorking)
            session.LastToolActivity = DateTimeOffset.Now;

        // Task-list "graduation" — when a session goes idle and has no
        // active tasks, clear the completed counter too. Otherwise
        // tasks_completed would linger forever after a session finishes
        // a batch ("5 done!" stuck on display until next prompt).
        if (newState == StateMapper.StateIdle && session.TasksActive == 0)
            session.Tasks.Clear();
    }

    private void EmitSnapshotIfChangedLocked(SessionState session, string? eventName, JsonNode? ts)
    {
        if (session.CurrentState is null) return;
        var tasksActive    = session.TasksActive;
        var tasksCompleted = session.TasksCompleted;
        // Re-read route on each emit so /claude-status:show changes
        // take effect at the next snapshot without a bridge restart.
        var clientSlot = _broker.ReadRouteForSession(session.SessionId);

        if (session.CurrentState == session.LastEmittedState
            && session.SubagentCount == session.LastEmittedSubagentCount
            && tasksActive == session.LastEmittedTasksActive
            && tasksCompleted == session.LastEmittedTasksCompleted
            && clientSlot == session.LastEmittedClientSlot)
            return;

        var line = StateMapper.BuildDeviceLine(
            session.CurrentState, clientSlot, session.SubagentCount,
            tasksActive, tasksCompleted, eventName, ts);
        if (!_serial.IsOpen) _serial.TryOpen();
        if (_serial.WriteLine(line))
        {
            Log.Info($"[bridge] -> {line}");
            session.LastEmit = DateTimeOffset.Now;
        }
        else
        {
            Log.Warn($"[bridge] dropped (serial closed): {line}");
        }

        session.LastEmittedState           = session.CurrentState;
        session.LastEmittedSubagentCount   = session.SubagentCount;
        session.LastEmittedTasksActive     = tasksActive;
        session.LastEmittedTasksCompleted  = tasksCompleted;
        session.LastEmittedClientSlot      = clientSlot;
    }

    // ============================================================
    // Serial monitoring
    // ============================================================

    private async Task SerialMonitorAsync(CancellationToken ct)
    {
        var previouslyAvailable = _serial.IsDeviceAvailable();
        Log.Info($"[bridge] serial monitor: {_options.ComPort} initially {(previouslyAvailable ? "available" : "unavailable")}, polling every {_options.SerialPollIntervalMs}ms");
        if (previouslyAvailable && _serial.IsOpen) lock (_lock) { SendConfigureIfChangedLocked(); }
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(_options.SerialPollIntervalMs, ct);
                var available = _serial.IsDeviceAvailable();

                if (available && !previouslyAvailable)
                {
                    Log.Info($"[bridge] device on {_options.ComPort} appeared");
                    if (_serial.TryOpen())
                    {
                        lock (_lock)
                        {
                            SendConfigureIfChangedLocked();
                            ForceReemitAllSessionsLocked();
                        }
                    }
                }
                else if (!available && previouslyAvailable)
                {
                    Log.Warn($"[bridge] device on {_options.ComPort} disappeared");
                    _serial.CloseIfOpen();
                }

                previouslyAvailable = available;
            }
        }
        catch (OperationCanceledException) { }
    }

    private void ForceReemitAllSessionsLocked()
    {
        foreach (var session in _sessions.Values)
        {
            if (session.CurrentState is null) continue;
            session.InvalidateLastEmit();
            EmitSnapshotIfChangedLocked(session, "device-reconnected", null);
        }
    }

    // ============================================================
    // Configure pass-through (panel_layout.json -> firmware)
    // ============================================================

    private void SendConfigureIfChangedLocked()
    {
        var layout = _broker.ReadPanelLayout();
        if (layout is null) return;
        var doc = new JsonObject
        {
            ["type"]         = "configure",
            ["panel_count"]  = layout.PanelCount,
            ["panel_width"]  = layout.PanelWidth,
            ["panel_height"] = layout.PanelHeight,
            ["layout"]       = layout.Layout,
            ["first_id"]     = layout.FirstId,
        };
        var json = doc.ToJsonString();
        if (json == _lastConfiguredJson) return;
        if (!_serial.IsOpen) _serial.TryOpen();
        if (_serial.WriteLine(json))
        {
            Log.Info($"[bridge] configure -> {json}");
            _lastConfiguredJson = json;
        }
        else
        {
            Log.Warn($"[bridge] configure dropped (serial closed): {json}");
        }
    }

    // ============================================================
    // Thinking + interrupt heuristics
    // ============================================================

    private async Task ThinkingTickerAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(1000, ct);
                lock (_lock)
                {
                    foreach (var session in _sessions.Values)
                    {
                        // working -> thinking after ThinkingIdleMs of no tool activity
                        if (session.CurrentState == StateMapper.StateWorking)
                        {
                            var idle = (DateTimeOffset.Now - session.LastToolActivity).TotalMilliseconds;
                            if (idle > _options.ThinkingIdleMs)
                            {
                                SetStateLocked(session, StateMapper.StateThinking);
                                EmitSnapshotIfChangedLocked(session, "thinking-heuristic", null);
                            }
                        }
                        // thinking -> idle after InterruptIdleMs of being stuck
                        else if (session.CurrentState == StateMapper.StateThinking)
                        {
                            var stuckFor = (DateTimeOffset.Now - session.LastStateChange).TotalMilliseconds;
                            if (stuckFor > _options.InterruptIdleMs)
                            {
                                SetStateLocked(session, StateMapper.StateIdle);
                                EmitSnapshotIfChangedLocked(session, "interrupt-heuristic", null);
                            }
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    // ============================================================
    // Subscription watchdog (single-subscribe only; replaced in Phase 3)
    // ============================================================

    private Task StartSubscriptionWatchdog(
        BrokerClient.BrokerEndpoint current,
        CancellationTokenSource subCts)
    {
        var intervalMs = _options.RescanIntervalMs;
        return Task.Run(async () =>
        {
            try
            {
                while (!subCts.IsCancellationRequested)
                {
                    await Task.Delay(intervalMs, subCts.Token);

                    var pinned = _broker.ReadPinnedSessionId();
                    if (pinned is not null)
                    {
                        if (pinned != current.SessionId)
                        {
                            Log.Info($"[bridge] pin points to {Shorten(pinned)}, switching");
                            subCts.Cancel();
                            return;
                        }
                        continue;
                    }

                    var latest = _broker.FindNewestBroker();
                    if (latest is not null && latest.SessionId != current.SessionId)
                    {
                        Log.Info($"[bridge] newer session detected ({Shorten(latest.SessionId)}), switching");
                        subCts.Cancel();
                        return;
                    }
                }
            }
            catch (OperationCanceledException) { }
        });
    }

    // ============================================================
    // Helpers
    // ============================================================

    private static string Shorten(string id) =>
        id.Length <= 8 ? id : id[..8];

    private static async Task SafeDelay(int ms, CancellationToken ct)
    {
        try { await Task.Delay(ms, ct); }
        catch (OperationCanceledException) { }
    }
}
