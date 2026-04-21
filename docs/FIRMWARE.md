# ESP32 firmware contract — claude-status

This document is self-contained context for building the ESP32 firmware half
of the `claude-status` system. It is intended to be read in a fresh Claude
Code session on a development machine that has ESP-IDF v6.0 installed.

> **If you are a future Claude session reading this:** you do not need
> access to the original design conversation. Everything the firmware needs
> to know is captured below. The authoritative spec is the **Serial wire
> contract** section; other sections are context and suggestions.

---

## 1. System overview

```
 Claude Code (WSL)
    │
    ▼ lifecycle hooks (stdin JSON)
 emit.py  ─────►  broker.py                (localhost TCP, NDJSON, PUB/SUB)
                        │
                        ▼ SUB subscription
                   ClaudeStatusBridge.exe  (Windows-side, C# / .NET 10)
                        │
                        ▼ NDJSON over USB-Serial-JTAG @ 115200
                   ESP32-S3               ◄── YOU ARE HERE
                        │
                        ▼ HUB75
                   WaveShare RGB-Matrix-P2.5-64x32
```

The firmware's only job is to:

1. Read NDJSON lines from its USB-Serial-JTAG interface.
2. Maintain a "current state" in memory.
3. Render something visually meaningful on the 64×32 HUB75 matrix based on
   state (and any auxiliary fields, if desired).

The firmware **does not** need to speak back to the host. This is a
unidirectional feed for v1. If logging is needed, emit it on UART0 (the
dev-board's on-chip UART) rather than USB-CDC, so it doesn't interfere with
the incoming data channel.

---

## 2. Hardware assumptions

| Component      | Value                                                 |
|----------------|-------------------------------------------------------|
| MCU            | ESP32-S3 (native USB, USB-Serial-JTAG peripheral)     |
| Panel          | WaveShare RGB-Matrix-P2.5-64x32 (standard HUB75 IDC)  |
| Host transport | USB cable to Windows PC; shows up as a `COM` port     |
| Baud rate      | 115200 (cosmetic for USB-CDC; use it anyway for       |
|                | consistency with the bridge's configured value)       |

HUB75 pinout (standard 16-pin IDC):

```
R1 G1 B1 GND   R2 G2 B2 E
A  B  C  D     CLK LAT OE GND
```

Assign ESP32-S3 GPIOs to these signals per whatever HUB75 library is chosen
(see §6). The E pin is unused on a 32-row panel (1/16 scan) but must still
be connected to a safe GPIO.

---

## 3. Serial wire contract (authoritative)

Each message from host to device is **one UTF-8 JSON object**, terminated
by a single `\n` (0x0A).

Example (as bytes sent on the wire):

```
{"state":"working","event":"UserPromptSubmit","ts":1713648012.14}\n
```

### Fields

| Field   | Type    | Req? | Notes                                                |
|---------|---------|------|------------------------------------------------------|
| `state` | string  | yes  | Lexicon in §4. Firmware MUST handle these three.     |
| `event` | string  | no   | Original Claude Code hook event name. Informational. |
| `ts`    | number  | no   | Unix timestamp (seconds, float). Useful for          |
|         |         |      | animations keyed on "time since last change."        |

**Forward compatibility:** the firmware MUST ignore unknown fields. Future
versions of the bridge may emit metrics like `subagent_count`, `tool_name`,
or similar. Treat each line independently; do not accumulate state across
lines based on "was this field present last time?"

**Malformed lines:** if a line fails to parse as JSON or lacks a valid
`state`, drop it silently. Do not crash, do not block subsequent lines.

**Rate:** expect bursts of a few lines per second during active Claude
sessions, with long idle periods in between. Buffer input to accommodate.

---

## 4. State lexicon (v1)

| `state` value | Meaning                                                  |
|---------------|----------------------------------------------------------|
| `"working"`   | Claude received a prompt and is processing.              |
| `"idle"`      | Claude has finished its turn; waiting for next prompt.   |
| `"blocked"`   | Claude is blocked on user input (permission prompt,      |
|               | notification requiring acknowledgment).                  |

On startup, before any line is received, the firmware should render an
**implicit "unknown"** state (e.g. dim white, a "..." icon, or a connection
glyph) so the user knows the device is alive but not yet connected. Once
the first valid line arrives, switch to the reported state.

---

## 5. Suggested firmware structure (ESP-IDF v6.0)

Three FreeRTOS tasks, communicating through a shared state struct
protected by a mutex (or use atomics for the state enum).

```c
typedef enum {
    STATUS_UNKNOWN = 0,
    STATUS_IDLE,
    STATUS_WORKING,
    STATUS_BLOCKED,
} status_state_t;

typedef struct {
    status_state_t state;
    int64_t        changed_at_us;   // esp_timer_get_time() at last change
    char           event[32];       // optional: last event name
} status_snapshot_t;
```

### Task 1 — Serial reader

- Install the USB-Serial-JTAG driver
  (`usb_serial_jtag_driver_install` / ESP-IDF's console component).
- In a loop: read bytes into a line buffer until `\n`, then parse.
- On parse success, update the shared snapshot and notify the render task
  (e.g. via an event group or simply by writing to the shared struct).

### Task 2 — Matrix render

- Drives the HUB75 panel at ~60–120 Hz refresh.
- Reads the shared snapshot each frame.
- Renders state-specific visuals. Suggestions (non-prescriptive):
  - `UNKNOWN`:  slow blue breathing, small "?" glyph
  - `IDLE`:     dim green dot, maybe a clock
  - `WORKING`:  yellow pulse, intensity keyed to `(now - changed_at)`
  - `BLOCKED`:  red with attention icon, maybe a slow blink
- Use `changed_at_us` to drive animations keyed to time-since-state-change
  rather than absolute time, so transitions feel responsive.

### Task 3 (optional) — Diagnostics

- Log to UART0 (the dev-board's non-USB serial) at some useful cadence,
  e.g. state changes or connection events. Never log to USB-CDC, as that
  channel is reserved for incoming host data.

---

## 6. HUB75 driver options for ESP-IDF

As of this writing, the most battle-tested HUB75 driver on ESP32-S3 is
**`mrfaptastic/ESP32-HUB75-MatrixPanel-DMA`**. It's primarily Arduino but
has ESP-IDF-compatible forks / components. Relevant considerations:

- Uses the S3's LCD_CAM peripheral (or I2S parallel on older chips) with
  DMA — frees the CPU for the render task.
- Configure panel dimensions: 64 width × 32 height, 1/16 scan.
- Color depth: typically 8-bit per channel after BCM, good enough for
  smooth animations on a 2.5mm panel.

Other paths a firmware author might explore (unverified, research
independently before committing):

- ESP-IDF native components: search the component registry at
  <https://components.espressif.com/> for "hub75" or "matrix".
- Pure ESP-IDF ports of `mrfaptastic` — several exist on GitHub; quality
  varies, inspect commit history.
- Rolling a custom driver on top of the `esp_lcd` peripheral: possible but
  significant effort; HUB75's row-select / OE latching is not a native
  LCD panel pattern.

Pin assignments should be captured in menuconfig or a `matrix_config.h`
header, not hard-coded, so they can be adjusted to match the physical
wiring.

---

## 7. Testing the firmware without Claude

You do not need to run Claude Code to exercise the firmware. You can
send lines directly into the COM port:

### From a Windows PowerShell (with the device on COM4):

```powershell
$p = [System.IO.Ports.SerialPort]::new('COM4', 115200, 'None', 8, 'One')
$p.NewLine = "`n"
$p.Open()
$p.WriteLine('{"state":"working","event":"test","ts":0}')
Start-Sleep -Seconds 2
$p.WriteLine('{"state":"blocked","event":"test","ts":0}')
Start-Sleep -Seconds 2
$p.WriteLine('{"state":"idle","event":"test","ts":0}')
$p.Close()
```

### From Linux (after the device enumerates as `/dev/ttyACM0` or similar):

```bash
stty -F /dev/ttyACM0 115200 cs8 -cstopb -parenb raw
echo '{"state":"working"}' > /dev/ttyACM0
sleep 2
echo '{"state":"blocked"}' > /dev/ttyACM0
sleep 2
echo '{"state":"idle"}' > /dev/ttyACM0
```

### Soak test — random transitions every second:

```bash
while true; do
  s=$(shuf -n1 -e idle working blocked)
  echo "{\"state\":\"$s\",\"ts\":$(date +%s.%N)}" > /dev/ttyACM0
  sleep 1
done
```

This is the fastest iteration loop for firmware work: no Claude, no
bridge, no broker — just hand-crafted NDJSON into the serial port.

---

## 8. Known extension points (for later)

When adding firmware support for future fields the bridge may emit,
prefer a data-driven approach over hard-coding. Likely future fields:

- `subagent_count` (int) — number of currently-running Claude subagents.
  Natural rendering: a vertical bar graph on one edge of the matrix.
- `tool_name` (string) — currently-executing tool. Rendering: small text
  banner, or a themed icon.
- `elapsed_ms` (int) — time since current state started. The firmware
  can already compute this locally from `ts` + `esp_timer_get_time`, so
  this field is only useful if the host has more precise info.

---

## 9. Getting unstuck

- **Device not enumerating as a COM port on Windows:** check that the
  ESP32-S3 is in normal run mode (not bootloader); the native USB CDC
  should auto-install on Windows 10+.
- **Seeing garbled bytes on the serial channel:** confirm UART logging is
  routed to UART0, not USB-CDC; check `CONFIG_ESP_CONSOLE_USB_SERIAL_JTAG`
  vs `CONFIG_ESP_CONSOLE_UART_DEFAULT` in menuconfig.
- **Lines appearing truncated:** increase the input buffer on the serial
  reader task; bursts from the bridge can be multiple lines deep.
- **Matrix flickers:** the HUB75 render task must have CPU headroom. If
  the serial reader is busy-looping or doing heavy JSON work, starvation
  can cause visual tearing. Keep the reader's per-loop work minimal.

---

## 10. Pointers to the other halves

- Plugin (hooks + broker, Python): `/mnt/w/sep/claude-status/` in the WSL
  side of the dev environment. The relevant source files are
  `bin/broker.py` (TCP NDJSON broker) and `bin/emit.py` (hook publisher).
- Bridge (Windows-side transport, C#): sibling directory
  `/mnt/w/sep/claude-status-bridge/`. See `SerialOutput.cs` and
  `StateMapper.cs` for what the bridge emits onto the serial port —
  this is the authoritative upstream for the wire contract in §3.
- The bridge mirrors broker discovery state to
  `%USERPROFILE%\.claude-status\sessions\<id>\broker.json` on the Windows
  side, so the Windows EXE can find live sessions without traversing
  `\\wsl$\`. Firmware does not need to know about this — it only sees
  the serial output.
