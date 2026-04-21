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

    public async IAsyncEnumerable<string> StreamEvents(
        BrokerEndpoint endpoint,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        using var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", endpoint.Port, ct);
        var stream = client.GetStream();
        var handshake = Encoding.ASCII.GetBytes("SUB\n");
        await stream.WriteAsync(handshake, ct);

        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null) yield break;
            if (line.Length == 0) continue;
            yield return line;
        }
    }
}
