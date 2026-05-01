using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;

namespace ClaudeStatusBridge;

/// <summary>
/// Avalonia application class for the bridge's system-tray UI.
/// No main window — the tray icon is the entire interface.
/// ShutdownMode.OnExplicitShutdown keeps the process alive even when
/// no windows are open; the user has to use the tray menu's "Quit"
/// item (or kill the process externally) to exit.
/// </summary>
public sealed class TrayApp : Application
{
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            TrayHost.AttachTo(this, desktop);
        }
        base.OnFrameworkInitializationCompleted();
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder
            .Configure<TrayApp>()
            .UsePlatformDetect()
            .LogToTrace();
}
