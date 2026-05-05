using static ClaudeStatusBridge.ProcessRunner;

namespace ClaudeStatusBridge;

/// <summary>
/// Linux host integration: systemd --user unit under
/// ~/.config/systemd/user/. Restart=on-failure mirrors the macOS
/// "restart on crash, not on quit" stance.
/// </summary>
internal sealed class LinuxPlatform : IPlatform
{
    private const string UnitName = "claude-status-bridge.service";

    public int Install(string exePath)
    {
        var unitPath = UnitPath;
        Directory.CreateDirectory(Path.GetDirectoryName(unitPath)!);
        File.WriteAllText(unitPath, BuildUnit(exePath));

        var reload = Run("systemctl", "--user", "daemon-reload");
        if (reload != 0) return reload;
        var enable = Run("systemctl", "--user", "enable", "--now", UnitName);
        if (enable != 0) return enable;

        Console.WriteLine($"[installer] systemd user unit enabled ({unitPath})");
        return 0;
    }

    public int Uninstall()
    {
        var unitPath = UnitPath;
        if (File.Exists(unitPath))
        {
            Run("systemctl", "--user", "disable", "--now", UnitName);
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

    public int Start() => Run("systemctl", "--user", "start", UnitName);
    public int Stop()  => Run("systemctl", "--user", "stop", UnitName);

    public int Status()
    {
        var unitPath = UnitPath;
        var unitExists = File.Exists(unitPath);
        Console.WriteLine(unitExists
            ? $"installed: yes ({unitPath})"
            : "installed: no");
        if (unitExists)
        {
            var active = RunQuiet("systemctl", "--user", "is-active", UnitName);
            Console.WriteLine(active == 0 ? "active:    yes" : "active:    no");
        }
        return 0;
    }

    public bool IsRegistered() => File.Exists(UnitPath);

    public IEnumerable<string> FilterSerialPorts(IEnumerable<string> raw) =>
        // Most ESP32-S3 native USB devices show up as /dev/ttyACM*.
        // Some classic USB-UART bridges (CP210x, CH340) show up as
        // /dev/ttyUSB*. /dev/ttyS* are typically motherboard UARTs.
        raw.Where(p =>
                p.StartsWith("/dev/ttyACM", StringComparison.Ordinal) ||
                p.StartsWith("/dev/ttyUSB", StringComparison.Ordinal))
           .OrderBy(p => p);

    public bool PortNameLooksValid(string name) =>
        !string.IsNullOrWhiteSpace(name)
        && (name.StartsWith("/dev/ttyACM", StringComparison.Ordinal)
            || name.StartsWith("/dev/ttyUSB", StringComparison.Ordinal));

    public string LogDir =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".local", "state", "claude-status-bridge");

    public string StateDir => LogDir;  // XDG_STATE_HOME covers both

    // -- helpers ----------------------------------------------------

    private static string UnitPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".config", "systemd", "user",
            UnitName);

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
}
