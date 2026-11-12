# claude-status — bridge

Cross-platform tray app that reads Claude Code lifecycle events from
the [claude-status plugin][plugin]'s local broker and forwards each
event over USB serial to an [ESP32-S3 firmware][firmware] driving a
HUB75 LED panel. **One of three components** in claude-status — the
plugin is the umbrella project with the full system overview and
install walkthrough:

**→ <https://github.com/sep/cc-status-plugin>**

```
 Claude Code  ──►  Plugin  ──►  Bridge  ──►  Firmware  ──►  Display
                  (Python)    (this repo)  (ESP32-S3)     (RGB matrix)
```

[plugin]: https://github.com/sep/cc-status-plugin
[firmware]: https://github.com/sep/cc-status-display

## Install

The bridge ships as a tray app (Windows notification area / macOS menu
bar / Linux status icon) with platform-native installers — `Setup.exe`
on Windows, `.dmg` on macOS, `.AppImage` on Linux. The install page
auto-detects your OS:

**→ <https://sep.github.io/cc-status-bridge/>**

After install, right-click the tray icon → **Connect device** to point
the bridge at the ESP32-S3 panel you flashed in step 1 of the
[full setup walkthrough](https://sep.github.io/cc-status-plugin/#installation).

## Remote display over SSH (wire v1.4)

The bridge can reach a display that has no direct network path to it —
different VLAN, different site — through an SSH account both can dial out
to. The device holds a remote port forward on that server; the bridge runs
the platform's OpenSSH client headless with a local forward to it. Design
and server setup: [`FIRMWARE.md`](FIRMWARE.md) §8 "Remote link over SSH";
host-side routing and the management window: [`MANAGEMENT.md`](MANAGEMENT.md).

Tray menu → **Manage display…**:

- *Display* tab — panel count, first id, per-panel split, the slot grid
  and the session list; click a session then a slot to route it; "Auto-assign
  new sessions" (default on) lights a new session up without any slash
  command.
- *Remote link* tab — with the device on USB: generate its key, copy the
  `authorized_keys` line, send Wi-Fi + server settings, pin the host key,
  read its status; then switch this bridge's transport to SSH.

Settings (`appsettings.json` `Bridge` section, or `CSB_BRIDGE__…` env vars):

```json
"Transport": "ssh",
"Ssh": { "Host": "vm.example.net", "Port": 22, "User": "claudepanel",
         "ListenPort": 7777, "LocalPort": 7777, "Executable": "ssh",
         "IdentityFile": "~/.ssh/claudepanel_bridge", "ExtraArgs": "" },
"AutoAssignNewSessions": true,
"SplitPanels": [2]
```

`Transport` defaults to `serial`. The SSH client is the user's own
(`~/.ssh/config` and agent apply). Give the bridge a **dedicated,
passphrase-less key** in `IdentityFile` — it runs `ssh` in batch mode and
cannot answer a passphrase prompt, and with `IdentityFile` set it offers
nothing else (`IdentitiesOnly`):

```sh
ssh-keygen -t ed25519 -f ~/.ssh/claudepanel_bridge -N "" -C "claudepanel bridge"
# on the server, next to the device's line:
#   restrict,port-forwarding,permitopen="localhost:7777" ssh-ed25519 AAAA… claudepanel bridge
``` Harnesses: `tools/ssh-link-smoke` (the transport, against
a device stand-in) and `tools/route-smoke` (the routing rules).

## Wire-protocol spec

The bridge ↔ firmware contract is documented in [`FIRMWARE.md`](FIRMWARE.md).
This is the canonical spec — both the bridge implementation and the
firmware implementation read it to know what bytes go on the wire. If
you're touching the serial path, read it first.

## Developers

- [`CONTRIBUTING.md`](CONTRIBUTING.md) — coding conventions, repo
  layout, slash-command rationale, commit style.
- [`MAINTENANCE.md`](MAINTENANCE.md) — every external dependency, how
  it's pinned, where to watch for upstream changes, and how to bump.
  Read this first when picking the project back up after months away.
- [`FIRMWARE.md`](FIRMWARE.md) — wire-protocol reference (canonical).

### Local build

You'll need [.NET SDK 10.0][dotnet] installed.

```sh
# Build for the current platform
dotnet build -c Release

# Or publish a single-file self-contained exe for a target RID
dotnet publish -c Release -r win-x64 -o publish/win-x64
dotnet publish -c Release -r osx-arm64 -o publish/osx-arm64
dotnet publish -c Release -r linux-x64 -o publish/linux-x64
```

[dotnet]: https://dotnet.microsoft.com/download

The build matrix targets `win-x64`, `linux-x64`, `osx-x64`, and
`osx-arm64`. Each release publishes a single-file binary plus a
platform-native installer (NSIS / `.dmg` / `.AppImage`).

### Cutting a release

CI builds per-RID binaries + installers, attaches them to a GitHub
Release, and verifies that `version.txt` matches the pushed tag.
Triggered by pushing a semver-tagged commit:

```sh
# Bump version.txt, commit, then:
git tag v0.3.6
git push --tags
```

See [`.github/workflows/release.yml`](.github/workflows/release.yml)
for the build matrix and steps.

## License

[MIT](LICENSE). © 2026 SEP.
