using System.Text.Json.Nodes;

namespace ClaudeStatusBridge;

/// <summary>
/// Owns the bridge's state machine: subscribes to the broker, interprets
/// lifecycle events into a small rendered-state lexicon plus a subagent
/// counter, and writes state snapshots to the serial port. One instance
/// per process.
/// </summary>
public sealed class BridgeRunner
{
    private readonly BridgeOptions _options;
    private readonly BrokerClient _broker;
    private readonly SerialOutput _serial;

    private readonly object _lock = new();
    private string? _currentState;
    private string? _preCompactState;
    private int _subagentCount;
    private readonly Dictionary<string, string> _tasks = new();
    private string? _lastEmittedState;
    private int _lastEmittedSubagentCount = -1;
    private int _lastEmittedTasksActive = -1;
    private int _lastEmittedTasksCompleted = -1;
    private string? _lastEmittedClientSlot;
    private DateTimeOffset _lastToolActivity = DateTimeOffset.Now;
    private string? _currentSessionId;

    public BridgeRunner(BridgeOptions options, BrokerClient broker, SerialOutput serial)
    {
        _options = options;
        _broker = broker;
        _serial = serial;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        var thinkingTicker = Task.Run(() => ThinkingTickerAsync(ct), ct);
        var serialMonitor = Task.Run(() => SerialMonitorAsync(ct), ct);
        try
        {
            await SubscriptionLoopAsync(ct);
        }
        finally
        {
            try { await thinkingTicker; } catch { }
            try { await serialMonitor; } catch { }
        }
    }

    private async Task SubscriptionLoopAsync(CancellationToken ct)
    {
        string? lastEmptyReason = null;

        while (!ct.IsCancellationRequested)
        {
            if (!_serial.IsOpen && !_serial.TryOpen())
                await SafeDelay(_options.SerialReopenDelayMs, ct);

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
            ResetSessionStateLocked(endpoint.SessionId);

            using var subCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var watchdog = StartSubscriptionWatchdog(endpoint, subCts);

            try
            {
                await foreach (var rawLine in _broker.StreamEvents(endpoint, subCts.Token))
                {
                    HandleEvent(rawLine);
                }
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

    private void ResetSessionStateLocked(string sessionId)
    {
        lock (_lock)
        {
            if (sessionId == _currentSessionId) return;
            _currentSessionId = sessionId;
            _subagentCount = 0;
            _tasks.Clear();
            _preCompactState = null;
            // Don't reset _currentState — let the next event (or replay) set it.
        }
    }

    private int TasksActive =>
        _tasks.Values.Count(s => s == "pending" || s == "in_progress");

    private int TasksCompleted =>
        _tasks.Values.Count(s => s == "completed");

    private void HandleEvent(string rawLine)
    {
        var parsed = StateMapper.Parse(rawLine);
        if (parsed is null) return;

        lock (_lock)
        {
            // --- subagent counter ---
            // Claude Code's subagent-spawning tool is named "Agent" on the
            // wire (not "Task" as one might guess from the user-facing term).
            if (parsed.EventName == "PreToolUse" && parsed.ToolName == "Agent")
                _subagentCount++;
            else if (parsed.EventName == "SubagentStop")
                _subagentCount = Math.Max(0, _subagentCount - 1);

            // --- task counter ---
            if (parsed.TaskId is not null && parsed.TaskStatus is not null)
            {
                if (parsed.TaskStatus == "deleted")
                    _tasks.Remove(parsed.TaskId);
                else
                    _tasks[parsed.TaskId] = parsed.TaskStatus;
            }

            // --- state transitions ---
            if (parsed.IsToolActivity)
            {
                _lastToolActivity = DateTimeOffset.Now;
                if (_currentState != StateMapper.StateWorking)
                    SetStateLocked(StateMapper.StateWorking);
            }

            if (parsed.State is not null)
            {
                if (parsed.EventName == "PreCompact")
                    _preCompactState = _currentState;
                SetStateLocked(parsed.State);
            }
            else if (parsed.EventName == "PostCompact")
            {
                var restore = _preCompactState ?? StateMapper.StateWorking;
                _preCompactState = null;
                SetStateLocked(restore);
            }

            EmitSnapshotIfChangedLocked(parsed.EventName, parsed.Ts);
        }
    }

    private async Task SerialMonitorAsync(CancellationToken ct)
    {
        var previouslyAvailable = _serial.IsDeviceAvailable();
        Log.Info($"[bridge] serial monitor: {_options.ComPort} initially {(previouslyAvailable ? "available" : "unavailable")}, polling every {_options.SerialPollIntervalMs}ms");
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
                        lock (_lock) ForceReemitCurrentStateLocked();
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

    private void ForceReemitCurrentStateLocked()
    {
        if (_currentState is null) return;
        _lastEmittedState = null;
        _lastEmittedSubagentCount = -1;
        _lastEmittedTasksActive = -1;
        _lastEmittedTasksCompleted = -1;
        _lastEmittedClientSlot = null;
        EmitSnapshotIfChangedLocked("device-reconnected", null);
    }

    private async Task ThinkingTickerAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(1000, ct);
                lock (_lock)
                {
                    if (_currentState == StateMapper.StateWorking)
                    {
                        var idle = (DateTimeOffset.Now - _lastToolActivity).TotalMilliseconds;
                        if (idle > _options.ThinkingIdleMs)
                        {
                            SetStateLocked(StateMapper.StateThinking);
                            EmitSnapshotIfChangedLocked("thinking-heuristic", null);
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private void SetStateLocked(string newState)
    {
        if (newState == _currentState) return;
        _currentState = newState;
        if (newState == StateMapper.StateWorking)
            _lastToolActivity = DateTimeOffset.Now;
    }

    private void EmitSnapshotIfChangedLocked(string? eventName, JsonNode? ts)
    {
        if (_currentState is null) return;
        var tasksActive = TasksActive;
        var tasksCompleted = TasksCompleted;
        // Re-read route on each emit so /claude-status:route changes take
        // effect at the next snapshot without a bridge restart.
        var clientSlot = _currentSessionId is null
            ? null
            : _broker.ReadRouteForSession(_currentSessionId);

        if (_currentState == _lastEmittedState
            && _subagentCount == _lastEmittedSubagentCount
            && tasksActive == _lastEmittedTasksActive
            && tasksCompleted == _lastEmittedTasksCompleted
            && clientSlot == _lastEmittedClientSlot)
            return;

        var line = StateMapper.BuildDeviceLine(
            _currentState, clientSlot, _subagentCount, tasksActive, tasksCompleted, eventName, ts);
        if (!_serial.IsOpen) _serial.TryOpen();
        if (_serial.WriteLine(line))
            Log.Info($"[bridge] -> {line}");
        else
            Log.Warn($"[bridge] dropped (serial closed): {line}");

        _lastEmittedState = _currentState;
        _lastEmittedSubagentCount = _subagentCount;
        _lastEmittedTasksActive = tasksActive;
        _lastEmittedTasksCompleted = tasksCompleted;
        _lastEmittedClientSlot = clientSlot;
    }

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

    private static string Shorten(string id) =>
        id.Length <= 8 ? id : id[..8];

    private static async Task SafeDelay(int ms, CancellationToken ct)
    {
        try { await Task.Delay(ms, ct); }
        catch (OperationCanceledException) { }
    }
}
