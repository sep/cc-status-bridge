using System.Reflection;

namespace ClaudeStatusBridge;

/// <summary>
/// Public CLI surface for install / uninstall / start / stop / status /
/// version / find / logs. Per-OS work is delegated to
/// <see cref="Platform.Current"/>; this class only handles the
/// platform-agnostic concerns (version string, logs tail, COM-port
/// config write, the interactive `find` flow).
/// </summary>
internal static class Installer
{
    // ==================================================================
    // Public API — façade over Platform.Current for the OS-divergent
    // operations, plus platform-agnostic helpers.
    // ==================================================================

    public static int Install()
    {
        var exePath = ResolveExePath();
        Console.WriteLine($"[installer] binary: {exePath}");
        return Platform.Current.Install(exePath);
    }

    public static int Uninstall() => Platform.Current.Uninstall();
    public static int Start()     => Platform.Current.Start();
    public static int Stop()      => Platform.Current.Stop();

    public static int Restart()
    {
        Stop();
        System.Threading.Thread.Sleep(200);
        return Start();
    }

    public static int Status()
    {
        Console.WriteLine($"version:   {VersionString()}");
        Console.WriteLine($"platform:  {Platform.Name}");
        Console.WriteLine($"binary:    {ResolveExePath()}");
        return Platform.Current.Status();
    }

    public static bool IsRegistered() => Platform.Current.IsRegistered();

    public static int PrintVersion()
    {
        Console.WriteLine(VersionString());
        return 0;
    }

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

    // ==================================================================
    // `find` — interactive scan + write of Bridge:ComPort
    // ==================================================================

    public static int Find(int baudRate)
    {
        Console.WriteLine("[find] scanning serial ports...");
        var ports = Discovery.EnumeratePorts();
        if (ports.Count == 0)
        {
            Console.Error.WriteLine("[find] no candidate serial ports found");
            Console.Error.WriteLine("[find] is your ClaudePanel hardware plugged in via USB?");
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
                Console.WriteLine("[find] cancelled");
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
            if (chosen is null) { Console.WriteLine("[find] cancelled"); return 1; }
        }
        else
        {
            Console.WriteLine("[find] no ClaudePanel responded on any port");
            Console.WriteLine("[find] is the firmware running and on baud 115200?");
            return 1;
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
        if (!TryWriteComPortConfig(comPort, out var configPath, out var err))
        {
            Console.Error.WriteLine($"[find] failed to write {configPath}: {err}");
            return 1;
        }
        Console.WriteLine();
        Console.WriteLine($"[find] wrote {configPath}");
        Console.WriteLine($"[find] Bridge:ComPort = {comPort}");
        Console.WriteLine();
        Console.WriteLine("Restart the bridge to pick up the new config:");
        Console.WriteLine("  bridge restart");
        return 0;
    }

    /// <summary>
    /// Persist a chosen COM port into appsettings.json next to the binary.
    /// No console output — used by both the interactive `find` subcommand
    /// (via WriteComPortConfig) and the tray's "Connect device" submenu.
    /// </summary>
    public static bool TryWriteComPortConfig(string comPort, out string configPath, out string error)
    {
        var binaryDir = Path.GetDirectoryName(Environment.ProcessPath ?? "")
                        ?? AppContext.BaseDirectory;
        configPath = Path.Combine(binaryDir, "appsettings.json");
        error = "";

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
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    // ==================================================================
    // `logs` — tail the bridge's log file, optionally following.
    // ==================================================================

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

    // ==================================================================
    // Shared helpers
    // ==================================================================

    private static string ResolveExePath()
    {
        var p = Environment.ProcessPath;
        if (string.IsNullOrEmpty(p))
            throw new InvalidOperationException("cannot determine executable path");
        return p;
    }
}
