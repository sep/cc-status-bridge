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
