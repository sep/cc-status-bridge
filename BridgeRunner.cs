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

    private string? _lastConfiguredJson;

    // Collision tracking: when a hint was last sent for a given slot.
    private readonly Dictionary<string, DateTimeOffset> _lastCollisionHint = new();

    // ============================================================
    // Aggregate state — exposed to the tray UI for icon coloring.
    // ============================================================

    /// <summary>
    /// Fires whenever the "loudest" state across all subscribed sessions
    /// changes. Argument is one of: "error", "blocked", "compacting",
    /// "working", "thinking", "idle", or "(none)" if no session has any
    /// state yet (or all sessions are gone).
    /// </summary>
    public event Action<string>? AggregateStateChanged;

    private string _lastAggregate = "(none)";

    private void RaiseAggregateStateLocked()
    {
        var aggregate = ComputeAggregateLocked();
        if (aggregate == _lastAggregate) return;
        _lastAggregate = aggregate;
        var handler = AggregateStateChanged;
        if (handler is not null) Task.Run(() => handler(aggregate));
    }

    private string ComputeAggregateLocked()
    {
        // Loudest first: error > blocked > compacting > working > thinking > idle
        var bits = 0;
        const int B_ERROR      = 1 << 5;
        const int B_BLOCKED    = 1 << 4;
        const int B_COMPACTING = 1 << 3;
        const int B_WORKING    = 1 << 2;
        const int B_THINKING   = 1 << 1;
        const int B_IDLE       = 1 << 0;
        foreach (var session in _sessions.Values)
        {
            switch (session.CurrentState)
            {
                case "error":      bits |= B_ERROR; break;
                case "blocked":    bits |= B_BLOCKED; break;
                case "compacting": bits |= B_COMPACTING; break;
                case "working":    bits |= B_WORKING; break;
                case "thinking":   bits |= B_THINKING; break;
                case "idle":       bits |= B_IDLE; break;
            }
        }
        if ((bits & B_ERROR)      != 0) return "error";
        if ((bits & B_BLOCKED)    != 0) return "blocked";
        if ((bits & B_COMPACTING) != 0) return "compacting";
        if ((bits & B_WORKING)    != 0) return "working";
        if ((bits & B_THINKING)   != 0) return "thinking";
        if ((bits & B_IDLE)       != 0) return "idle";
        return "(none)";
    }

    public BridgeRunner(BridgeOptions options, BrokerClient broker, SerialOutput serial)
    {
        _options = options;
        _broker = broker;
        _serial = serial;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        var thinkingTicker  = Task.Run(() => ThinkingTickerAsync(ct), ct);
        var serialMonitor   = Task.Run(() => SerialMonitorAsync(ct), ct);
        var collisionTicker = Task.Run(() => CollisionTickerAsync(ct), ct);
        var pinger          = Task.Run(() => PingerAsync(ct), ct);
        try
        {
            await SubscriptionManagerAsync(ct);
        }
        finally
        {
            try { await thinkingTicker; }  catch { }
            try { await serialMonitor; }   catch { }
            try { await collisionTicker; } catch { }
            try { await pinger; }          catch { }
        }
    }

    // ============================================================
    // Subscription manager (multi-session, v0.2.0)
    //
    // Discovers every Claude session that has an active broker, opens a
    // SUB stream to each one, and routes their events through HandleEvent
    // (which already keys per-session state by session_id). Sessions
    // that the user has explicitly hidden via /claude-status:hide are
    // skipped entirely. Sessions with no route get displayed at the
    // firmware's default slot (FIRMWARE.md §3.2). Sessions with explicit
    // routes get their `client` field set in the wire protocol.
    // ============================================================

    private sealed class ActiveSubscription
    {
        public Task Task = null!;
        public CancellationTokenSource Cts = null!;
        public BrokerClient.BrokerEndpoint Endpoint = null!;
    }

    private async Task SubscriptionManagerAsync(CancellationToken ct)
    {
        var subs = new Dictionary<string, ActiveSubscription>(StringComparer.Ordinal);
        var loggedEmpty = false;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                // Make sure serial is open if the device is plugged in;
                // configure / reemit happen in SerialMonitorAsync.
                if (!_serial.IsOpen) _serial.TryOpen();

                var endpoints = _broker.FindAllBrokers().ToList();
                var hidden = _broker.ReadHiddenSessions();
                var wanted = endpoints
                    .Where(e => !hidden.Contains(e.SessionId))
                    .ToList();
                var wantedIds = new HashSet<string>(
                    wanted.Select(e => e.SessionId), StringComparer.Ordinal);

                if (wanted.Count == 0 && subs.Count == 0)
                {
                    if (!loggedEmpty)
                    {
                        Log.Info("[bridge] no active sessions to subscribe to; waiting...");
                        loggedEmpty = true;
                    }
                }
                else
                {
                    loggedEmpty = false;
                }

                // Add new subscriptions for sessions we don't yet have one for.
                foreach (var endpoint in wanted)
                {
                    if (subs.ContainsKey(endpoint.SessionId)) continue;
                    Log.Info($"[bridge] subscribing to session {Shorten(endpoint.SessionId)} on port {endpoint.Port}");
                    var subCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    var task = Task.Run(() => SubscribeOneAsync(endpoint, subCts.Token), subCts.Token);
                    subs[endpoint.SessionId] = new ActiveSubscription
                    {
                        Task = task, Cts = subCts, Endpoint = endpoint,
                    };
                }

                // Tear down subscriptions for sessions that vanished or were hidden.
                var toRemove = subs.Keys.Where(k => !wantedIds.Contains(k)).ToList();
                foreach (var id in toRemove)
                {
                    var entry = subs[id];
                    Log.Info($"[bridge] unsubscribing from session {Shorten(id)}");
                    subs.Remove(id);
                    entry.Cts.Cancel();
                    try { await entry.Task; } catch { }
                    entry.Cts.Dispose();
                    lock (_lock) _sessions.Remove(id);
                }

                await SafeDelay(_options.RescanIntervalMs, ct);
            }
        }
        finally
        {
            // Clean shutdown: cancel every active subscription.
            foreach (var (_, entry) in subs)
            {
                try { entry.Cts.Cancel(); } catch { }
            }
            foreach (var (_, entry) in subs)
            {
                try { await entry.Task; } catch { }
                try { entry.Cts.Dispose(); } catch { }
            }
        }
    }

    private async Task SubscribeOneAsync(
        BrokerClient.BrokerEndpoint endpoint, CancellationToken ct)
    {
        try
        {
            await foreach (var rawLine in _broker.StreamEvents(endpoint, ct))
                HandleEvent(rawLine);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            Log.Warn($"[bridge] subscription error for {Shorten(endpoint.SessionId)}: {ex.Message}");
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

        // Multi-subscribe: dispatch by the event's own session_id.
        JsonNode? rawNode = null;
        try { rawNode = JsonNode.Parse(rawLine); } catch { /* tolerated */ }
        var eventSessionId = rawNode?["session_id"]?.GetValue<string>();
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

        RaiseAggregateStateLocked();
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
    // Collision detection — emit a {"type":"hint","kind":"collision",...}
    // to the firmware whenever > 1 active session resolves to the same
    // slot. Active = LastEmit within the last `activeWindowMs`. Hint is
    // re-emitted every `hintIntervalMs` while the condition persists;
    // firmware decays its overlay after a few seconds of silence per
    // FIRMWARE.md §8.
    // ============================================================

    private async Task CollisionTickerAsync(CancellationToken ct)
    {
        const int hintIntervalMs   = 3000;
        const int activeWindowMs   = 30000;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(hintIntervalMs, ct);
                lock (_lock) CheckCollisionsLocked(activeWindowMs);
            }
        }
        catch (OperationCanceledException) { }
    }

    private void CheckCollisionsLocked(int activeWindowMs)
    {
        var now = DateTimeOffset.Now;
        var defaultSlot = (_broker.ReadPanelLayout()?.FirstId ?? 1).ToString();
        var slotCounts = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var session in _sessions.Values)
        {
            if (session.CurrentState is null) continue;
            if ((now - session.LastEmit).TotalMilliseconds > activeWindowMs) continue;

            var slot = _broker.ReadRouteForSession(session.SessionId) ?? defaultSlot;
            if (slot == "_hidden") continue;

            slotCounts[slot] = slotCounts.GetValueOrDefault(slot) + 1;
        }

        foreach (var (slot, count) in slotCounts)
        {
            if (count <= 1) continue;
            var hint = new JsonObject
            {
                ["type"]     = "hint",
                ["client"]   = slot,
                ["kind"]     = "collision",
                ["sessions"] = count,
            };
            var json = hint.ToJsonString();
            if (!_serial.IsOpen) _serial.TryOpen();
            if (_serial.WriteLine(json))
            {
                _lastCollisionHint[slot] = now;
                Log.Info($"[bridge] collision-hint -> {json}");
            }
        }
    }

    // ============================================================
    // PING / PONG liveness ticker (FIRMWARE.md §8 v1.2)
    //
    // Sends `{"type":"ping","seq":N,"ts":...}` every PingIntervalMs and
    // listens for matching pongs via SerialOutput's LineReceived event.
    // If the most recent ping doesn't get acked within PingTimeoutMs of
    // its send, the missed counter increments. Crossing
    // PingMissedThreshold logs `device unresponsive` ONCE; recovery
    // logs `device responsive again` ONCE. Routine ping/pong traffic is
    // intentionally NOT logged — would noise up the log file at one
    // line per cycle. State updates keep flowing through SerialMonitor
    // regardless of pong arrival; PING/PONG is purely informational.
    // ============================================================

    private readonly object _pingLock = new();
    private long _lastSeqSent;
    private long _lastSeqAcked;
    private DateTimeOffset? _lastPingSentAt;
    private int _pingMissedConsecutive;
    private bool _pingWarned;

    private async Task PingerAsync(CancellationToken ct)
    {
        void OnLine(JsonNode doc)
        {
            if (doc["type"]?.GetValue<string>() != "pong") return;
            var seqValue = doc["seq"];
            if (seqValue is null) return;
            long seq;
            try { seq = seqValue.GetValue<long>(); }
            catch
            {
                try { seq = seqValue.GetValue<int>(); }
                catch { return; }
            }
            lock (_pingLock)
            {
                if (seq > _lastSeqAcked) _lastSeqAcked = seq;
            }
        }
        _serial.LineReceived += OnLine;

        var pingInterval = TimeSpan.FromMilliseconds(_options.PingIntervalMs);
        var pingTimeout  = TimeSpan.FromMilliseconds(_options.PingTimeoutMs);
        var threshold    = _options.PingMissedThreshold;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                // Evaluate ack-status of the most recent ping.
                lock (_pingLock)
                {
                    if (_lastPingSentAt is not null)
                    {
                        var elapsed = DateTimeOffset.Now - _lastPingSentAt.Value;
                        var acked   = _lastSeqAcked >= _lastSeqSent;

                        if (!acked && elapsed > pingTimeout)
                        {
                            _pingMissedConsecutive++;
                            if (_pingMissedConsecutive == threshold && !_pingWarned)
                            {
                                Log.Warn($"[bridge] device unresponsive ({_pingMissedConsecutive} missed pongs)");
                                _pingWarned = true;
                            }
                        }
                        else if (acked)
                        {
                            if (_pingWarned)
                            {
                                Log.Info("[bridge] device responsive again");
                                _pingWarned = false;
                            }
                            _pingMissedConsecutive = 0;
                        }
                    }
                }

                // Send a fresh ping if the port is open.
                if (_serial.IsOpen)
                {
                    long seq;
                    lock (_pingLock)
                    {
                        seq = ++_lastSeqSent;
                        _lastPingSentAt = DateTimeOffset.Now;
                    }
                    var ts = DateTimeOffset.Now.ToUnixTimeMilliseconds() / 1000.0;
                    var ping = new JsonObject
                    {
                        ["type"] = "ping",
                        ["seq"]  = seq,
                        ["ts"]   = ts,
                    };
                    // Don't log; per-5s noise would drown the rest of the log.
                    _serial.WriteLine(ping.ToJsonString());
                }

                try { await Task.Delay(pingInterval, ct); }
                catch (OperationCanceledException) { break; }
            }
        }
        finally
        {
            _serial.LineReceived -= OnLine;
        }
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
