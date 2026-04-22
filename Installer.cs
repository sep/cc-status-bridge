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

    public static int Restart()
    {
        Stop();                          // ignore exit; may already be stopped
        System.Threading.Thread.Sleep(200);
        return Start();
    }

    // ------------------------------------------------------------------
    // Windows (Scheduled Task)
    // ------------------------------------------------------------------

    private static int InstallWindows(string exePath)
    {
        // /Create /F forces overwrite if the task already exists,
        // which makes install idempotent / upgrade-safe.
        var code = Run("schtasks",
            "/Create", "/F",
            "/TN", ServiceName,
            "/TR", $"\"{exePath}\"",
            "/SC", "ONLOGON");
        if (code != 0) return code;

        // Start it now so the user doesn't have to log out.
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
