using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Win32;
using static ClaudeStatusBridge.ProcessRunner;

namespace ClaudeStatusBridge;

/// <summary>
/// Windows host integration: HKCU Run-key autostart + manual
/// process management (Process.Start / Process.Kill against the
/// running bridge by image name).
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsPlatform : IPlatform
{
    private const string ServiceName = "ClaudeStatusBridge";
    private const string RunKey      = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue    = "ClaudeStatusBridge";

    public int Install(string exePath)
    {
        // Best-effort cleanup of any legacy scheduled task left behind by
        // older installs that used schtasks. RunQuiet so Access-Denied
        // (legacy task created with an elevated token) doesn't paint red
        // into the installer log — once we own autostart via the Run key,
        // the legacy task becomes inert: at login both would race to
        // start the bridge, but SingleInstance ensures only one survives.
        RunQuiet("schtasks", "/End", "/TN", ServiceName);
        RunQuiet("schtasks", "/Delete", "/F", "/TN", ServiceName);

        WriteRunValue(exePath);
        Console.WriteLine($"[installer] autostart registered (HKCU\\{RunKey}\\{RunValue})");

        // Launch immediately so the user doesn't have to log out / back
        // in to see the tray icon.
        LaunchBridge(exePath);
        return 0;
    }

    public int Uninstall()
    {
        Console.WriteLine(DeleteRunValue()
            ? "[installer] autostart removed"
            : "[installer] autostart was not registered");

        KillBridge();

        // Best-effort cleanup of any legacy scheduled task.
        RunQuiet("schtasks", "/End", "/TN", ServiceName);
        RunQuiet("schtasks", "/Delete", "/F", "/TN", ServiceName);

        return 0;
    }

    public int Start()
    {
        var exe = Environment.ProcessPath
                  ?? throw new InvalidOperationException("cannot determine executable path");
        return LaunchBridge(exe);
    }

    public int Stop() => KillBridge();

    public int Status()
    {
        var registered = RunValueExists();
        Console.WriteLine(registered
            ? $"installed: yes (HKCU\\{RunKey}\\{RunValue})"
            : "installed: no");
        Console.WriteLine($"running:   {(IsBridgeRunning() ? "yes" : "no")}");
        return 0;
    }

    public bool IsRegistered() => RunValueExists();

    public IEnumerable<string> FilterSerialPorts(IEnumerable<string> raw) =>
        // Any COM* is fair game on Windows — the OS already filters out
        // bluetooth-incoming garbage from GetPortNames().
        raw.OrderBy(p => p, StringComparer.OrdinalIgnoreCase);

    public bool PortNameLooksValid(string name) =>
        !string.IsNullOrWhiteSpace(name)
        && System.Text.RegularExpressions.Regex.IsMatch(
            name, @"^COM\d+$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    public string LogDir =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "claude-status-bridge");

    public string StateDir => LogDir;  // %LOCALAPPDATA% covers both

    // -- helpers ----------------------------------------------------

    private static void WriteRunValue(string exePath)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        // Quote the path so spaces in usernames / install dirs don't
        // confuse the shell when Windows hands the value to CreateProcess.
        key.SetValue(RunValue, $"\"{exePath}\"", RegistryValueKind.String);
    }

    private static bool DeleteRunValue()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (key is null) return false;
        if (key.GetValue(RunValue) is null) return false;
        key.DeleteValue(RunValue, throwOnMissingValue: false);
        return true;
    }

    private static bool RunValueExists()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
        return key?.GetValue(RunValue) is not null;
    }

    private static int LaunchBridge(string exePath)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName        = exePath,
                UseShellExecute = false,
                CreateNoWindow  = true,
            };
            using var proc = Process.Start(psi);
            return proc is null ? 1 : 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[installer] failed to launch bridge: {ex.Message}");
            return 1;
        }
    }

    private static int KillBridge()
    {
        var self = Process.GetCurrentProcess().Id;
        foreach (var p in Process.GetProcessesByName(ServiceName))
        {
            try
            {
                if (p.Id == self) continue;
                p.Kill(entireProcessTree: false);
                p.WaitForExit(2000);
            }
            catch { /* best-effort; process may have exited between enum and Kill */ }
            finally { p.Dispose(); }
        }
        return 0;
    }

    private static bool IsBridgeRunning()
    {
        var self = Process.GetCurrentProcess().Id;
        foreach (var p in Process.GetProcessesByName(ServiceName))
        {
            try { if (p.Id != self) return true; }
            finally { p.Dispose(); }
        }
        return false;
    }
}
