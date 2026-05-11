---
layout: default
title: ClaudePanel Bridge
---

# ClaudePanel Bridge

> **Not sure where to start?** Begin at the
> [plugin site](https://sep.github.io/cc-status-plugin/) — that's the
> system overview and install walkthrough that gets your Claude Code
> session talking to the panel.

The bridge is the cross-platform tray app that connects Claude Code to
your ClaudePanel hardware. It subscribes to the [ClaudePanel
plugin](https://sep.github.io/cc-status-plugin/)'s local broker, watches
your Claude Code session lifecycle events, and forwards each state change
to your ESP32-S3 over USB serial.

It lives in your system tray (Windows notification area / macOS menu bar
/ Linux status icon). Right-click for start / stop / pause / logs / quit.
You probably don't need to think about it once it's installed — it
starts on login, runs in the background, and survives reboots.

## Install

<style>
  .os-tabs {
    margin: 1.5rem 0;
    border: 1px solid #d0d7de;
    border-radius: 6px;
    overflow: hidden;
    background: #fff;
  }
  .os-tab-buttons {
    display: flex;
    background: #f6f8fa;
    border-bottom: 1px solid #d0d7de;
  }
  .os-tab-button {
    flex: 1;
    padding: 0.75rem 1rem;
    border: 0;
    background: transparent;
    font: inherit;
    font-weight: 500;
    color: #57606a;
    cursor: pointer;
    border-bottom: 2px solid transparent;
    transition: all 0.12s ease;
  }
  .os-tab-button:hover { background: #eaeef2; color: #24292f; }
  .os-tab-button.active {
    color: #24292f;
    border-bottom-color: #159957;
    background: #fff;
  }
  .os-tab-content { display: none; padding: 1.25rem 1.5rem; }
  .os-tab-content.active { display: block; }
  .os-tab-content h3:first-child { margin-top: 0; }
  .os-tab-content pre { background: #f6f8fa; padding: 0.75rem 1rem; border-radius: 6px; overflow-x: auto; }
  .os-tab-content code { background: #eef1f4; padding: 0.1em 0.35em; border-radius: 3px; font-size: 0.92em; }
  .os-tab-content pre code { background: transparent; padding: 0; }
</style>

<div class="os-tabs" id="install-tabs">
  <div class="os-tab-buttons" role="tablist">
    <button class="os-tab-button" data-os="windows" role="tab">Windows</button>
    <button class="os-tab-button" data-os="macos"   role="tab">macOS</button>
    <button class="os-tab-button" data-os="linux"   role="tab">Linux</button>
  </div>

  <div class="os-tab-content" data-os="windows" role="tabpanel">
    <h3>Windows (x64)</h3>
    {% if site.bridge_release_tag != "" %}
    <p>Download
       <a href="https://github.com/sep/cc-status-bridge/releases/download/{{ site.bridge_release_tag }}/ClaudePanelBridge-{{ site.bridge_version }}-Setup.exe"><code>ClaudePanelBridge-{{ site.bridge_version }}-Setup.exe</code></a>
       and double-click it.</p>
    {% else %}
    <p>Download <code>ClaudePanelBridge-*-Setup.exe</code> from the
       <a href="https://github.com/sep/cc-status-bridge/releases/latest">latest release</a>
       and double-click it.</p>
    {% endif %}
    <p>Per-user install — no admin password needed. The installer drops
       the binary into <code>%LOCALAPPDATA%</code>, registers an entry in
       Apps &amp; Features, writes the standard
       <code>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</code>
       autostart entry so the bridge launches at login, and starts the
       tray icon immediately.</p>
    <p>SmartScreen may warn "publisher not verified" on first run; click
       <em>More info → Run anyway</em>. The binary is self-signed; full
       Authenticode signing isn't in place yet.</p>
    <p><strong>Uninstall:</strong> Settings → Apps → ClaudePanel Bridge →
       Uninstall.</p>
  </div>

  <div class="os-tab-content" data-os="macos" role="tabpanel">
    <h3>macOS (Apple Silicon or Intel)</h3>
    {% if site.bridge_release_tag != "" %}
    <p>Download the matching <code>.dmg</code>:</p>
    <ul>
      <li><a href="https://github.com/sep/cc-status-bridge/releases/download/{{ site.bridge_release_tag }}/ClaudePanelBridge-{{ site.bridge_version }}-osx-arm64.dmg"><code>ClaudePanelBridge-{{ site.bridge_version }}-osx-arm64.dmg</code></a> &mdash; Apple Silicon</li>
      <li><a href="https://github.com/sep/cc-status-bridge/releases/download/{{ site.bridge_release_tag }}/ClaudePanelBridge-{{ site.bridge_version }}-osx-x64.dmg"><code>ClaudePanelBridge-{{ site.bridge_version }}-osx-x64.dmg</code></a> &mdash; Intel</li>
    </ul>
    {% else %}
    <p>Download the matching <code>.dmg</code> from the
       <a href="https://github.com/sep/cc-status-bridge/releases/latest">latest release</a>:</p>
    <ul>
      <li><code>ClaudePanelBridge-*-osx-arm64.dmg</code> &mdash; Apple Silicon</li>
      <li><code>ClaudePanelBridge-*-osx-x64.dmg</code> &mdash; Intel</li>
    </ul>
    {% endif %}
    <p>Open the <code>.dmg</code>, drag <code>ClaudePanelBridge.app</code>
       into <code>Applications</code>, then double-click it. The app
       lives in your menu bar — there is no Dock icon.</p>
    <p>First launch may need: System Settings → Privacy &amp; Security →
       <em>Open Anyway</em>. The bundle is ad-hoc signed but not
       notarized (no Apple Developer cert yet), so Gatekeeper asks for
       confirmation the first time.</p>
  </div>

  <div class="os-tab-content" data-os="linux" role="tabpanel">
    <h3>Linux (x64)</h3>
    {% if site.bridge_release_tag != "" %}
    <p>Download
       <a href="https://github.com/sep/cc-status-bridge/releases/download/{{ site.bridge_release_tag }}/ClaudePanelBridge-{{ site.bridge_version }}-x86_64.AppImage"><code>ClaudePanelBridge-{{ site.bridge_version }}-x86_64.AppImage</code></a>.</p>
    {% else %}
    <p>Download <code>ClaudePanelBridge-*-x86_64.AppImage</code> from
       the <a href="https://github.com/sep/cc-status-bridge/releases/latest">latest release</a>.</p>
    {% endif %}
<pre><code>chmod +x ClaudePanelBridge-*-x86_64.AppImage
./ClaudePanelBridge-*-x86_64.AppImage
</code></pre>
    <p>If you want it to start at login, run
       <code>./ClaudePanelBridge-*-x86_64.AppImage install</code> once —
       that writes a systemd user unit
       (<code>~/.config/systemd/user/claude-status-bridge.service</code>)
       which resumes the bridge each session.</p>
    <p>The tray icon needs a system-tray host on GNOME (the
       <em>AppIndicator and KStatusNotifierItem Support</em> extension
       is the usual one). KDE / XFCE / Cinnamon work out of the box.</p>
  </div>
</div>

<script>
  (function () {
    const tabs = document.getElementById('install-tabs');
    if (!tabs) return;

    function detectOs() {
      const data = navigator.userAgentData;
      if (data && data.platform) {
        const p = data.platform.toLowerCase();
        if (p.includes('win')) return 'windows';
        if (p.includes('mac')) return 'macos';
        if (p.includes('linux')) return 'linux';
      }
      const ua = (navigator.userAgent || '').toLowerCase();
      if (ua.includes('win')) return 'windows';
      if (ua.includes('mac')) return 'macos';
      return 'linux';
    }

    const buttons = tabs.querySelectorAll('.os-tab-button');
    const contents = tabs.querySelectorAll('.os-tab-content');

    function activate(os) {
      buttons.forEach(b => b.classList.toggle('active', b.dataset.os === os));
      contents.forEach(c => c.classList.toggle('active', c.dataset.os === os));
    }

    buttons.forEach(b => b.addEventListener('click', () => activate(b.dataset.os)));
    activate(detectOs());
  })();
</script>

## Find your hardware

The bridge needs to know which USB serial port your ClaudePanel is on.

**On first run**, if no port is configured (fresh install, or
`appsettings.json` missing / `Bridge:ComPort` empty), the bridge pops a
**Connect device** dialog automatically before it even starts the tray
icon. The dialog scans every plausible serial port the moment it opens,
asks each one to identify itself, and shows you a list. Pick the
matching ClaudePanel with a click — your choice is written into
`appsettings.json` so subsequent launches go straight to the tray.

**Later**, if you swap boards or want to re-pick, click
**Connect device** in the tray menu — same scan, same dialog.

For scripting, recovery, or headless setups, the same flow is available
as a CLI subcommand (`ClaudeStatusBridge find`) — see
[CLI usage](#cli-usage) below for the exact invocation on your OS.

## After install

The bridge runs in the background. To see it working:

1. Plug your ESP32-S3 ClaudePanel hardware in via USB. Don't have one
   yet? Head to the [firmware site](https://sep.github.io/cc-status-display/)
   for a one-click flasher and a parts list.
2. Right-click the tray icon → use **Connect device** to pick the
   detected ClaudePanel (or run `find` from a terminal).
3. Send a prompt in any Claude Code session that has the
   <a href="https://sep.github.io/cc-status-plugin/">ClaudePanel plugin</a>
   installed. The matrix should react.

## CLI usage

The bridge is primarily a tray app — most users never need the
command line. But all the same operations are available as
subcommands of the binary, which is handy for scripting, CI, or
debugging.

On Windows the executable lives at
`%LOCALAPPDATA%\ClaudePanelBridge\ClaudeStatusBridge.exe`. On macOS
it's inside the bundle: `/Applications/ClaudePanelBridge.app/Contents/MacOS/ClaudeStatusBridge`.
On Linux it's the AppImage you downloaded.

**Windows note:** the Windows build is a Windows-subsystem binary so
the tray launches without a console flash. That means cmd / PowerShell
won't block waiting for it to finish — output still flows to your
shell, but it interleaves with the next prompt. For interactive
subcommands like `find`, invoke via `Start-Process -Wait`:

```pwsh
Start-Process -Wait `
    "$env:LOCALAPPDATA\ClaudePanelBridge\ClaudeStatusBridge.exe" `
    -ArgumentList find
```

| Command            | What it does                                        |
|--------------------|-----------------------------------------------------|
| `install`          | Register + start the background service.            |
| `uninstall`        | Stop + deregister.                                  |
| `start` / `stop`   | Toggle the running instance without deregistering.  |
| `restart`          | Stop then start.                                    |
| `find`             | Scan + identify a connected ClaudePanel; write the  |
|                    | chosen port to `appsettings.json`. (interactive)    |
|                    | Also accepts the legacy alias `match`.              |
| `status`           | Show install state, running state, and version.     |
| `version`          | Print the version string.                           |
| `logs`             | Tail the bridge log (Ctrl-C to exit).               |
| `help`             | Show usage.                                         |

## Related projects

- **[Plugin](https://sep.github.io/cc-status-plugin/)** — system entry
  point; install starts here.
- **[Firmware](https://sep.github.io/cc-status-display/)** — flashing
  guide and pre-built binaries for the ESP32-S3 display.
- **[Bridge source](https://github.com/sep/cc-status-bridge)** — this
  app's repo, for issues and code.
