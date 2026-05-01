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

        StartBridge();
        RefreshAutoRunCheck();
    }

    // ============================================================
    // Menu construction
    // ============================================================

    private static NativeMenu BuildMenuRoot()
    {
        var menu = new NativeMenu();
        if (_statusItem is not null)       menu.Add(_statusItem);
        if (_pauseResumeItem is not null)  menu.Add(_pauseResumeItem);
        menu.Add(new NativeMenuItemSeparator());
        if (_autoRunItem is not null)      menu.Add(_autoRunItem);
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
    // Quit
    // ============================================================

    private static void Quit()
    {
        // Best-effort: stop the bridge, then shutdown Avalonia.
        try { _runnerCts?.Cancel(); } catch { }
        try { _serial?.Dispose(); } catch { }
        Dispatcher.UIThread.Post(() => _desktop?.Shutdown());
    }
}
