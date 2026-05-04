using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using ClaudeStatusBridge;

// CLI subcommand dispatch — these run synchronously and exit, no tray.
// Bridge is WinExe (Windows-subsystem), so the OS doesn't allocate a
// console for us; on the CLI path we attach to the parent shell's
// console so output flows through. The tray path leaves console state
// alone — silent launch, no flash.
if (args.Length > 0)
{
    ConsoleAttach.AttachToParentIfCli();

    switch (args[0].ToLowerInvariant())
    {
        case "install":   return Installer.Install();
        case "uninstall": return Installer.Uninstall();
        case "start":     return Installer.Start();
        case "stop":      return Installer.Stop();
        case "restart":   return Installer.Restart();
        case "find":      return Installer.Find(115200);
        case "match":     return Installer.Find(115200);  // legacy alias
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

// Default invocation: launch the Avalonia tray app. The bridge subscription
// runs as a background Task owned by TrayHost; the tray icon and menu are
// the entire user interface.
//
// We still take a single-instance lock so two stray bridges can't multiplex
// to the same serial port if (e.g.) the scheduled task fires twice.
using var singleInstance = SingleInstance.TryAcquire();
if (singleInstance is null)
{
    // Another instance is already running. Quietly exit; if a user
    // double-launched the EXE, the tray icon is still there from the
    // first one.
    return 0;
}

return TrayApp.BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

static void PrintHelp()
{
    Console.WriteLine(
        "ClaudeStatusBridge — transport for claude-status plugin\n" +
        "\n" +
        "Default invocation launches a system-tray application. The tray\n" +
        "icon is the entire UI; right-click for start/stop/logs/quit.\n" +
        "\n" +
        "On Windows the binary is Windows-subsystem (no console), so when\n" +
        "you invoke a subcommand from pwsh / cmd the shell does not block\n" +
        "waiting for it. Output still works — we attach to the parent\n" +
        "console — but it interleaves with the next prompt. For interactive\n" +
        "subcommands (`find`), invoke via `Start-Process -Wait`:\n" +
        "    Start-Process -Wait .\\ClaudeStatusBridge.exe -ArgumentList find\n" +
        "\n" +
        "Usage:\n" +
        "  ClaudeStatusBridge               Launch the tray app (default)\n" +
        "  ClaudeStatusBridge install       Register + start as a user-scope service\n" +
        "  ClaudeStatusBridge uninstall     Stop + deregister (idempotent)\n" +
        "  ClaudeStatusBridge start         Start the registered service\n" +
        "  ClaudeStatusBridge stop          Stop the running service (stays registered)\n" +
        "  ClaudeStatusBridge restart       Stop then start\n" +
        "  ClaudeStatusBridge find          Scan USB serial ports for a ClaudePanel\n" +
        "                                   and write the chosen port to appsettings.json\n" +
        "                                   (alias: `match`, kept for back-compat)\n" +
        "  ClaudeStatusBridge status        Show install / running / version\n" +
        "  ClaudeStatusBridge logs          Tail the bridge log (Ctrl+C to exit)\n" +
        "                                   --no-follow: print and exit\n" +
        "                                   --all: print entire file from start\n" +
        "  ClaudeStatusBridge version       Print version string\n" +
        "  ClaudeStatusBridge help          This message\n" +
        "\n" +
        "Per-user install mechanism by platform:\n" +
        "  Windows: HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Run\n" +
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
