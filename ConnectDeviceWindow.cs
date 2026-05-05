using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace ClaudeStatusBridge;

/// <summary>
/// First-run / no-valid-port-configured device picker. Shown by TrayHost
/// when the configured ComPort doesn't look like a real USB-serial port
/// for this OS (e.g. the legacy "COM4" default on macOS, or empty on a
/// fresh install). Lists every detected port via the same
/// Discovery.EnumeratePorts + Discovery.Probe machinery the tray's
/// "Connect device" submenu uses; lets the user pick one and Connect, or
/// dismiss to quit. The picker is a one-shot at startup — once the bridge
/// has a working port, the tray submenu owns post-startup port switching.
/// </summary>
internal sealed class ConnectDeviceWindow : Window
{
    private readonly ListBox _list;
    private readonly Button _connectButton;
    private readonly TextBlock _statusLabel;
    private readonly int _baudRate;

    /// <summary>
    /// Raised when the user clicks Connect on a selected port. The
    /// argument is the port name (e.g. "/dev/cu.usbmodem1101"). The
    /// window closes itself after the event fires.
    /// </summary>
    public event Action<string>? PortChosen;

    /// <summary>
    /// True iff the user actually picked a port (vs. dismissed the
    /// window). TrayHost uses this in the Closed handler to decide
    /// whether to proceed with bridge startup or exit the app.
    /// </summary>
    public bool PortWasChosen { get; private set; }

    public ConnectDeviceWindow(int baudRate)
    {
        _baudRate = baudRate;
        Title = "ClaudePanel Bridge — Connect device";
        Width = 480;
        Height = 360;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var header = new TextBlock
        {
            Text = "Pick the serial port your ClaudePanel is connected to. "
                 + "Cancel to quit.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12),
            FontSize = 13,
        };

        _statusLabel = new TextBlock
        {
            Text = "Scanning…",
            FontStyle = FontStyle.Italic,
            Foreground = new SolidColorBrush(Color.Parse("#57606a")),
            Margin = new Thickness(0, 0, 0, 8),
            FontSize = 12,
        };

        // Build _connectButton before _list so the SelectionChanged
        // handler below can reference it without tripping the nullable
        // analyzer.
        _connectButton = new Button
        {
            Content = "Connect",
            IsEnabled = false,
            Padding = new Thickness(20, 6),
        };
        _connectButton.Click += (_, _) => OnConnectClicked();

        _list = new ListBox
        {
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        _list.SelectionChanged += (_, _) =>
            _connectButton.IsEnabled = _list.SelectedItem is not null;

        var cancelButton = new Button
        {
            Content = "Cancel",
            Padding = new Thickness(20, 6),
            Margin = new Thickness(0, 0, 8, 0),
        };
        cancelButton.Click += (_, _) => Close();

        var buttonRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0),
        };
        buttonRow.Children.Add(cancelButton);
        buttonRow.Children.Add(_connectButton);

        var grid = new Grid
        {
            Margin = new Thickness(16),
            RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto"),
        };
        Grid.SetRow(header, 0);          grid.Children.Add(header);
        Grid.SetRow(_statusLabel, 1);    grid.Children.Add(_statusLabel);
        Grid.SetRow(_list, 2);           grid.Children.Add(_list);
        Grid.SetRow(buttonRow, 3);       grid.Children.Add(buttonRow);
        Content = grid;

        // Kick the scan off the moment the window is shown so we don't
        // block the UI thread during construction.
        Opened += (_, _) => _ = ScanAsync();
    }

    private void OnConnectClicked()
    {
        if (_list.SelectedItem is not PortRow row) return;
        PortWasChosen = true;
        PortChosen?.Invoke(row.Port);
        Close();
    }

    private async Task ScanAsync()
    {
        _statusLabel.Text = "Scanning serial ports…";
        var results = await Task.Run(() =>
        {
            var ports = Discovery.EnumeratePorts();
            return ports.Select(p => Discovery.Probe(p, _baudRate)).ToList();
        });

        Dispatcher.UIThread.Post(() => RenderResults(results));
    }

    private void RenderResults(IList<Discovery.ProbeResult> results)
    {
        var rows = results
            .OrderByDescending(r => r.IsClaudePanel)
            .ThenBy(r => r.Port, StringComparer.Ordinal)
            .Select(r => new PortRow(r))
            .ToList();

        _list.ItemsSource = rows;

        var matched = rows.Count(r => r.IsClaudePanel);
        if (rows.Count == 0)
        {
            _statusLabel.Text = "No serial ports found. Is your ClaudePanel plugged in?";
        }
        else if (matched == 0)
        {
            _statusLabel.Text = $"{rows.Count} port(s) found, none responded as a ClaudePanel. "
                              + "You can still pick one manually.";
        }
        else
        {
            _statusLabel.Text = $"{matched} ClaudePanel(s) detected. Pick one and click Connect.";
            // Auto-select the first ClaudePanel match — the common case.
            _list.SelectedIndex = 0;
        }
    }

    /// <summary>
    /// ListBox row model. Implements ToString so Avalonia's default
    /// ItemTemplate renders something sensible without us shipping an
    /// XAML DataTemplate.
    /// </summary>
    private sealed record PortRow(Discovery.ProbeResult Result)
    {
        public string Port         => Result.Port;
        public bool   IsClaudePanel => Result.IsClaudePanel;
        public override string ToString() =>
            Result.IsClaudePanel
                ? $"✓  {Result.Port}    ClaudePanel ({Result.Detail})"
                : $"    {Result.Port}    ({Result.Detail})";
    }
}
