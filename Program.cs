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

Log.Info($"[bridge] mirror_dir={options.ResolvedMirrorDir}");
Log.Info($"[bridge] com_port={options.ComPort} baud={options.BaudRate}");
Log.Info($"[bridge] rescan_interval={options.RescanIntervalMs}ms thinking_idle={options.ThinkingIdleMs}ms");

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    if (!cts.IsCancellationRequested)
    {
        Log.Info("[bridge] shutdown requested, draining...");
        cts.Cancel();
    }
};

using var serial = new SerialOutput(options);
var broker = new BrokerClient(options);

var initialPin = broker.ReadPinnedSessionId();
Log.Info(initialPin is null
    ? "[bridge] pin: (none; auto-switching to newest session)"
    : $"[bridge] pin: {ShortenPin(initialPin)} (auto-switching disabled)");

var runner = new BridgeRunner(options, broker, serial);

try
{
    await runner.RunAsync(cts.Token);
}
catch (OperationCanceledException)
{
    // expected on Ctrl+C
}

Log.Info("[bridge] exiting");
return 0;

static string ShortenPin(string id) => id.Length <= 8 ? id : id[..8];
