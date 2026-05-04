using static ClaudeStatusBridge.ProcessRunner;

namespace ClaudeStatusBridge;

/// <summary>
/// macOS host integration: launchd LaunchAgent under
/// ~/Library/LaunchAgents/. KeepAlive is configured to restart on
/// crash but NOT on user-initiated quit (see BuildPlist).
/// </summary>
internal sealed class MacOSPlatform : IPlatform
{
    private const string Label = "com.claude-status.bridge";

    public int Install(string exePath)
    {
        var plistPath = PlistPath;
        Directory.CreateDirectory(Path.GetDirectoryName(plistPath)!);
        File.WriteAllText(plistPath, BuildPlist(exePath));

        // Unload first in case an older install exists; ignore failure.
        Run("launchctl", "unload", plistPath);
        var code = Run("launchctl", "load", plistPath);
        if (code != 0) return code;

        Console.WriteLine($"[installer] launchd agent loaded ({plistPath})");
        return 0;
    }

    public int Uninstall()
    {
        var plistPath = PlistPath;
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

    public int Start() => Run("launchctl", "load", PlistPath);
    public int Stop()  => Run("launchctl", "unload", PlistPath);

    public int Status()
    {
        var plistPath = PlistPath;
        var plistExists = File.Exists(plistPath);
        Console.WriteLine(plistExists
            ? $"installed: yes ({plistPath})"
            : "installed: no");
        if (plistExists)
        {
            // launchctl list exits 0 if the label is loaded.
            var code = RunQuiet("launchctl", "list", Label);
            Console.WriteLine(code == 0 ? "loaded:    yes" : "loaded:    no");
        }
        return 0;
    }

    public bool IsRegistered() => File.Exists(PlistPath);

    public IEnumerable<string> FilterSerialPorts(IEnumerable<string> raw) =>
        // /dev/tty.* blocks on DCD; /dev/cu.* doesn't. Always prefer cu.*
        // for outbound writes, and skip bluetooth-incoming garbage.
        raw.Where(p => p.StartsWith("/dev/cu.", StringComparison.Ordinal))
           .Where(p => !p.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase))
           .OrderBy(p => p);

    public string LogDir =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Library", "Logs");

    public string StateDir =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Library", "Caches", "claude-status-bridge");

    // -- helpers ----------------------------------------------------

    private static string PlistPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Library", "LaunchAgents",
            $"{Label}.plist");

    private static string BuildPlist(string exePath) =>
        $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<!DOCTYPE plist PUBLIC ""-//Apple//DTD PLIST 1.0//EN"" ""http://www.apple.com/DTDs/PropertyList-1.0.dtd"">
<plist version=""1.0"">
<dict>
    <key>Label</key>
    <string>{Label}</string>
    <key>ProgramArguments</key>
    <array>
        <string>{System.Security.SecurityElement.Escape(exePath)}</string>
    </array>
    <key>RunAtLoad</key>
    <true/>
    <!-- KeepAlive as a dict with SuccessfulExit:false means launchd
         restarts the bridge on crash (non-zero exit) but does NOT
         restart it when the user quits cleanly from the tray menu —
         which is the right shape for a tray app. KeepAlive:true would
         turn a tray Quit into an immediate launchd restart, which is
         never what the user means. -->
    <key>KeepAlive</key>
    <dict>
        <key>SuccessfulExit</key>
        <false/>
    </dict>
    <key>StandardOutPath</key>
    <string>/tmp/claude-status-bridge.out</string>
    <key>StandardErrorPath</key>
    <string>/tmp/claude-status-bridge.err</string>
</dict>
</plist>
";
}
