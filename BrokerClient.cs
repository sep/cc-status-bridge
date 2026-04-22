using System.Net.Sockets;
using System.Text;
using System.Text.Json;
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

    public BrokerEndpoint? FindNewestBroker()
    {
        var sessionsDir = Path.Combine(_options.ResolvedMirrorDir, "sessions");
        if (!Directory.Exists(sessionsDir)) return null;

        BrokerEndpoint? best = null;
        foreach (var sessionDir in Directory.EnumerateDirectories(sessionsDir))
        {
            var stateFile = Path.Combine(sessionDir, "broker.json");
            if (!File.Exists(stateFile)) continue;
            var mtime = File.GetLastWriteTimeUtc(stateFile);
            int port;
            string sessionId;
            try
            {
                var node = JsonNode.Parse(File.ReadAllText(stateFile));
                port = node?["port"]?.GetValue<int>() ?? 0;
                sessionId = node?["session_id"]?.GetValue<string>() ?? Path.GetFileName(sessionDir);
                if (port <= 0) continue;
            }
            catch (Exception)
            {
                continue;
            }
            if (best is null || mtime > best.Mtime)
                best = new BrokerEndpoint(sessionId, port, mtime);
        }
        return best;
    }

    public BrokerEndpoint? FindBrokerForSession(string sessionId)
    {
        var stateFile = Path.Combine(_options.ResolvedMirrorDir, "sessions", sessionId, "broker.json");
        if (!File.Exists(stateFile)) return null;
        try
        {
            var node = JsonNode.Parse(File.ReadAllText(stateFile));
            var port = node?["port"]?.GetValue<int>() ?? 0;
            if (port <= 0) return null;
            return new BrokerEndpoint(sessionId, port, File.GetLastWriteTimeUtc(stateFile));
        }
        catch (Exception)
        {
            return null;
        }
    }

    public string? ReadPinnedSessionId()
    {
        var pinFile = Path.Combine(_options.ResolvedMirrorDir, "pin.json");
        if (!File.Exists(pinFile)) return null;
        try
        {
            var node = JsonNode.Parse(File.ReadAllText(pinFile));
            var sid = node?["session_id"]?.GetValue<string>();
            return string.IsNullOrWhiteSpace(sid) ? null : sid;
        }
        catch (Exception)
        {
            return null;
        }
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
        catch (OperationCanceledException)
        {
            yield break;
        }

        var stream = client.GetStream();
        var handshake = Encoding.ASCII.GetBytes("SUB\n");
        try
        {
            await stream.WriteAsync(handshake, ct);
        }
        catch (OperationCanceledException)
        {
            yield break;
        }

        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (!ct.IsCancellationRequested)
        {
            string? line;
            try
            {
                line = await reader.ReadLineAsync(ct);
            }
            catch (OperationCanceledException)
            {
                yield break;
            }
            catch (IOException)
            {
                yield break;
            }
            if (line is null) yield break;
            if (line.Length == 0) continue;
            yield return line;
        }
    }
}
