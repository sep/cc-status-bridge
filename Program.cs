using ClaudeStatusBridge;
using Microsoft.Extensions.Configuration;

// On Windows, when we're launched without a parent console (scheduled
// task), the OS allocates a console window for our process. Hide it so
// service runs are silent. When launched from pwsh / cmd, the console is
// shared with the parent and we leave it visible.
ConsoleAttach.HideConsoleIfOwned();

// Subcommand dispatch. Anything other than the defaults falls through to
// running the bridge in the foreground.
if (args.Length > 0)
{
    switch (args[0].ToLowerInvariant())
    {
        case "install":   return Installer.Install();
        case "uninstall": return Installer.Uninstall();
        case "start":     return Installer.Start();
        case "stop":      return Installer.Stop();
        case "restart":   return Installer.Restart();
        case "match":     return Installer.Match(115200);
        case "status":    return Installer.Status();
        case "logs":
            // bridge logs           — last 50 lines, then follow
            // bridge logs --no-follow — last 50 lines, then exit
            // bridge logs --all     — entire file, then follow
            var follow = !args.Contains("--no-follow");
            var tail = args.Contains("--all") ? int.MaxValue : 50;
            return Installer.Logs(follow, tail);
        case "version":
        case "--version":
        case "-v":
            return Installer.PrintVersion();
        case "help":
        case "--help":
        case "-h":
            PrintHelp();
            return 0;
    }
}

// Load config from BOTH the runtime base directory (the .NET host's
// working dir, which for a single-file self-contained EXE may be a
// temp extract path) AND the directory containing the actual binary,
// since `bridge match` writes appsettings.json next to the binary.
// Whichever has a real file wins; later sources override earlier ones.
var binaryDir = Path.GetDirectoryName(Environment.ProcessPath ?? "")
                ?? AppContext.BaseDirectory;
var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
    .AddJsonFile(Path.Combine(binaryDir, "appsettings.json"), optional: true, reloadOnChange: false)
    .AddEnvironmentVariables(prefix: "CSB_")
    .AddCommandLine(args)
    .Build();

// Foreground / service mode below this point. Take a single-instance lock
// so multiple stray bridges can't pile up (even if the scheduler fires more
// than one launch).
using var singleInstance = SingleInstance.TryAcquire();
if (singleInstance is null)
{
    Log.Warn("[bridge] another instance is already running; exiting");
    return 0;
}

var options = new BridgeOptions();
config.GetSection("Bridge").Bind(options);

var broker = new BrokerClient(options);

var rootsPreview = string.Join(", ", broker.CandidateDataRoots().Take(4));
Log.Info($"[bridge] data roots: {(string.IsNullOrEmpty(rootsPreview) ? "(none found)" : rootsPreview)}");
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

static void PrintHelp()
{
    Console.WriteLine(
        "ClaudeStatusBridge — transport for claude-status plugin\n" +
        "\n" +
        "Usage:\n" +
        "  ClaudeStatusBridge               Run in the foreground (dev mode)\n" +
        "  ClaudeStatusBridge install       Register + start as a user-scope service\n" +
        "  ClaudeStatusBridge uninstall     Stop + deregister (idempotent)\n" +
        "  ClaudeStatusBridge start         Start the registered service\n" +
        "  ClaudeStatusBridge stop          Stop the running service (stays registered)\n" +
        "  ClaudeStatusBridge restart       Stop then start\n" +
        "  ClaudeStatusBridge match         Scan USB serial ports for a ClaudePanel\n" +
        "                                   and write the chosen port to appsettings.json\n" +
        "  ClaudeStatusBridge status        Show install / running / version\n" +
        "  ClaudeStatusBridge logs          Tail the bridge log (Ctrl+C to exit)\n" +
        "                                   --no-follow: print and exit\n" +
        "                                   --all: print entire file from start\n" +
        "  ClaudeStatusBridge version       Print version string\n" +
        "  ClaudeStatusBridge help          This message\n" +
        "\n" +
        "Per-user install mechanism by platform:\n" +
        "  Windows: Scheduled Task (no admin)\n" +
        "  macOS:   launchd LaunchAgent under ~/Library/LaunchAgents/\n" +
        "  Linux:   systemd --user unit under ~/.config/systemd/user/\n" +
        "\n" +
        "Configuration (appsettings.json / CLI / CSB_ env):\n" +
        "  Bridge:ComPort             (default COM4)\n" +
        "  Bridge:BaudRate            (default 115200)\n" +
        "  Bridge:MirrorDir           (explicit override; default = auto-discover)\n" +
        "  Bridge:RescanIntervalMs    (default 5000)\n" +
        "  Bridge:ThinkingIdleMs      (default 8000)\n" +
        "  Bridge:SerialPollIntervalMs (default 2000)\n");
}
