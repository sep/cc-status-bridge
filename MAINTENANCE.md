# Maintenance

Single living document of every external dependency the bridge relies
on, where to watch them, how to bump them, and what to test after.
**If you're picking the bridge back up after months away, start here**
— it's designed to remove the "where do I even look?" phase of
catching up.

The general posture is **proactive over reactive**: pin tightly,
review changes deliberately, build canaries against upstream's next
release. CI surfaces drift; humans decide when to adopt it.

## Active maintenance signals

| Signal | Cadence | Lives in | What it means |
| --- | --- | --- | --- |
| **Dependabot PRs** | Mondays | `.github/dependabot.yml` | A pinned NuGet package or GitHub Action has a new upstream version. Minor/patch bumps batch into one PR; majors land alone for individual review. Read the diff + release notes; merge if green. |
| **PR check** | On every PR | `.github/workflows/pr.yml` | `dotnet build` against every release RID (win-x64, linux-x64, osx-x64, osx-arm64) plus `dotnet list package --vulnerable --include-transitive` so any reintroduced vuln shows up before merge. |
| **Smoke build** | Mondays | `.github/workflows/smoke.yml` | Weekly rebuild against currently-pinned .NET SDK. Red mail = supply-chain rot (a NuGet package vanished, the SDK image broke, etc.) before a user hits it. |
| **Release** | On tag push | `.github/workflows/release.yml` | Builds + signs + ships per-RID binaries, NSIS installer, `.dmg`, AppImage. Verifies `version.txt` matches the pushed tag and refuses to publish if they drift. |

If Mondays are quiet, the bridge is healthy. If your inbox lights up,
treat each signal as a single ticket and drain the queue before adding
features.

## Dependencies

### .NET SDK

- **Currently pinned to:** `10.0.x` (CI uses `actions/setup-dotnet`
  with `dotnet-version: "10.0.x"`).
- **Pinned in:**
  - `.github/workflows/release.yml` — `dotnet-version: "10.0.x"`
  - `.github/workflows/pr.yml`      — `dotnet-version: "10.0.x"`
  - `.github/workflows/smoke.yml`   — `dotnet-version: "10.0.x"`
  - `ClaudeStatusBridge.csproj`     — `<TargetFramework>net10.0</TargetFramework>`
- **Watch:** <https://dotnet.microsoft.com/en-us/download/dotnet>
  (release notes), <https://github.com/dotnet/core/releases>.
- **Bump procedure:**
  1. Wait for smoke to be green on the current pin.
  2. Edit the four locations above. SDK pin and TFM bump in lockstep.
  3. Push a branch; PR check builds against every RID.
  4. Publish a dry-run `Setup.exe` / `.dmg` / `.AppImage` from the
     branch and run through the install + tray-launch + Connect
     device flow on real hardware.
  5. Tag a new semver and push; `release.yml` handles the rest.

### Avalonia

- **Currently pinned to:** `11.2.7` across `Avalonia`,
  `Avalonia.Desktop`, `Avalonia.Themes.Fluent`.
- **Pinned in:** `ClaudeStatusBridge.csproj`.
- **Watch:** <https://github.com/AvaloniaUI/Avalonia/releases>.
- **Risk:** medium. Avalonia is the entire UI layer (tray icon,
  Connect device window, logs window). Major bumps have historically
  reshaped the application bootstrap (`AppBuilder` API,
  `MacOSPlatformOptions`, etc.) — read release notes carefully.
- **Bump procedure:** edit the three package versions in lockstep
  (mismatch surfaces as runtime exceptions, not build errors). Test
  on macOS specifically — Avalonia's macOS path has the most quirks
  (Dock-icon behavior, tray-icon registration, `LSUIElement`
  interaction).

### `Tmds.DBus.Protocol`

- **Currently pinned to:** `0.21.3` (explicit override of Avalonia's
  transitive 0.20.0, which is flagged by GHSA-xrw6-gwf8-vvr9).
- **Pinned in:** `ClaudeStatusBridge.csproj`.
- **Watch:** <https://github.com/tmds/Tmds.DBus/releases>.
- **Risk:** Linux-only (D-Bus is unused on Windows / macOS). Removed
  if Avalonia drops the transitive dep entirely; otherwise this pin
  stays.

### `System.IO.Ports`, `Microsoft.Extensions.Configuration*`

- **Currently pinned to:** `10.0.0`.
- **Pinned in:** `ClaudeStatusBridge.csproj`.
- **Watch:** part of the .NET runtime release cadence.
- **Bump procedure:** typically moves with the .NET SDK pin.

### GitHub Actions

All actions in `.github/workflows/*.yml` should ideally be **pinned to
commit SHAs** (not tags) for supply-chain security — Dependabot
rewrites both the SHA and the trailing version comment when bumping.
Currently a mix of SHA and tag references; conversion is on
Dependabot's queue.

| Action | Watch |
| --- | --- |
| `actions/checkout` | <https://github.com/actions/checkout/releases> |
| `actions/setup-dotnet` | <https://github.com/actions/setup-dotnet/releases> |
| `actions/upload-artifact` | <https://github.com/actions/upload-artifact/releases> |
| `actions/download-artifact` | <https://github.com/actions/download-artifact/releases> |
| `softprops/action-gh-release` | <https://github.com/softprops/action-gh-release/releases> |

Dependabot opens a weekly PR with available bumps; read upstream
release notes (Dependabot links them) and merge if benign. **Major
bumps land alone** — minor/patch batch into one PR. This is
deliberate: a bundle of seven simultaneous major bumps is too much
surface to vet in one diff.

The `pr.yml` workflow runs on every PR (Dependabot's included) and
covers most of the release pipeline — `dotnet build` per RID, vuln
scan — but **does not exercise the actual signing / installer build
/ release publish steps**. For PRs that bump
`softprops/action-gh-release` or anything inside the NSIS / .dmg /
AppImage build paths, the only real verification is a dry-run release
on a throwaway tag against the dependabot branch:

```sh
git checkout dependabot/...
git tag v0.0.0-test-$(date +%s)
git push origin --tags
# watch release.yml run end-to-end, then delete the tag
# and the auto-generated draft release.
```

## Versioning

The bridge version's source of truth is **`version.txt`** at the
repo root. Bumped by hand when starting work toward a new release;
tagged into git when shipping. CI verifies they match — release
fails loudly if they drift.

`version.txt` holds **bare semver** (`0.3.5`); git tags use the
**`v`-prefixed** form (`v0.3.5`). The release workflow strips a
leading `v` from both sides before comparing, so either form works
on either side.

The `.csproj` reads `version.txt` and threads it through
`AssemblyInformationalVersion`. The bridge logs it on startup and
exposes it via the tray-icon tooltip and the `bridge version`
subcommand.

### When to bump

Loose semver, scoped to behavior end users will notice:

- **Major (vX.0.0)** — a behavior change a user has to know about.
  E.g., wire-protocol breakage, removal of a CLI subcommand, autostart
  mechanism change. Rare.
- **Minor (vX.Y.0)** — a new tray menu item, a new CLI subcommand, a
  new wire-protocol type the bridge emits. Additive and
  backward-compatible.
- **Patch (vX.Y.Z)** — bug fixes, dependency bumps that don't change
  behavior, doc edits.

### Pre-1.0

While we're below v1.0.0, the bar is looser — minor bumps are fine
for behavior changes, patch for everything else. Promotion to v1.0.0
should signal "this is the bridge contract end users can rely on
not to change underneath them." Not there yet.

## Cutting a release

1. Confirm the latest smoke build was green.
2. Confirm the latest PR check was green on the merge-base.
3. **Bump `version.txt`** to the version you're about to release.
   Commit on `main`. Release CI verifies `version.txt` matches the
   pushed tag and fails loudly otherwise.
4. On real hardware, walk through the install flow on Windows
   (NSIS), macOS (`.dmg` + Gatekeeper "Open Anyway"), and Linux
   (AppImage). Confirm the tray icon appears, Connect device works,
   and the panel reacts to a Claude Code prompt.
5. Update the release-notes body in `release.yml` if anything needs
   to change about install instructions for this version.
6. `git tag vX.Y.Z && git push --tags`. CI does the rest.

## When a Monday is loud

If multiple signals fire at once (Dependabot PRs, smoke red), drain
in this order:

1. **Smoke red first.** Something concrete is broken right now.
2. **Dependabot PRs second.** They might fix the smoke red, or they
   might be the cause of it; review the diff carefully.
