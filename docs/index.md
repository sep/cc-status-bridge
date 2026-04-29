---
layout: default
title: ClaudePanel Bridge
---

# ClaudePanel Bridge

The bridge is the cross-platform daemon that connects Claude Code to your
ClaudePanel hardware. It subscribes to the [ClaudePanel
plugin](https://sep.github.io/cc-status-plugin/)'s local broker, watches
your Claude Code session lifecycle events, and forwards each state change
to your ESP32-S3 over USB serial.

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
    <p>Download <code>ClaudeStatusBridge-win-x64.exe</code> from the
       <a href="https://github.com/sep/cc-status-bridge/releases/latest">latest release</a>.</p>
    <p>Open an <strong>Administrator</strong> PowerShell window in the
       folder where you saved the binary, then:</p>
<pre><code>.\ClaudeStatusBridge-win-x64.exe install
</code></pre>
    <p>This registers a Scheduled Task that runs the bridge at login.
       The bridge starts immediately and from then on auto-launches each
       time you sign in.</p>
    <p><strong>Verify:</strong></p>
<pre><code>.\ClaudeStatusBridge-win-x64.exe status
</code></pre>
    <p>To see live logs, run <code>.\ClaudeStatusBridge-win-x64.exe logs</code>.</p>
  </div>

  <div class="os-tab-content" data-os="macos" role="tabpanel">
    <h3>macOS (Apple Silicon or Intel)</h3>
    <p>Download <code>ClaudeStatusBridge-osx-arm64</code> (Apple Silicon)
       or <code>ClaudeStatusBridge-osx-x64</code> (Intel) from the
       <a href="https://github.com/sep/cc-status-bridge/releases/latest">latest release</a>.</p>
    <p>In Terminal, navigate to the download folder, then:</p>
<pre><code>chmod +x ClaudeStatusBridge-osx-arm64
xattr -dr com.apple.quarantine ./ClaudeStatusBridge-osx-arm64
./ClaudeStatusBridge-osx-arm64 install
</code></pre>
    <p>The <code>xattr</code> step clears macOS Gatekeeper's quarantine
       flag — it's required because the binary is signed ad-hoc rather
       than fully notarized. Without it, the launchd agent that
       <code>install</code> registers would fail to launch the binary
       silently.</p>
    <p><strong>Verify:</strong></p>
<pre><code>./ClaudeStatusBridge-osx-arm64 status
</code></pre>
    <p>To see live logs, run <code>./ClaudeStatusBridge-osx-arm64 logs</code>.</p>
  </div>

  <div class="os-tab-content" data-os="linux" role="tabpanel">
    <h3>Linux (x64)</h3>
    <p>Download <code>ClaudeStatusBridge-linux-x64</code> from the
       <a href="https://github.com/sep/cc-status-bridge/releases/latest">latest release</a>.</p>
<pre><code>chmod +x ClaudeStatusBridge-linux-x64
./ClaudeStatusBridge-linux-x64 install
</code></pre>
    <p>This writes a systemd user unit (<code>~/.config/systemd/user/claude-status-bridge.service</code>)
       and starts it. It will resume at every login.</p>
    <p><strong>Verify:</strong></p>
<pre><code>./ClaudeStatusBridge-linux-x64 status
</code></pre>
    <p>To see live logs, run <code>./ClaudeStatusBridge-linux-x64 logs</code>.</p>
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
Easiest way to figure it out:

```text
bridge match
```

`match` scans every plausible serial port, asks each one to identify
itself, and offers to write the chosen port into `appsettings.json` for
you. Run it once after install (or whenever you switch USB cables).

## After install

The bridge runs in the background. To see it working:

1. Plug your ESP32-S3 ClaudePanel hardware in via USB.
2. Run `bridge match` and confirm the port it found.
3. Send a prompt in any Claude Code session that has the
   <a href="https://sep.github.io/cc-status-plugin/">ClaudePanel plugin</a>
   installed. The matrix should react.

## Subcommands

| Command            | What it does                                        |
|--------------------|-----------------------------------------------------|
| `install`          | Register + start the background service.            |
| `uninstall`        | Stop + deregister.                                  |
| `start` / `stop`   | Toggle the running instance without deregistering.  |
| `restart`          | Stop then start.                                    |
| `match`            | Scan + identify a connected ClaudePanel; write the  |
|                    | chosen port to `appsettings.json`.                  |
| `status`           | Show install state, running state, and version.     |
| `version`          | Print the version string.                           |
| `logs`             | Tail the bridge log (Ctrl-C to exit).               |
| `help`             | Show usage.                                         |

## Source

<https://github.com/sep/cc-status-bridge>
