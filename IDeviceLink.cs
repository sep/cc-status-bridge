using System.Text.Json.Nodes;

namespace ClaudeStatusBridge;

/// <summary>
/// A full-duplex NDJSON byte stream to the display firmware. Two
/// implementations: <see cref="SerialOutput"/> (USB-Serial-JTAG, the
/// original) and <see cref="SshTunnelLink"/> (a TCP socket through a
/// headless OpenSSH local forward to the device's remote forward; wire
/// protocol v1.4, FIRMWARE.md §8 "Remote link over SSH"). BridgeRunner
/// treats both identically: open when available, write lines, re-emit on
/// reconnect, PING/PONG for liveness.
/// </summary>
public interface IDeviceLink : IDisposable
{
    /// <summary>Human-readable target for logs and the status label.</summary>
    string Describe { get; }

    bool IsOpen { get; }

    /// <summary>
    /// Cheap "could TryOpen succeed right now?" probe, polled every
    /// SerialPollIntervalMs. Must not block for long.
    /// </summary>
    bool IsDeviceAvailable();

    /// <summary>Open (or confirm open). Implementations rate-limit retries.</summary>
    bool TryOpen();

    void CloseIfOpen();

    /// <summary>Write one line (no trailing newline needed). False = dropped.</summary>
    bool WriteLine(string line);

    /// <summary>Every successfully parsed JSON line received from the device.</summary>
    event Action<JsonNode>? LineReceived;
}
