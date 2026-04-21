using ClaudeStatusBridge;
using Microsoft.Extensions.Configuration;

var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
    .AddEnvironmentVariables(prefix: "CSB_")
    .AddCommandLine(args)
    .Build();

var options = new BridgeOptions();
config.GetSection("Bridge").Bind(options);

Console.WriteLine($"[bridge] mirror_dir={options.ResolvedMirrorDir}");
Console.WriteLine($"[bridge] com_port={options.ComPort} baud={options.BaudRate}");

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

using var serial = new SerialOutput(options);
var broker = new BrokerClient(options);

await RunAsync(options, broker, serial, cts.Token);

Console.WriteLine("[bridge] exiting");

static async Task RunAsync(
    BridgeOptions options,
    BrokerClient broker,
    SerialOutput serial,
    CancellationToken ct)
{
    BrokerClient.BrokerEndpoint? currentEndpoint = null;

    while (!ct.IsCancellationRequested)
    {
        if (!serial.IsOpen && !serial.TryOpen())
        {
            await SafeDelay(options.SerialReopenDelayMs, ct);
        }

        var endpoint = broker.FindNewestBroker();
        if (endpoint is null)
        {
            Console.WriteLine("[bridge] no broker found, scanning...");
            await SafeDelay(options.ReconnectDelayMs, ct);
            continue;
        }

        if (currentEndpoint is null || currentEndpoint.SessionId != endpoint.SessionId)
        {
            Console.WriteLine($"[bridge] subscribing to session {endpoint.SessionId[..Math.Min(8, endpoint.SessionId.Length)]} on port {endpoint.Port}");
            currentEndpoint = endpoint;
        }

        try
        {
            await foreach (var rawLine in broker.StreamEvents(endpoint, ct))
            {
                var deviceLine = StateMapper.ToDeviceLine(rawLine);
                if (deviceLine is null) continue;
                if (!serial.IsOpen) serial.TryOpen();
                if (serial.WriteLine(deviceLine))
                    Console.WriteLine($"[bridge] -> {deviceLine}");
                else
                    Console.Error.WriteLine($"[bridge] dropped (serial closed): {deviceLine}");
            }
            Console.WriteLine("[bridge] broker closed connection");
            currentEndpoint = null;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[bridge] broker error: {ex.Message}");
            currentEndpoint = null;
        }

        await SafeDelay(options.ReconnectDelayMs, ct);
    }
}

static async Task SafeDelay(int ms, CancellationToken ct)
{
    try { await Task.Delay(ms, ct); }
    catch (OperationCanceledException) { }
}
