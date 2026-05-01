using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace ClaudeStatusBridge;

public sealed class BrokerClient
{
    private readonly BridgeOptions _options;

    public BrokerClient(BridgeOptions options)
    {
        _options = options;
    }

    public record BrokerEndpoint(string SessionId, int Port, DateTime Mtime);

    /// <summary>
    /// Yields every directory the bridge should inspect for broker state
    /// (sessions/ subtrees and pin.json). Ordered by priority, de-duplicated.
    ///
    /// Priority:
    ///  1. Explicit <see cref="BridgeOptions.MirrorDir"/> override — if set,
    ///     nothing else is searched.
    ///  2. Windows mirror path at <c>~/.claude-status</c>. Populated by
    ///     WSL-side broker.py when mirroring for a Windows bridge. Harmless
    ///     on native macOS/Linux (the dir simply won't exist).
    ///  3. Every subdirectory of <c>~/.claude/plugins/data/</c>. This is
    ///     where Claude Code stores plugin state on native installs. We
    ///     glob rather than hardcode because the subdirectory name
    ///     (plugin-name + marketplace-name) isn't stable.
    /// </summary>
    public IEnumerable<string> CandidateDataRoots()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var dir in EnumerateCandidates())
        {
            if (string.IsNullOrEmpty(dir)) continue;
            if (!Directory.Exists(dir)) continue;
            string canonical;
            try { canonical = Path.GetFullPath(dir); }
            catch { continue; }
            if (seen.Add(canonical))
                yield return canonical;
        }
    }

    private IEnumerable<string> EnumerateCandidates()
    {
        var explicitDir = _options.ResolvedMirrorDir;
        if (!string.IsNullOrEmpty(explicitDir))
        {
            yield return explicitDir;
            yield break;  // explicit override wins outright
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home)) yield break;

        yield return Path.Combine(home, ".claude-status");

        var pluginsData = Path.Combine(home, ".claude", "plugins", "data");
        if (Directory.Exists(pluginsData))
        {
            foreach (var sub in Directory.EnumerateDirectories(pluginsData))
                yield return sub;
        }
    }

    public BrokerEndpoint? FindNewestBroker()
    {
        BrokerEndpoint? best = null;

        foreach (var root in CandidateDataRoots())
        {
            var sessionsDir = Path.Combine(root, "sessions");
            if (!Directory.Exists(sessionsDir)) continue;

            foreach (var sessionDir in Directory.EnumerateDirectories(sessionsDir))
            {
                var stateFile = Path.Combine(sessionDir, "broker.json");
                if (!File.Exists(stateFile)) continue;

                var endpoint = ReadEndpointFromStateFile(stateFile, sessionDir);
                if (endpoint is null) continue;

                if (best is null || endpoint.Mtime > best.Mtime)
                    best = endpoint;
            }
        }
        return best;
    }

    /// <summary>
    /// Yields every active broker (every session_id with a current
    /// broker.json) found across all candidate data roots, deduplicated
    /// by session_id. Used by the v0.2.0 multi-session subscription
    /// manager.
    /// </summary>
    public IEnumerable<BrokerEndpoint> FindAllBrokers()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var root in CandidateDataRoots())
        {
            var sessionsDir = Path.Combine(root, "sessions");
            if (!Directory.Exists(sessionsDir)) continue;
            foreach (var sessionDir in Directory.EnumerateDirectories(sessionsDir))
            {
                var stateFile = Path.Combine(sessionDir, "broker.json");
                if (!File.Exists(stateFile)) continue;
                var endpoint = ReadEndpointFromStateFile(stateFile, sessionDir);
                if (endpoint is null) continue;
                if (!seen.Add(endpoint.SessionId)) continue;
                yield return endpoint;
            }
        }
    }

    /// <summary>
    /// Set of session_ids the user has explicitly hidden via
    /// /claude-status:hide. Encoded as routes.json entries with the
    /// special slot value "_hidden".
    /// </summary>
    public HashSet<string> ReadHiddenSessions()
    {
        var hidden = new HashSet<string>(StringComparer.Ordinal);
        foreach (var root in CandidateDataRoots())
        {
            var routesFile = Path.Combine(root, "routes.json");
            if (!File.Exists(routesFile)) continue;
            try
            {
                if (JsonNode.Parse(File.ReadAllText(routesFile)) is JsonObject obj)
                {
                    foreach (var kvp in obj)
                    {
                        if (kvp.Value?.GetValue<string>() == "_hidden")
                            hidden.Add(kvp.Key);
                    }
                }
            }
            catch { /* malformed; try next candidate */ }
        }
        return hidden;
    }

    public BrokerEndpoint? FindBrokerForSession(string sessionId)
    {
        foreach (var root in CandidateDataRoots())
        {
            var stateFile = Path.Combine(root, "sessions", sessionId, "broker.json");
            if (!File.Exists(stateFile)) continue;
            var endpoint = ReadEndpointFromStateFile(stateFile, fallbackSessionId: sessionId);
            if (endpoint is not null) return endpoint;
        }
        return null;
    }

    public string? ReadPinnedSessionId()
    {
        foreach (var root in CandidateDataRoots())
        {
            var pinFile = Path.Combine(root, "pin.json");
            if (!File.Exists(pinFile)) continue;
            try
            {
                var node = JsonNode.Parse(File.ReadAllText(pinFile));
                var sid = node?["session_id"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(sid)) return sid;
            }
            catch
            {
                // malformed pin file; try next candidate
            }
        }
        return null;
    }

    /// <summary>
    /// Panel layout written by the plugin's /claude-status:configure
    /// slash-command handler. Bridge sends this to the firmware on every
    /// successful serial connect (per FIRMWARE.md §8). Null when no
    /// layout has been configured — firmware then uses its compile-time
    /// default (single 64×32 panel).
    /// </summary>
    public sealed record PanelLayout(
        int PanelCount,
        int PanelWidth,
        int PanelHeight,
        string Layout,
        int FirstId);

    public PanelLayout? ReadPanelLayout()
    {
        foreach (var root in CandidateDataRoots())
        {
            var path = Path.Combine(root, "panel_layout.json");
            if (!File.Exists(path)) continue;
            try
            {
                var node = JsonNode.Parse(File.ReadAllText(path));
                if (node is null) continue;
                return new PanelLayout(
                    PanelCount:  node["panel_count"]?.GetValue<int>() ?? 1,
                    PanelWidth:  node["panel_width"]?.GetValue<int>() ?? 64,
                    PanelHeight: node["panel_height"]?.GetValue<int>() ?? 32,
                    Layout:      node["layout"]?.GetValue<string>() ?? "horizontal",
                    FirstId:     node["first_id"]?.GetValue<int>() ?? 1);
            }
            catch
            {
                // malformed; try next candidate
            }
        }
        return null;
    }

    /// <summary>
    /// Look up the display client slot for a session, written by the
    /// plugin's /claude-status:route slash-command handler. Returns null
    /// if no route is configured for this session.
    /// </summary>
    public string? ReadRouteForSession(string sessionId)
    {
        foreach (var root in CandidateDataRoots())
        {
            var routeFile = Path.Combine(root, "routes.json");
            if (!File.Exists(routeFile)) continue;
            try
            {
                var node = JsonNode.Parse(File.ReadAllText(routeFile));
                var slot = node?[sessionId]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(slot)) return slot;
            }
            catch
            {
                // malformed; try next candidate
            }
        }
        return null;
    }

    public BrokerEndpoint? PickEndpoint()
    {
        var pinned = ReadPinnedSessionId();
        if (pinned is not null)
            return FindBrokerForSession(pinned);
        return FindNewestBroker();
    }

    public async IAsyncEnumerable<string> StreamEvents(
        BrokerEndpoint endpoint,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        using var client = new TcpClient();
        try
        {
            await client.ConnectAsync("127.0.0.1", endpoint.Port, ct);
        }
        catch (OperationCanceledException) { yield break; }

        var stream = client.GetStream();
        var handshake = Encoding.ASCII.GetBytes("SUB\n");
        try
        {
            await stream.WriteAsync(handshake, ct);
        }
        catch (OperationCanceledException) { yield break; }

        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (!ct.IsCancellationRequested)
        {
            string? line;
            try
            {
                line = await reader.ReadLineAsync(ct);
            }
            catch (OperationCanceledException) { yield break; }
            catch (IOException) { yield break; }

            if (line is null) yield break;
            if (line.Length == 0) continue;
            yield return line;
        }
    }

    private static BrokerEndpoint? ReadEndpointFromStateFile(
        string stateFile,
        string? sessionDir = null,
        string? fallbackSessionId = null)
    {
        try
        {
            var node = JsonNode.Parse(File.ReadAllText(stateFile));
            var port = node?["port"]?.GetValue<int>() ?? 0;
            if (port <= 0) return null;
            var sessionId = node?["session_id"]?.GetValue<string>()
                         ?? fallbackSessionId
                         ?? (sessionDir is not null ? Path.GetFileName(sessionDir) : null);
            if (string.IsNullOrWhiteSpace(sessionId)) return null;
            return new BrokerEndpoint(sessionId, port, File.GetLastWriteTimeUtc(stateFile));
        }
        catch
        {
            return null;
        }
    }
}
