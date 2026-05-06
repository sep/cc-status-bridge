# Contributing to claude-status-bridge

## Coding conventions

### Layout

C# defaults: 4-space indent, K&R braces, `var` for local types when
the right-hand side makes the type obvious, explicit types when it
doesn't.

- 100-column soft limit, 120 hard.
- One class per file. Filename matches class name.
- File-scoped namespaces (`namespace ClaudeStatusBridge;` not the
  block form).
- `using` directives at the top, sorted, blank line between
  `System.*` and other groups.

### Naming

C# defaults — match what the BCL does. We don't enforce via tooling
yet; the rules below are followed by hand:

| Kind                                | Style          | Example                                        |
| ----------------------------------- | -------------- | ---------------------------------------------- |
| Public methods, properties, events  | `PascalCase`   | `Install`, `IsRegistered`, `AggregateStateChanged` |
| Local variables, parameters         | `camelCase`    | `exePath`, `comPort`                           |
| Private fields (instance, static)   | `_camelCase`   | `_runner`, `_options`                          |
| Constants                           | `PascalCase`   | `WindowsRunKey`, `ServiceName`                 |
| Types (class, struct, interface)    | `PascalCase`   | `IPlatform`, `BridgeRunner`, `SerialOutput`    |
| Interfaces                          | `IPascalCase`  | `IPlatform`                                    |
| Enum values                         | `PascalCase`   | `StateMapper.StateWorking`                     |

### Platform abstraction

Don't reach for `OperatingSystem.IsXxx()` in feature code. Add a
method or property to [`IPlatform`](IPlatform.cs) and implement it on
each platform class. The single OS-dispatch point in the codebase is
`Platform.Current` — `[SupportedOSPlatform]` on each platform class
plus the runtime guards in the factory keep the analyzer happy.

The exception is `ConsoleAttach.HideConsoleIfOwned`'s
`OperatingSystem.IsWindows()` early-return — that code touches Win32
console APIs that are fundamentally Windows-only.

### When in doubt

Match the surrounding code. The codebase is small enough that
consistency beats perfection.

## Wire-protocol changes

Any change to the bridge ↔ firmware JSON shape goes in
[`FIRMWARE.md`](FIRMWARE.md) **first**, then implementation here and
in the firmware repo. The plugin and firmware authors both read the
canonical to know what to send. Don't drift the bridge ahead of the
spec — and don't fork the spec into other repos either, even
temporarily.

## Adding a new tray menu item

Tray menu construction lives in [`TrayHost.cs`](TrayHost.cs):

1. Add a `private static NativeMenuItem? _myNewItem;` field at the
   top.
2. Construct it in `BuildMenu()` with a `Click` handler.
3. Insert it into the menu in `BuildMenuRoot()` at the right
   position relative to existing items.
4. If the item triggers anything that touches the serial port,
   reuse `_serial?.Dispose()` + `_serial = new SerialOutput(_options)`
   patterns from `ChangeComPortAsync` rather than reimplementing
   the dispose+recreate dance.

## Adding a new CLI subcommand

Subcommand dispatch lives in [`Program.cs`](Program.cs). Add a
`case` that calls into `Installer.Xxx()` (for cross-platform
operations) or `Platform.Current.Xxx()` (for OS-specific ones).
Update the `PrintHelp` synopsis and the bridge install page's CLI
table at [`docs/index.md`](docs/index.md).

## Commit & PR style

- Keep commits small and reviewable.
- Commit messages: imperative subject, body if non-obvious. "Why"
  matters more than "what" — the diff already shows what.
- Don't commit `bin/`, `obj/`, `publish/`, `*.user`, or `appsettings.local.json`
  — they're in [`.gitignore`](.gitignore).
- Tag releases with `vX.Y.Z` after bumping `version.txt` to match.
  CI verifies the two are in lockstep and refuses to publish if
  they aren't.
