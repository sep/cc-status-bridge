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
            // Forward the tray-launch args to TrayHost so it can layer
            // them onto the configuration pipeline (CLI flags override
            // appsettings.json + env vars for any Bridge:* option).
            TrayHost.AttachTo(this, desktop, desktop.Args ?? Array.Empty<string>());
        }
        base.OnFrameworkInitializationCompleted();
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder
            .Configure<TrayApp>()
            .UsePlatformDetect()
            // Tell Avalonia's macOS bootstrap to use NSApplicationActivationPolicy.Accessory
            // instead of .Regular. Without this, Avalonia overrides the bundle's
            // LSUIElement=true and we get a Dock icon despite Info.plist saying we
            // shouldn't. ShowInDock=false → menu-bar agent app, no Dock entry, no
            // main app menu (so Cmd+Q is a non-issue; quit lives in the tray menu).
            // No-op on Windows / Linux.
            .With(new MacOSPlatformOptions { ShowInDock = false })
            .LogToTrace();
}
