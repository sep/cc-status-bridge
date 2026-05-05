using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Microsoft.Extensions.Configuration;

namespace ClaudeStatusBridge;

/// <summary>
/// Owns the system-tray icon, its menu, and the BridgeRunner background
/// task. Bridge starts subscribed automatically when the tray app launches;
/// the menu lets the user pause/resume the subscription, toggle
/// auto-launch-at-login, view logs, and quit. Icon color reflects the
/// "loudest" aggregate state across all currently-subscribed sessions.
/// </summary>
internal static class TrayHost
{
    private static TrayIcon? _trayIcon;
    private static IClassicDesktopStyleApplicationLifetime? _desktop;
    private static BridgeOptions? _options;
    private static BrokerClient? _broker;
    private static SerialOutput? _serial;
    private static BridgeRunner? _runner;
    private static CancellationTokenSource? _runnerCts;
    private static Task? _runnerTask;
    private static LogsWindow? _logsWindow;

    private static NativeMenuItem? _statusItem;
    private static NativeMenuItem? _pauseResumeItem;
    private static NativeMenuItem? _autoRunItem;
    private static NativeMenuItem? _connectDeviceItem;
    private static NativeMenu?     _connectDeviceMenu;
    private static CancellationTokenSource? _scannerCts;

    public static void AttachTo(Application app, IClassicDesktopStyleApplicationLifetime desktop)
    {
        _desktop = desktop;

        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
            .AddJsonFile(
                Path.Combine(
                    Path.GetDirectoryName(Environment.ProcessPath ?? "") ?? AppContext.BaseDirectory,
                    "appsettings.json"),
                optional: true, reloadOnChange: false)
            .AddEnvironmentVariables(prefix: "CSB_")
            .Build();
        _options = new BridgeOptions();
        config.GetSection("Bridge").Bind(_options);

        _broker = new BrokerClient(_options);
        _serial = new SerialOutput(_options);

        BuildMenu();
        _trayIcon = new TrayIcon
        {
            Icon = IconRenderer.RenderColored(IconRenderer.GrayDim),
            ToolTipText = $"ClaudePanel Bridge v{Installer.VersionString()}",
            IsVisible = true,
            Menu = BuildMenuRoot(),
        };
        TrayIcon.SetIcons(app, new TrayIcons { _trayIcon });

        RefreshAutoRunCheck();

        // First-run / stale-default guard: if the configured ComPort
        // doesn't match the OS's port-name pattern (e.g. empty on first
        // run, or "COM4" on a Mac upgrading from a pre-0.3.3 default),
        // pop the picker before starting the bridge subscription.
        // Otherwise we'd silently fail to open the port and leave the
        // user staring at a gray tray icon with no feedback.
        var configured = _options.ComPort ?? "";
        if (Platform.Current.PortNameLooksValid(configured))
        {
            StartBridge();
        }
        else
        {
            ShowPortPickerOrExit();
        }
    }

    // ============================================================
    // Menu construction
    // ============================================================

    private static NativeMenu BuildMenuRoot()
    {
        var menu = new NativeMenu();
        if (_statusItem is not null)        menu.Add(_statusItem);
        if (_pauseResumeItem is not null)   menu.Add(_pauseResumeItem);
        menu.Add(new NativeMenuItemSeparator());
        if (_connectDeviceItem is not null) menu.Add(_connectDeviceItem);
        if (_autoRunItem is not null)       menu.Add(_autoRunItem);
        var showLogs = new NativeMenuItem("Show logs");
        showLogs.Click += (_, _) => OpenLogsWindow();
        menu.Add(showLogs);
        menu.Add(new NativeMenuItemSeparator());
        var quit = new NativeMenuItem("Quit");
        quit.Click += (_, _) => Quit();
        menu.Add(quit);
        return menu;
    }

    private static void BuildMenu()
    {
        _statusItem = new NativeMenuItem("Status: starting...") { IsEnabled = false };
        _pauseResumeItem = new NativeMenuItem("Pause subscription");
        _pauseResumeItem.Click += (_, _) => TogglePauseResume();
        _autoRunItem = new NativeMenuItem("Run on login")
        {
            ToggleType = NativeMenuItemToggleType.CheckBox,
        };
        _autoRunItem.Click += (_, _) => ToggleAutoRun();

        _connectDeviceMenu = new NativeMenu();
        _connectDeviceMenu.Add(new NativeMenuItem("Scanning…") { IsEnabled = false });
        _connectDeviceItem = new NativeMenuItem("Connect device") { Menu = _connectDeviceMenu };
    }

    // ============================================================
    // Bridge lifecycle (subscription start / pause / resume)
    // ============================================================

    private static void StartBridge()
    {
        if (_runner is not null) return;
        if (_options is null || _broker is null || _serial is null) return;

        _runnerCts = new CancellationTokenSource();
        _runner = new BridgeRunner(_options, _broker, _serial);
        _runner.AggregateStateChanged += OnAggregateStateChanged;
        _runnerTask = Task.Run(() => _runner.RunAsync(_runnerCts.Token));

        // Now that we have a real configured port the runner will hold,
        // the background scanner can safely poke the *other* ports for
        // the "Connect device ▸" submenu. Calling this earlier (before
        // the picker resolved) raced the picker's own scan and produced
        // spurious "access denied" hits on whichever scan lost the
        // open() coin flip. StartScanner is idempotent.
        StartScanner();

        UpdateStatusLabel("Status: running");
        UpdatePauseResumeLabel(paused: false);
    }

    private static void StopBridgeAsync()
    {
        if (_runner is null) return;
        var cts = _runnerCts;
        var task = _runnerTask;
        var runner = _runner;
        _runner = null;
        _runnerCts = null;
        _runnerTask = null;

        Task.Run(async () =>
        {
            try { cts?.Cancel(); } catch { }
            try { if (task is not null) await task; } catch { }
            try { runner.AggregateStateChanged -= OnAggregateStateChanged; } catch { }
            try { cts?.Dispose(); } catch { }
            Dispatcher.UIThread.Post(() =>
            {
                UpdateStatusLabel("Status: paused");
                UpdatePauseResumeLabel(paused: true);
                if (_trayIcon is not null)
                    _trayIcon.Icon = IconRenderer.RenderColored(IconRenderer.GrayDim);
            });
        });
    }

    private static void TogglePauseResume()
    {
        if (_runner is null) StartBridge();
        else                 StopBridgeAsync();
    }

    private static void OnAggregateStateChanged(string aggregate)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_trayIcon is null) return;
            var color = IconRenderer.ColorFor(aggregate);
            _trayIcon.Icon = IconRenderer.RenderColored(color);
            UpdateStatusLabel($"Status: running — {aggregate}");
        });
    }

    private static void UpdateStatusLabel(string text)
    {
        if (_statusItem is not null) _statusItem.Header = text;
    }

    private static void UpdatePauseResumeLabel(bool paused)
    {
        if (_pauseResumeItem is null) return;
        _pauseResumeItem.Header = paused ? "Resume subscription" : "Pause subscription";
    }

    // ============================================================
    // Auto-run-on-login (just calls Installer)
    // ============================================================

    private static void ToggleAutoRun()
    {
        if (_autoRunItem is null) return;
        var enabling = !_autoRunItem.IsChecked;
        Task.Run(() =>
        {
            var rc = enabling ? Installer.Install() : Installer.Uninstall();
            Dispatcher.UIThread.Post(() => RefreshAutoRunCheck());
        });
    }

    private static void RefreshAutoRunCheck()
    {
        if (_autoRunItem is null) return;
        _autoRunItem.IsChecked = Installer.IsRegistered();
    }

    // ============================================================
    // Logs window
    // ============================================================

    private static void OpenLogsWindow()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_logsWindow is null || !_logsWindow.IsVisible)
            {
                _logsWindow = new LogsWindow();
                _logsWindow.Closed += (_, _) => _logsWindow = null;
                _logsWindow.Show();
            }
            else
            {
                _logsWindow.Activate();
            }
        });
    }

    // ============================================================
    // Connect device (background scanner + submenu + port swap)
    // ============================================================

    private static void StartScanner()
    {
        // Idempotent: StartBridge calls into us each time it spins up
        // the runner (initial start, resume-from-pause, post-picker
        // first start). One scanner loop is enough for the lifetime
        // of the process.
        if (_scannerCts is not null) return;
        _scannerCts = new CancellationTokenSource();
        _ = Task.Run(() => ScannerLoopAsync(_scannerCts.Token));
    }

    private static async Task ScannerLoopAsync(CancellationToken ct)
    {
        // First scan eager (within a couple seconds of tray launch); then
        // every 30s. Probe is ~600ms per port and the user rarely changes
        // hardware mid-session, so a slow background tick is fine.
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var results = await Task.Run(ScanPorts, ct);
                Dispatcher.UIThread.Post(() => UpdateConnectDeviceMenu(results));
            }
            catch (OperationCanceledException) { break; }
            catch { /* swallow; we'll try again on the next tick */ }

            try { await Task.Delay(TimeSpan.FromSeconds(30), ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private static List<Discovery.ProbeResult> ScanPorts()
    {
        var ports = Discovery.EnumeratePorts();
        var activePort = _options?.ComPort;
        var baud = _options?.BaudRate ?? 115200;
        var results = new List<Discovery.ProbeResult>();
        foreach (var p in ports)
        {
            // Skip the active port — bridge is holding it open and a probe
            // would race for the handle. Surface it as the current selection
            // so the user sees it in the submenu.
            if (string.Equals(p, activePort, StringComparison.OrdinalIgnoreCase))
            {
                results.Add(new Discovery.ProbeResult(p, true, "active"));
                continue;
            }
            results.Add(Discovery.Probe(p, baud));
        }
        return results;
    }

    private static void UpdateConnectDeviceMenu(List<Discovery.ProbeResult> results)
    {
        if (_connectDeviceMenu is null) return;
        _connectDeviceMenu.Items.Clear();

        var matches = results.Where(r => r.IsClaudePanel).ToList();
        if (matches.Count == 0)
        {
            _connectDeviceMenu.Items.Add(
                new NativeMenuItem("(no ClaudePanel detected — plug it in?)") { IsEnabled = false });
            return;
        }

        var activePort = _options?.ComPort;
        foreach (var r in matches)
        {
            var item = new NativeMenuItem($"{r.Port} — {r.Detail}")
            {
                ToggleType = NativeMenuItemToggleType.CheckBox,
                IsChecked  = string.Equals(r.Port, activePort, StringComparison.OrdinalIgnoreCase),
            };
            var port = r.Port;
            item.Click += (_, _) => _ = Task.Run(() => ChangeComPortAsync(port));
            _connectDeviceMenu.Items.Add(item);
        }
    }

    private static async Task ChangeComPortAsync(string newPort)
    {
        if (_options is null) return;
        if (string.Equals(newPort, _options.ComPort, StringComparison.OrdinalIgnoreCase))
            return;

        // Tear the runner down and wait for it to actually stop, so the
        // serial handle is released before we reopen against the new port.
        var cts = _runnerCts;
        var task = _runnerTask;
        var runner = _runner;
        _runner = null;
        _runnerCts = null;
        _runnerTask = null;
        try { cts?.Cancel(); } catch { }
        try { if (task is not null) await task; } catch { }
        try { if (runner is not null) runner.AggregateStateChanged -= OnAggregateStateChanged; } catch { }
        try { cts?.Dispose(); } catch { }

        // Persist + update in-memory options.
        if (!Installer.TryWriteComPortConfig(newPort, out _, out var err))
        {
            Dispatcher.UIThread.Post(() => UpdateStatusLabel($"Status: failed to save port ({err})"));
            return;
        }
        _options.ComPort = newPort;

        // Recreate SerialOutput against the updated options. SerialOutput
        // re-reads ComPort lazily on each open, so a fresh instance picks
        // up the new value on its next reconnect attempt.
        try { _serial?.Dispose(); } catch { }
        _serial = new SerialOutput(_options);

        Dispatcher.UIThread.Post(StartBridge);
    }

    // ============================================================
    // First-run port picker
    // ============================================================

    private static ConnectDeviceWindow? _pickerWindow;

    private static void ShowPortPickerOrExit()
    {
        if (_options is null) return;
        Dispatcher.UIThread.Post(() =>
        {
            UpdateStatusLabel("Status: pick a device");
            _pickerWindow = new ConnectDeviceWindow(_options.BaudRate);
            _pickerWindow.PortChosen += OnPickerPortChosen;
            _pickerWindow.Closed += OnPickerClosed;
            _pickerWindow.Show();
        });
    }

    private static void OnPickerPortChosen(string port)
    {
        if (_options is null) return;
        if (!Installer.TryWriteComPortConfig(port, out _, out var err))
        {
            UpdateStatusLabel($"Status: failed to save port ({err})");
            return;
        }
        _options.ComPort = port;
        try { _serial?.Dispose(); } catch { }
        _serial = new SerialOutput(_options);
        StartBridge();
    }

    private static void OnPickerClosed(object? sender, EventArgs e)
    {
        var chosen = _pickerWindow?.PortWasChosen ?? false;
        _pickerWindow = null;
        if (!chosen)
        {
            // User dismissed without picking — there's no port to talk to,
            // so quit cleanly rather than leave a useless tray icon.
            Quit();
        }
    }

    // ============================================================
    // Quit
    // ============================================================

    private static void Quit()
    {
        // Best-effort: stop the scanner + bridge, then shutdown Avalonia.
        try { _scannerCts?.Cancel(); } catch { }
        try { _runnerCts?.Cancel(); } catch { }
        try { _serial?.Dispose(); } catch { }
        Dispatcher.UIThread.Post(() => _desktop?.Shutdown());
    }
}
