using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using System.Text.Json.Nodes;

namespace ClaudeStatusBridge;

/// <summary>
/// "Manage display…" window (MANAGEMENT.md): layout + routing on the Display
/// tab, v1.4 provisioning and transport on the Remote link tab. Reads the
/// live BridgeRunner through a delegate so it survives pause/resume, and
/// refreshes on a timer. Code-behind UI like the other windows.
/// </summary>
internal sealed class ManagementWindow : Window
{
    private readonly BridgeOptions _options;
    private readonly Func<BridgeRunner?> _runner;
    private readonly Func<Task> _restartBridge;
    private readonly DispatcherTimer _timer;

    // Display tab
    private readonly NumericUpDown _panelCount;
    private readonly NumericUpDown _firstId;
    private readonly StackPanel _splitRow;
    private readonly CheckBox _autoAssign;
    private readonly WrapPanel _slotGrid;
    private readonly ListBox _sessions;
    private readonly TextBlock _displayStatus;
    private string? _selectedSession;

    // Remote link tab
    private readonly TextBox _ssid, _psk, _host, _port, _user, _listenPort;
    private readonly CheckBox _enabled;
    private readonly TextBox _keyOut, _statusOut;
    private readonly TextBlock _hostkeyLabel;
    private readonly ComboBox _transport;
    private readonly TextBox _bHost, _bPort, _bUser, _bListen, _bLocal, _bIdentity, _bExtra;
    private string? _lastSeenFingerprint;
    private BridgeRunner? _subscribedRunner;

    public ManagementWindow(BridgeOptions options, Func<BridgeRunner?> runner, Func<Task> restartBridge)
    {
        _options = options;
        _runner = runner;
        _restartBridge = restartBridge;

        Title = "ClaudePanel Bridge — Manage display";
        Width = 760;
        Height = 620;
        CanResize = true;

        // ---------------- Display tab ----------------
        _panelCount = new NumericUpDown { Minimum = 1, Maximum = 4, Increment = 1, Width = 90, FormatString = "0" };
        _firstId    = new NumericUpDown { Minimum = 1, Maximum = 99, Increment = 1, Width = 90, FormatString = "0" };
        _splitRow   = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        var applyLayout = new Button { Content = "Apply layout", Padding = new Thickness(14, 4) };
        applyLayout.Click += (_, _) => ApplyLayout();
        var identify = new Button { Content = "Identify panels", Padding = new Thickness(14, 4) };
        identify.Click += (_, _) => Send(new JsonObject { ["type"] = "identify", ["duration_ms"] = 5000 });
        _panelCount.ValueChanged += (_, _) => RebuildSplitRow();

        var layoutRow = Row(
            Label("Panels"), _panelCount,
            Label("First id"), _firstId,
            Label("Split"), _splitRow,
            applyLayout, identify);

        _autoAssign = new CheckBox { Content = "Auto-assign new sessions to the first free slot" };
        _autoAssign.IsCheckedChanged += (_, _) =>
        {
            _options.AutoAssignNewSessions = _autoAssign.IsChecked == true;
            Persist(b => b["AutoAssignNewSessions"] = _options.AutoAssignNewSessions);
        };

        _slotGrid = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8) };
        _sessions = new ListBox { Height = 200 };
        _sessions.SelectionChanged += (_, _) =>
            _selectedSession = (_sessions.SelectedItem as SessionRow)?.Info.SessionId;
        var unroute = new Button { Content = "Unroute selected", Padding = new Thickness(14, 4) };
        unroute.Click += (_, _) => { if (_selectedSession is not null) _runner()?.Unbind(_selectedSession); Refresh(); };
        var refresh = new Button { Content = "Refresh", Padding = new Thickness(14, 4) };
        refresh.Click += (_, _) => Refresh();
        _displayStatus = new TextBlock { FontSize = 12, Foreground = new SolidColorBrush(Color.Parse("#57606a")), TextWrapping = TextWrapping.Wrap };

        var displayPanel = new StackPanel { Margin = new Thickness(16), Spacing = 8 };
        displayPanel.Children.Add(layoutRow);
        displayPanel.Children.Add(_autoAssign);
        displayPanel.Children.Add(Label("Slots — click a session below, then a slot. Click an occupied slot to unroute it."));
        displayPanel.Children.Add(_slotGrid);
        displayPanel.Children.Add(Label("Sessions on this machine"));
        displayPanel.Children.Add(_sessions);
        displayPanel.Children.Add(Row(unroute, refresh));
        displayPanel.Children.Add(_displayStatus);

        // ---------------- Remote link tab ----------------
        _ssid = Field(); _psk = Field(); _psk.PasswordChar = '•';
        _host = Field(); _port = Field("22"); _user = Field("claudepanel"); _listenPort = Field("7777");
        _enabled = new CheckBox { Content = "Enabled", IsChecked = true };
        var provision = new Button { Content = "Send to device (provision)", Padding = new Thickness(14, 4) };
        provision.Click += (_, _) => Provision();
        var keygen = new Button { Content = "Generate / show device key", Padding = new Thickness(14, 4) };
        keygen.Click += (_, _) => Send(new JsonObject { ["type"] = "keygen" });
        _keyOut = new TextBox { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 70, FontSize = 11,
                                FontFamily = new FontFamily("Cascadia Mono, Consolas, Menlo, monospace") };
        var copyKey = new Button { Content = "Copy authorized_keys line", Padding = new Thickness(14, 4) };
        copyKey.Click += async (_, _) =>
        {
            var keyOut = _keyOut;
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard && keyOut.Tag is string line)
                await clipboard.SetTextAsync(line);
        };
        _hostkeyLabel = new TextBlock { Text = "Host key: (none seen yet — provision, then wait for the device to connect)", FontSize = 12, TextWrapping = TextWrapping.Wrap };
        var pin = new Button { Content = "Pin this host key", Padding = new Thickness(14, 4) };
        pin.Click += (_, _) => { if (_lastSeenFingerprint is not null) Send(new JsonObject { ["type"] = "pin_hostkey", ["fingerprint"] = _lastSeenFingerprint }); };
        var status = new Button { Content = "Device status", Padding = new Thickness(14, 4) };
        status.Click += (_, _) => Send(new JsonObject { ["type"] = "tunnel_status" });
        _statusOut = new TextBox { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 110, FontSize = 11,
                                   FontFamily = new FontFamily("Cascadia Mono, Consolas, Menlo, monospace") };

        _transport = new ComboBox { ItemsSource = new[] { "serial", "ssh" }, Width = 110 };
        _bHost = Field(_options.Ssh.Host); _bPort = Field(_options.Ssh.Port.ToString()); _bUser = Field(_options.Ssh.User);
        _bListen = Field(_options.Ssh.ListenPort.ToString()); _bLocal = Field(_options.Ssh.LocalPort.ToString());
        _bIdentity = Field(_options.Ssh.IdentityFile); _bIdentity.MinWidth = 260; _bExtra = Field(_options.Ssh.ExtraArgs);
        var saveTransport = new Button { Content = "Save & restart link", Padding = new Thickness(14, 4) };
        saveTransport.Click += async (_, _) => await SaveTransportAsync();

        var remotePanel = new StackPanel { Margin = new Thickness(16), Spacing = 8 };
        remotePanel.Children.Add(Heading("1. Device identity (USB connection required)"));
        remotePanel.Children.Add(Row(keygen, copyKey));
        remotePanel.Children.Add(_keyOut);
        remotePanel.Children.Add(Heading("2. Device settings (USB connection required)"));
        remotePanel.Children.Add(Row(Label("Wi-Fi SSID"), _ssid, Label("Password"), _psk));
        remotePanel.Children.Add(Row(Label("SSH host"), _host, Label("Port"), _port, Label("User"), _user, Label("Listen port"), _listenPort));
        remotePanel.Children.Add(Row(_enabled, provision));
        remotePanel.Children.Add(_hostkeyLabel);
        remotePanel.Children.Add(Row(pin, status));
        remotePanel.Children.Add(_statusOut);
        remotePanel.Children.Add(Heading("3. This bridge's transport"));
        remotePanel.Children.Add(Row(Label("Transport"), _transport, Label("Host"), _bHost, Label("Port"), _bPort, Label("User"), _bUser));
        remotePanel.Children.Add(Row(Label("Device port"), _bListen, Label("Local port"), _bLocal));
        remotePanel.Children.Add(Row(Label("Identity file"), _bIdentity, Label("Extra ssh args"), _bExtra, saveTransport));
        remotePanel.Children.Add(new TextBlock
        {
            FontSize = 11, TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.Parse("#57606a")),
            Text = "Use a dedicated passphrase-less key for the bridge (ssh runs in batch mode and cannot prompt): "
                 + "ssh-keygen -t ed25519 -f ~/.ssh/claudepanel_bridge -N \"\"  — then add its .pub to the server as "
                 + "restrict,port-forwarding,permitopen=\"localhost:<device port>\" <key>. \"~\" is expanded here.",
        });

        var tabs = new TabControl();
        tabs.Items.Add(new TabItem { Header = "Display", Content = new ScrollViewer { Content = displayPanel } });
        tabs.Items.Add(new TabItem { Header = "Remote link", Content = new ScrollViewer { Content = remotePanel } });
        Content = tabs;

        LoadInitialValues();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += (_, _) => Refresh();
        Opened += (_, _) => { Refresh(); _timer.Start(); };
        Closed += (_, _) => { _timer.Stop(); Unsubscribe(); };
    }

    // ---------------------------------------------------------------- helpers

    private static TextBlock Label(string text) =>
        new() { Text = text, VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
    private static TextBlock Heading(string text) =>
        new() { Text = text, FontWeight = FontWeight.Bold, Margin = new Thickness(0, 10, 0, 0) };
    private static TextBox Field(string initial = "") => new() { Text = initial, MinWidth = 120, FontSize = 12 };
    private static StackPanel Row(params Control[] items)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        foreach (var c in items) row.Children.Add(c);
        return row;
    }

    private void Persist(Action<JsonObject> mutate)
    {
        if (!Installer.TryUpdateBridgeConfig(mutate, out _, out var err))
            _displayStatus.Text = $"Failed to save settings: {err}";
    }

    private bool Send(JsonObject cmd)
    {
        var r = _runner();
        if (r is null) { _statusOut.Text = "Bridge is paused — resume it first."; return false; }
        var ok = r.SendDeviceCommand(cmd);
        if (!ok) _statusOut.Text = $"Device link not open ({r.LinkDescribe}).";
        return ok;
    }

    private void LoadInitialValues()
    {
        var layout = _runner()?.Routes.ReadPanelLayout();
        _panelCount.Value = layout?.PanelCount ?? 1;
        _firstId.Value = layout?.FirstId ?? 1;
        RebuildSplitRow();
        _autoAssign.IsChecked = _options.AutoAssignNewSessions;
        _transport.SelectedItem = _options.UsesSsh ? "ssh" : "serial";
    }

    private void RebuildSplitRow()
    {
        var count = (int)(_panelCount.Value ?? 1);
        var first = (int)(_firstId.Value ?? 1);
        var split = new HashSet<int>(_options.SplitPanels ?? Array.Empty<int>());
        _splitRow.Children.Clear();
        for (var i = 0; i < count; i++)
        {
            var n = first + i;
            _splitRow.Children.Add(new CheckBox { Content = n.ToString(), IsChecked = split.Contains(n), Tag = n });
        }
    }

    private void ApplyLayout()
    {
        var r = _runner();
        if (r is null) { _displayStatus.Text = "Bridge is paused — resume it first."; return; }
        var count = (int)(_panelCount.Value ?? 1);
        var first = (int)(_firstId.Value ?? 1);
        var split = _splitRow.Children.OfType<CheckBox>().Where(c => c.IsChecked == true).Select(c => (int)c.Tag!).ToArray();
        if (!r.Routes.SetPanelLayout(count, first)) { _displayStatus.Text = "Layout rejected (1–4 panels, first id 1–99)."; return; }
        _options.SplitPanels = split;
        Persist(b => b["SplitPanels"] = new JsonArray(split.Select(s => (JsonNode)s).ToArray()));
        _displayStatus.Text = r.ResendConfigure();
        Refresh();
    }

    private void Refresh()
    {
        var r = _runner();
        Subscribe(r);
        if (r is null)
        {
            _displayStatus.Text = "Bridge is paused.";
            return;
        }

        // Sessions
        var infos = r.GetSessions();
        var rows = infos.Select(i => new SessionRow(i)).ToList();
        _sessions.ItemsSource = rows;
        if (_selectedSession is not null)
            _sessions.SelectedItem = rows.FirstOrDefault(x => x.Info.SessionId == _selectedSession);

        // Slots
        var routes = r.Routes.ReadRoutes();
        var byId = infos.ToDictionary(i => i.SessionId, i => i);
        var pongClients = r.LatestPong?["clients"] as JsonObject;
        _slotGrid.Children.Clear();
        foreach (var slot in r.Routes.OfferedSlots())
        {
            var holder = routes.FirstOrDefault(kv => kv.Value == slot).Key;
            string body;
            if (holder is not null && byId.TryGetValue(holder, out var info))
                body = $"{SessionRow.Folder(info)}\n{info.State ?? "—"}";
            else if (pongClients?[slot] is JsonObject pc)
                body = $"(other bridge)\n{pc["state"]?.GetValue<string>() ?? "busy"}";
            else
                body = "empty";
            var btn = new Button
            {
                Content = $"{slot}\n{body}",
                Width = 120, Height = 64, Margin = new Thickness(0, 0, 8, 8),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                Tag = (slot, holder),
            };
            btn.Click += (_, _) => OnSlotClicked(slot, holder);
            _slotGrid.Children.Add(btn);
        }

        var link = r.LinkIsOpen ? $"link open: {r.LinkDescribe}" : $"link closed: {r.LinkDescribe}";
        _displayStatus.Text = $"{infos.Count} session(s), {routes.Count} routed · {link}";
    }

    private void OnSlotClicked(string slot, string? holder)
    {
        var r = _runner();
        if (r is null) return;
        if (_selectedSession is not null && _selectedSession != holder)
        {
            r.Bind(_selectedSession, slot);
        }
        else if (holder is not null)
        {
            r.Unbind(holder);
        }
        Refresh();
    }

    private void Provision()
    {
        var cmd = new JsonObject
        {
            ["type"] = "provision",
            ["wifi"] = new JsonObject { ["ssid"] = _ssid.Text ?? "", ["psk"] = _psk.Text ?? "" },
            ["ssh"] = new JsonObject
            {
                ["host"] = _host.Text ?? "",
                ["port"] = int.TryParse(_port.Text, out var p) ? p : 22,
                ["user"] = _user.Text ?? "",
                ["listen_port"] = int.TryParse(_listenPort.Text, out var lp) ? lp : 7777,
            },
            ["enabled"] = _enabled.IsChecked == true,
        };
        if (Send(cmd)) _statusOut.Text = "provision sent; waiting for the device…";
    }

    private async Task SaveTransportAsync()
    {
        _options.Transport = _transport.SelectedItem as string ?? "serial";
        _options.Ssh.Host = _bHost.Text ?? "";
        _options.Ssh.Port = int.TryParse(_bPort.Text, out var p) ? p : 22;
        _options.Ssh.User = _bUser.Text ?? "";
        _options.Ssh.ListenPort = int.TryParse(_bListen.Text, out var lp) ? lp : 7777;
        _options.Ssh.LocalPort = int.TryParse(_bLocal.Text, out var lo) ? lo : 7777;
        _options.Ssh.IdentityFile = _bIdentity.Text ?? "";
        _options.Ssh.ExtraArgs = _bExtra.Text ?? "";
        Persist(b =>
        {
            b["Transport"] = _options.Transport;
            b["Ssh"] = new JsonObject
            {
                ["Host"] = _options.Ssh.Host, ["Port"] = _options.Ssh.Port, ["User"] = _options.Ssh.User,
                ["ListenPort"] = _options.Ssh.ListenPort, ["LocalPort"] = _options.Ssh.LocalPort,
                ["Executable"] = _options.Ssh.Executable, ["IdentityFile"] = _options.Ssh.IdentityFile,
                ["ExtraArgs"] = _options.Ssh.ExtraArgs,
            };
        });
        _statusOut.Text = "transport saved; restarting the bridge link…";
        await _restartBridge();
        Refresh();
    }

    // Device replies land here (on a thread-pool thread) → marshal to UI.
    private void Subscribe(BridgeRunner? r)
    {
        if (ReferenceEquals(r, _subscribedRunner)) return;
        Unsubscribe();
        if (r is null) return;
        r.DeviceLineReceived += OnDeviceLine;
        _subscribedRunner = r;
    }

    private void Unsubscribe()
    {
        if (_subscribedRunner is not null) _subscribedRunner.DeviceLineReceived -= OnDeviceLine;
        _subscribedRunner = null;
    }

    private void OnDeviceLine(JsonNode doc)
    {
        string? type;
        try { type = doc["type"]?.GetValue<string>(); } catch { return; }
        if (type is not ("pubkey" or "provisioned" or "hostkey_seen" or "tunnel_status")) return;
        Dispatcher.UIThread.Post(() =>
        {
            switch (type)
            {
                case "pubkey":
                    var line = doc["authorized_keys_line"]?.GetValue<string>() ?? "";
                    _keyOut.Tag = line;
                    _keyOut.Text = $"{doc["key"]?.GetValue<string>()}\nfingerprint {doc["fingerprint"]?.GetValue<string>()}\n"
                                 + (doc["generated"]?.GetValue<bool>() == true ? "(newly generated — update authorized_keys on the server)\n" : "")
                                 + $"authorized_keys: {line}";
                    break;
                case "hostkey_seen":
                    _lastSeenFingerprint = doc["fingerprint"]?.GetValue<string>();
                    var pinned = doc["pinned"]?.GetValue<bool>() == true;
                    var match = doc["match"] is JsonValue mv && mv.TryGetValue<bool>(out var m) ? (bool?)m : null;
                    _hostkeyLabel.Text = $"Host key seen: {_lastSeenFingerprint} ({doc["keytype"]?.GetValue<string>()}) — "
                                       + (pinned ? (match == true ? "pinned, matches" : "PINNED, MISMATCH") : "not pinned; compare with `ssh-keygen -lf` on the server, then Pin");
                    break;
                default:
                    _statusOut.Text = doc.ToJsonString();
                    break;
            }
        });
    }

    private sealed record SessionRow(BridgeRunner.SessionInfo Info)
    {
        public static string Folder(BridgeRunner.SessionInfo i) =>
            string.IsNullOrEmpty(i.Cwd) ? i.SessionId[..Math.Min(8, i.SessionId.Length)]
                                        : (Path.GetFileName(i.Cwd.TrimEnd('/', '\\')) is { Length: > 0 } f ? f : i.Cwd);
        private static string Age(DateTimeOffset? t)
        {
            if (t is null) return "";
            var d = DateTimeOffset.Now - t.Value;
            return d.TotalHours >= 1 ? $"{(int)d.TotalHours}h" : $"{Math.Max(0, (int)d.TotalMinutes)}m";
        }
        public override string ToString() =>
            $"{(Info.Slot is null ? "[ — ]" : $"[{Info.Slot}]")}  {Folder(Info)}  ·  {Age(Info.StartedAt)}  ·  {Info.State ?? "?"}"
            + (string.IsNullOrEmpty(Info.LastPrompt) ? "" : $"  ·  “{Info.LastPrompt}”");
    }
}
