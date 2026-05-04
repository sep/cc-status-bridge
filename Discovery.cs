using System.IO.Ports;
using System.Text.Json.Nodes;

namespace ClaudeStatusBridge;

/// <summary>
/// Cross-platform serial port discovery and ClaudePanel probing.
///
/// Discovery uses SerialPort.GetPortNames() but filters per-platform so we
/// don't bother probing things like /dev/tty.Bluetooth-Incoming-Port (macOS)
/// or /dev/ttyS0 (Linux serial console).
///
/// Probing speaks the firmware v1.2 PING/PONG protocol (FIRMWARE.md §8):
/// we send {"type":"ping","seq":1}, expect a {"type":"pong",...} reply
/// within a short window. If the reply matches, we mark the port as a
/// ClaudePanel and surface useful metadata for display to the user.
/// </summary>
internal static class Discovery
{
    public sealed record ProbeResult(string Port, bool IsClaudePanel, string Detail);

    public static List<string> EnumeratePorts()
    {
        IEnumerable<string> raw;
        try
        {
            raw = SerialPort.GetPortNames();
        }
        catch
        {
            return new List<string>();
        }
        return Platform.Current.FilterSerialPorts(raw).ToList();
    }

    public static ProbeResult Probe(string portName, int baudRate, int timeoutMs = 600)
    {
        SerialPort? port = null;
        try
        {
            port = new SerialPort(portName, baudRate)
            {
                ReadTimeout = timeoutMs,
                WriteTimeout = 500,
                NewLine = "\n",
                DtrEnable = false,
                RtsEnable = false,
            };
            port.Open();

            // Drain anything already buffered (boot logs, heartbeats, etc.).
            try { port.DiscardInBuffer(); } catch { }

            // Send PING.
            var seq = Random.Shared.Next(100000, 999999);
            port.WriteLine($"{{\"type\":\"ping\",\"seq\":{seq}}}");

            // Read up to a few lines, watching for a pong with our seq.
            // The device may emit unrelated lines (heartbeats, logs) that we
            // skip past until we hit a matching pong or run out of time.
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs * 2);
            while (DateTime.UtcNow < deadline)
            {
                string line;
                try { line = port.ReadLine(); }
                catch (TimeoutException) { break; }

                JsonNode? doc;
                try { doc = JsonNode.Parse(line); }
                catch { continue; }
                if (doc is null) continue;

                if (doc["type"]?.GetValue<string>() != "pong") continue;
                if (doc["seq"]?.GetValue<int>() != seq) continue;

                var panelCount = doc["panel_count"]?.GetValue<int>();
                var uptime = doc["uptime_ms"]?.GetValue<long>();
                var detail = $"panel_count={panelCount?.ToString() ?? "?"} uptime_ms={uptime?.ToString() ?? "?"}";
                return new ProbeResult(portName, true, detail);
            }
            return new ProbeResult(portName, false, "no pong");
        }
        catch (UnauthorizedAccessException)
        {
            return new ProbeResult(portName, false, "in use / access denied");
        }
        catch (Exception ex)
        {
            return new ProbeResult(portName, false, ex.Message);
        }
        finally
        {
            try { port?.Close(); } catch { }
            try { port?.Dispose(); } catch { }
        }
    }
}
