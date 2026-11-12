# Display management — routing and layout move to the bridge

**Status: design, 2026-11-12.** Companion to [`FIRMWARE.md`](FIRMWARE.md)
§8 "Remote link over SSH (v1.4)". That document is the device wire
contract; this one is the host-side contract between the plugin, the
bridge, and the user.

## Problem

Today a session reaches the display only after the user types
`/llmstatus:show <slot>` inside that session, which (a) must happen
before the long-running prompt they actually care about, (b) needs a
one-time `/llmstatus:permit` so the command doesn't prompt, and (c) rides
on the most fragile part of the system — slash-command delivery differs
per host (Copilot rewrites prompts, parses frontmatter strictly, doesn't
expand `${CLAUDE_PLUGIN_ROOT}`). Panel layout (`/llmstatus:configure`)
has the same shape. The bridge already has every input these commands
produce or consume, and a tray icon, and nowhere to put a window.

## Decision

The **bridge** becomes the primary place to (1) describe the display
hardware and (2) assign sessions to display slots, through a management
window reached from the tray icon. The plugin keeps publishing session
state; its slash commands remain as a secondary way to write the same
files, for the "I'm in this session and know it" case, and stop being
required.

Nothing in the device wire protocol changes for this. Everything below
is host-side.

## Contracts

### 1. Session metadata (plugin → bridge) — `broker.json` v2

`<status dir>/sessions/<session_id>/broker.json` gains fields so a human
can tell sessions apart:

```json
{"pid": 1234, "port": 51321, "session_id": "f584be84-…", "platform": "wsl",
 "cwd": "/mnt/w/sep/claude-status", "started_at": 1762950000.1}
```

- `cwd` — the session's working directory, taken from the first hook
  event that carries one (Claude Code and Copilot both send `cwd` on
  every hook). Absent until then.
- `started_at` — Unix seconds when the broker came up.

Events published by the broker additionally carry `cwd` (added to the
plugin's forwarded-field list). The bridge keeps, per session, the
**last user prompt's first line** from `UserPromptSubmit` events, for
labelling; nothing new is persisted for that.

Readers MUST tolerate the fields' absence (older plugin).

### 2. Routing state — unchanged files, new writer

`routes.json` (`{"<session_id>": "<slot>"}`) and `panel_layout.json`
keep their shapes and locations (the well-known status dir plus the
per-agent data dirs; see the plugin's MAINTENANCE.md "State topology").
The bridge becomes a writer and MUST implement the same rules
`pin.py` does, because the WSL/Windows convergence work depends on them:

| Rule | Meaning |
|---|---|
| One slot per session, one session per slot | Binding `S → slot` evicts any other session on `slot` and releases any other slot `S` held. |
| Prune at bind time | Routes whose session has no `broker.json` in any base are dropped on every write. |
| Write to all targets | The well-known status dir **and** every data dir the bridge found routes in, so the plugin-side readers stay coherent. |
| Valid slots | `<N>`, `<N>a`, `<N>b`, with `N` within `first_id … first_id + panel_count - 1` of the current layout. |

The slash commands continue to write through `pin.py`; both writers
converge on the same files, last writer wins, and the bridge re-reads on
every rescan (5 s) and every emit, as today.

### 3. Occupancy (device → bridge) — already in `pong`

`pong.clients` lists every slot the device has ever received state for,
with its current state. The window uses it to show what each cell is
doing right now, including slots driven by *another* bridge over the
remote link. v1.5 candidate: a per-slot channel tag so the window can say
"held by another bridge" rather than merely "busy".

## The window

One window, three tabs, opened from the tray menu ("Manage display…").
Avalonia, like `ConnectDeviceWindow` and `LogsWindow`.

### Display tab

- **Layout editor** — panel count (1–4), panel size (64×32 default),
  first id, and a **split** toggle per panel. Split is purely a UI
  notion: it decides whether that panel offers one drop target (`N`) or
  two (`Na`, `Nb`); the firmware already renders whichever slots receive
  state. Saving writes `panel_layout.json`; the bridge sends `configure`
  on mismatch (FIRMWARE.md §8, v1.4 rule).
- **Slot grid** — one cell per offered slot, drawn in the panel's
  physical arrangement. Each cell shows: the session assigned by *this*
  bridge (label), or the live state from `pong` when something else is
  driving it, or empty.
- **Session list** — every session with a live `broker.json` on this
  machine: label = last path component of `cwd` + age + last prompt's
  first line (truncated), with the full `cwd` and session id in a
  tooltip. Sorted newest first. Routed sessions show their slot.
- **Assign**: click a session, click a cell (v1). Drag-and-drop (v1.1,
  polish). Click an occupied cell's "×" to unroute.
- **Auto-assign new sessions** (checkbox, persisted in bridge settings,
  default **on**): when a session appears and has no route, bind it to
  the lowest free offered slot. This is the fix for "forgot to run
  `show` before the long prompt". When no slot is free, nothing happens
  and the session shows as "waiting" in the list.
- **Identify** button — sends `identify` (unchanged).

### Remote link tab

The provisioning flow from FIRMWARE.md §8 v1.4: USB connection required;
`keygen` → copyable `authorized_keys` line → Wi-Fi + server fields →
`provision` → fingerprint from `hostkey_seen` with a **Pin** button →
`tunnel_status` readout. A **Transport** selector (Serial / SSH) with the
SSH fields the bridge itself needs (`host`, `port`, `user`,
`listen_port`, local port).

### Logs tab

The existing `LogsWindow` content.

## Bridge settings (additions)

```json
"Bridge": {
  "Transport": "serial",            // or "ssh"
  "AutoAssignNewSessions": true,
  "Ssh": { "Host": "", "Port": 22, "User": "claudepanel",
           "ListenPort": 7777, "LocalPort": 7777 }
}
```

## What changes where

| Repo | Change |
|---|---|
| plugin | forward `cwd`; broker writes `cwd` + `started_at`; docs say the commands are optional; `/permit` wording softened. Version bump. |
| bridge | routing writer (port of `pin.py` rules), session labels, management window (3 tabs), auto-assign, settings. |
| firmware | none for this document (v1.4 wire changes are separate). |

## Out of scope (for now)

- Cross-bridge slot collision detection beyond "cell shows busy". Needs
  a device-side channel tag (v1.5).
- Per-session colours / names persisted anywhere.
- Removing the slash commands. They stay as a secondary writer.
