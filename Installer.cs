using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace ClaudeStatusBridge;

/// <summary>
/// Per-user install / uninstall / status for the claude-status bridge.
///
/// Windows → Scheduled Task (schtasks, runs at logon).
/// macOS   → launchd LaunchAgent (~/Library/LaunchAgents/...plist).
/// Linux   → systemd user unit (~/.config/systemd/user/...service).
///
/// All three are user-scope — no sudo / admin required.
/// </summary>
internal static class Installer
{
    private const string ServiceName = "ClaudeStatusBridge";
    private const string MacLabel    = "com.claude-status.bridge";
    private const string LinuxUnit   = "claude-status-bridge.service";

    public static int Install()
    {
        var exePath = ResolveExePath();
        Console.WriteLine($"[installer] binary: {exePath}");
        if (OperatingSystem.IsWindows()) return InstallWindows(exePath);
        if (OperatingSystem.IsMacOS())   return InstallMacOS(exePath);
        if (OperatingSystem.IsLinux())   return InstallLinux(exePath);
        Console.Error.WriteLine("[installer] unsupported platform");
        return 1;
    }

    public static int Uninstall()
    {
        if (OperatingSystem.IsWindows()) return UninstallWindows();
        if (OperatingSystem.IsMacOS())   return UninstallMacOS();
        if (OperatingSystem.IsLinux())   return UninstallLinux();
        Console.Error.WriteLine("[installer] unsupported platform");
        return 1;
    }

    public static int Status()
    {
        Console.WriteLine($"version:   {VersionString()}");
        Console.WriteLine($"platform:  {PlatformName()}");
        Console.WriteLine($"binary:    {ResolveExePath()}");

        if (OperatingSystem.IsWindows()) return StatusWindows();
        if (OperatingSystem.IsMacOS())   return StatusMacOS();
        if (OperatingSystem.IsLinux())   return StatusLinux();
        Console.Error.WriteLine("[installer] unsupported platform");
        return 1;
    }

    public static int PrintVersion()
    {
        Console.WriteLine(VersionString());
        return 0;
    }

    public static int Stop()
    {
        if (OperatingSystem.IsWindows())
            return Run("schtasks", "/End", "/TN", ServiceName);
        if (OperatingSystem.IsMacOS())
            return Run("launchctl", "unload", MacPlistPath());
        if (OperatingSystem.IsLinux())
            return Run("systemctl", "--user", "stop", LinuxUnit);
        Console.Error.WriteLine("[installer] unsupported platform");
        return 1;
    }

    public static int Start()
    {
        if (OperatingSystem.IsWindows())
            return Run("schtasks", "/Run", "/TN", ServiceName);
        if (OperatingSystem.IsMacOS())
            return Run("launchctl", "load", MacPlistPath());
        if (OperatingSystem.IsLinux())
            return Run("systemctl", "--user", "start", LinuxUnit);
        Console.Error.WriteLine("[installer] unsupported platform");
        return 1;
    }

    public static bool IsRegistered()
    {
        if (OperatingSystem.IsWindows())
            return RunQuiet("schtasks", "/Query", "/TN", ServiceName) == 0;
        if (OperatingSystem.IsMacOS())
            return File.Exists(MacPlistPath());
        if (OperatingSystem.IsLinux())
            return File.Exists(LinuxUnitPath());
        return false;
    }

    public static int Restart()
    {
        Stop();                          // ignore exit; may already be stopped
        System.Threading.Thread.Sleep(200);
        return Start();
    }

    /// <summary>
    /// Scan serial ports, probe each with PING, and write the chosen
    /// port to an appsettings.json next to the binary. Interactive.
    /// </summary>
    public static int Match(int baudRate)
    {
        Console.WriteLine("[match] scanning serial ports...");
        var ports = Discovery.EnumeratePorts();
        if (ports.Count == 0)
        {
            Console.Error.WriteLine("[match] no candidate serial ports found");
            Console.Error.WriteLine("[match] is your ClaudePanel hardware plugged in via USB?");
            return 1;
        }

        var results = ports.Select(p => Discovery.Probe(p, baudRate)).ToList();
        var matches = results.Where(r => r.IsClaudePanel).ToList();

        Console.WriteLine();
        Console.WriteLine($"  {"Port",-32}  Status");
        Console.WriteLine($"  {new string('-', 32)}  {new string('-', 50)}");
        foreach (var r in results)
        {
            var marker = r.IsClaudePanel ? "✓" : " ";
            var msg = r.IsClaudePanel ? $"ClaudePanel ({r.Detail})" : $"({r.Detail})";
            Console.WriteLine($"  {r.Port,-32} {marker} {msg}");
        }
        Console.WriteLine();

        string? chosen;
        if (matches.Count == 1)
        {
            chosen = matches[0].Port;
            Console.Write($"Set {chosen} as the bridge's serial port? [Y/n] ");
            var response = (Console.ReadLine() ?? "").Trim().ToLowerInvariant();
            if (response != "" && response != "y" && response != "yes")
            {
                Console.WriteLine("[match] cancelled");
                return 1;
            }
        }
        else if (matches.Count > 1)
        {
            Console.WriteLine("Multiple ClaudePanels responded:");
            for (var i = 0; i < matches.Count; i++)
                Console.WriteLine($"  {i + 1}) {matches[i].Port}  ({matches[i].Detail})");
            Console.Write($"Pick one [1-{matches.Count}] or 0 to cancel: ");
            chosen = ReadChoice(matches.Count, matches.Select(m => m.Port).ToList());
            if (chosen is null) { Console.WriteLine("[match] cancelled"); return 1; }
        }
        else
        {
            Console.WriteLine("No ClaudePanels responded to PING. Possible reasons:");
            Console.WriteLine("  - the device firmware doesn't speak the v1.2 protocol yet");
            Console.WriteLine("  - another application has the port open");
            Console.WriteLine("  - the device just rebooted and isn't ready to respond");
            Console.WriteLine();
            Console.WriteLine("If you know which port your ClaudePanel is on, pick it from");
            Console.WriteLine("the candidates and we'll write it to the config anyway:");
            for (var i = 0; i < ports.Count; i++)
                Console.WriteLine($"  {i + 1}) {ports[i]}");
            Console.Write($"Pick one [1-{ports.Count}] or 0 to cancel: ");
            chosen = ReadChoice(ports.Count, ports);
            if (chosen is null) { Console.WriteLine("[match] cancelled"); return 1; }
        }

        return WriteComPortConfig(chosen);
    }

    private static string? ReadChoice(int max, IList<string> options)
    {
        var input = (Console.ReadLine() ?? "").Trim();
        if (!int.TryParse(input, out var choice) || choice < 1 || choice > max)
            return null;
        return options[choice - 1];
    }

    private static int WriteComPortConfig(string comPort)
    {
        var binaryDir = Path.GetDirectoryName(Environment.ProcessPath ?? "")
                        ?? AppContext.BaseDirectory;
        var configPath = Path.Combine(binaryDir, "appsettings.json");

        System.Text.Json.Nodes.JsonObject root;
        if (File.Exists(configPath))
        {
            try
            {
                root = (System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(configPath))
                       as System.Text.Json.Nodes.JsonObject) ?? new();
            }
            catch
            {
                root = new();
            }
        }
        else
        {
            root = new();
        }

        if (root["Bridge"] is not System.Text.Json.Nodes.JsonObject bridge)
        {
            bridge = new System.Text.Json.Nodes.JsonObject();
            root["Bridge"] = bridge;
        }
        bridge["ComPort"] = comPort;

        try
        {
            File.WriteAllText(configPath,
                root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[match] failed to write {configPath}: {ex.Message}");
            return 1;
        }

        Console.WriteLine();
        Console.WriteLine($"[match] wrote {configPath}");
        Console.WriteLine($"[match] Bridge:ComPort = {comPort}");
        Console.WriteLine();
        Console.WriteLine("Restart the bridge to pick up the new config:");
        Console.WriteLine("  bridge restart");
        return 0;
    }

    public static int Logs(bool follow, int tailLines)
    {
        var path = Log.FilePath;
        if (path is null)
        {
            Console.Error.WriteLine("[bridge] cannot determine log file path on this platform");
            return 1;
        }
        if (!File.Exists(path))
        {
            Console.WriteLine($"[bridge] no log file yet at {path}");
            Console.WriteLine("[bridge] (the bridge writes to this file once it produces output)");
            return 0;
        }

        // Print the last `tailLines` lines from the file.
        PrintTail(path, tailLines);

        if (!follow) return 0;

        // Follow the file: poll for new content every 200ms until Ctrl+C.
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        fs.Seek(0, SeekOrigin.End);
        using var reader = new StreamReader(fs, System.Text.Encoding.UTF8);

        while (!cts.IsCancellationRequested)
        {
            var line = reader.ReadLine();
            if (line is null)
            {
                System.Threading.Thread.Sleep(200);
                continue;
            }
            Console.WriteLine(line);
        }
        return 0;
    }

    private static void PrintTail(string path, int tailLines)
    {
        try
        {
            string[] all;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete))
            using (var sr = new StreamReader(fs, System.Text.Encoding.UTF8))
            {
                all = sr.ReadToEnd().Split('\n');
            }
            var start = Math.Max(0, all.Length - tailLines - 1);
            for (var i = start; i < all.Length; i++)
            {
                if (i == all.Length - 1 && all[i].Length == 0) continue;  // trailing empty
                Console.WriteLine(all[i]);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[bridge] failed to read log: {ex.Message}");
        }
    }

    // ------------------------------------------------------------------
    // Windows (Scheduled Task)
    // ------------------------------------------------------------------

    private static int InstallWindows(string exePath)
    {
        // Stop any currently-running instance of the existing task first,
        // otherwise we end up with the old bridge still alive plus a fresh
        // one started by /Run below.
        RunQuiet("schtasks", "/End", "/TN", ServiceName);

        // /Create /F forces overwrite if the task already exists,
        // which makes install idempotent / upgrade-safe.
        var code = Run("schtasks",
            "/Create", "/F",
            "/TN", ServiceName,
            "/TR", $"\"{exePath}\"",
            "/SC", "ONLOGON");
        if (code != 0) return code;

        Run("schtasks", "/Run", "/TN", ServiceName);
        Console.WriteLine($"[installer] scheduled task '{ServiceName}' registered and started");
        return 0;
    }

    private static int UninstallWindows()
    {
        Run("schtasks", "/End", "/TN", ServiceName);  // may fail if not running; ignore
        var code = Run("schtasks", "/Delete", "/F", "/TN", ServiceName);
        if (code == 0)
            Console.WriteLine($"[installer] scheduled task '{ServiceName}' removed");
        else
            Console.WriteLine($"[installer] scheduled task '{ServiceName}' was not registered");
        return 0;  // idempotent
    }

    private static int StatusWindows()
    {
        var code = Run("schtasks", "/Query", "/TN", ServiceName);
        Console.WriteLine(code == 0
            ? $"installed: yes (scheduled task '{ServiceName}')"
            : $"installed: no");
        return 0;
    }

    // ------------------------------------------------------------------
    // macOS (launchd)
    // ------------------------------------------------------------------

    private static int InstallMacOS(string exePath)
    {
        var plistPath = MacPlistPath();
        Directory.CreateDirectory(Path.GetDirectoryName(plistPath)!);
        File.WriteAllText(plistPath, BuildPlist(exePath));

        // Unload first in case an older install exists; ignore failure.
        Run("launchctl", "unload", plistPath);
        var code = Run("launchctl", "load", plistPath);
        if (code != 0) return code;

        Console.WriteLine($"[installer] launchd agent loaded ({plistPath})");
        return 0;
    }

    private static int UninstallMacOS()
    {
        var plistPath = MacPlistPath();
        if (File.Exists(plistPath))
        {
            Run("launchctl", "unload", plistPath);
            File.Delete(plistPath);
            Console.WriteLine($"[installer] launchd agent unloaded and removed ({plistPath})");
        }
        else
        {
            Console.WriteLine("[installer] launchd agent was not registered");
        }
        return 0;
    }

    private static int StatusMacOS()
    {
        var plistPath = MacPlistPath();
        var plistExists = File.Exists(plistPath);
        Console.WriteLine(plistExists
            ? $"installed: yes ({plistPath})"
            : "installed: no");
        if (plistExists)
        {
            // launchctl list exits 0 if the label is loaded.
            var code = RunQuiet("launchctl", "list", MacLabel);
            Console.WriteLine(code == 0 ? "loaded:    yes" : "loaded:    no");
        }
        return 0;
    }

    private static string MacPlistPath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Library", "LaunchAgents",
            $"{MacLabel}.plist");

    private static string BuildPlist(string exePath) =>
        $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<!DOCTYPE plist PUBLIC ""-//Apple//DTD PLIST 1.0//EN"" ""http://www.apple.com/DTDs/PropertyList-1.0.dtd"">
<plist version=""1.0"">
<dict>
    <key>Label</key>
    <string>{MacLabel}</string>
    <key>ProgramArguments</key>
    <array>
        <string>{System.Security.SecurityElement.Escape(exePath)}</string>
    </array>
    <key>RunAtLoad</key>
    <true/>
    <key>KeepAlive</key>
    <true/>
    <key>StandardOutPath</key>
    <string>/tmp/claude-status-bridge.out</string>
    <key>StandardErrorPath</key>
    <string>/tmp/claude-status-bridge.err</string>
</dict>
</plist>
";

    // ------------------------------------------------------------------
    // Linux (systemd --user)
    // ------------------------------------------------------------------

    private static int InstallLinux(string exePath)
    {
        var unitPath = LinuxUnitPath();
        Directory.CreateDirectory(Path.GetDirectoryName(unitPath)!);
        File.WriteAllText(unitPath, BuildUnit(exePath));

        var reload = Run("systemctl", "--user", "daemon-reload");
        if (reload != 0) return reload;
        var enable = Run("systemctl", "--user", "enable", "--now", LinuxUnit);
        if (enable != 0) return enable;

        Console.WriteLine($"[installer] systemd user unit enabled ({unitPath})");
        return 0;
    }

    private static int UninstallLinux()
    {
        var unitPath = LinuxUnitPath();
        if (File.Exists(unitPath))
        {
            Run("systemctl", "--user", "disable", "--now", LinuxUnit);
            File.Delete(unitPath);
            Run("systemctl", "--user", "daemon-reload");
            Console.WriteLine($"[installer] systemd user unit disabled and removed ({unitPath})");
        }
        else
        {
            Console.WriteLine("[installer] systemd user unit was not registered");
        }
        return 0;
    }

    private static int StatusLinux()
    {
        var unitPath = LinuxUnitPath();
        var unitExists = File.Exists(unitPath);
        Console.WriteLine(unitExists
            ? $"installed: yes ({unitPath})"
            : "installed: no");
        if (unitExists)
        {
            var active = RunQuiet("systemctl", "--user", "is-active", LinuxUnit);
            Console.WriteLine(active == 0 ? "active:    yes" : "active:    no");
        }
        return 0;
    }

    private static string LinuxUnitPath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".config", "systemd", "user",
            LinuxUnit);

    private static string BuildUnit(string exePath) =>
        $@"[Unit]
Description=Claude Status Bridge
After=default.target

[Service]
Type=simple
ExecStart={exePath}
Restart=on-failure
RestartSec=3

[Install]
WantedBy=default.target
";

    // ------------------------------------------------------------------
    // Shared helpers
    // ------------------------------------------------------------------

    public static string VersionString()
    {
        var asm = Assembly.GetExecutingAssembly();
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(info))
        {
            // Strip the +<git-sha> suffix for the user-facing version.
            var plus = info.IndexOf('+');
            return plus > 0 ? info[..plus] : info;
        }
        return asm.GetName().Version?.ToString() ?? "unknown";
    }

    private static string PlatformName()
    {
        if (OperatingSystem.IsWindows()) return "windows";
        if (OperatingSystem.IsMacOS())   return "macos";
        if (OperatingSystem.IsLinux())   return "linux";
        return RuntimeInformation.OSDescription;
    }

    private static string ResolveExePath()
    {
        var p = Environment.ProcessPath;
        if (string.IsNullOrEmpty(p))
            throw new InvalidOperationException("cannot determine executable path");
        return p;
    }

    private static int Run(string fileName, params string[] args)
        => RunCore(fileName, suppress: false, args);

    private static int RunQuiet(string fileName, params string[] args)
        => RunCore(fileName, suppress: true, args);

    private static int RunCore(string fileName, bool suppress, string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);

            using var proc = Process.Start(psi);
            if (proc is null) return -1;

            var stdout = proc.StandardOutput.ReadToEnd();
            var stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit();

            if (!suppress)
            {
                if (!string.IsNullOrWhiteSpace(stdout))
                    Console.Write(stdout);
                if (!string.IsNullOrWhiteSpace(stderr))
                    Console.Error.Write(stderr);
            }
            return proc.ExitCode;
        }
        catch (Exception ex)
        {
            if (!suppress)
                Console.Error.WriteLine($"[installer] failed to run {fileName}: {ex.Message}");
            return -1;
        }
    }
}
