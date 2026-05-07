using System.IO.Ports;
using System.Text;
using System.Text.Json.Nodes;

namespace ClaudeStatusBridge;

public sealed class SerialOutput : IDisposable
{
    private readonly BridgeOptions _options;
    private SerialPort? _port;
    private readonly object _lock = new();

    // Reader-side state for the PING/PONG path. The reader runs as a
    // background Task whenever a port is open; on close it gets
    // cancelled and the task is abandoned (the port-close itself will
    // make ReadLine throw, which is the loop's exit signal).
    private CancellationTokenSource? _readerCts;
    private Task? _readerTask;

    /// <summary>
    /// Fires for every successfully-parsed JSON line received from the
    /// device. Currently used by BridgeRunner.PingerAsync to spot
    /// `{"type":"pong",...}` replies, but any caller can subscribe.
    /// Lines that fail to parse as JSON are silently dropped.
    /// </summary>
    public event Action<JsonNode>? LineReceived;

    public SerialOutput(BridgeOptions options)
    {
        _options = options;
    }

    public bool IsOpen
    {
        get
        {
            lock (_lock) return _port?.IsOpen == true;
        }
    }

    public bool IsDeviceAvailable()
    {
        // Step 1: name enumeration. On some USB-CDC drivers the COM name
        // lingers in the registry after the device is unplugged, so a
        // positive answer here isn't sufficient on its own.
        bool listed;
        try
        {
            var ports = SerialPort.GetPortNames();
            listed = false;
            foreach (var p in ports)
            {
                if (string.Equals(p, _options.ComPort, StringComparison.OrdinalIgnoreCase))
                {
                    listed = true;
                    break;
                }
            }
        }
        catch
        {
            return false;
        }
        if (!listed) return false;

        // Steps 2 & 3 both touch the OS port handle and need to coordinate
        // with TryOpen running on another thread. Without this lock the
        // throwaway probe in Step 3 briefly holds the OS handle exclusively
        // — and a concurrent TryOpen() (from Pinger, the state-line emit
        // path, etc.) would land on `Access to the path 'COM4' is denied`
        // for the duration of the probe. Locking serialises them; lock
        // hold time is bounded by the probe's open+close round-trip
        // (single-digit ms in the success case) so contention is
        // negligible.
        lock (_lock)
        {
            // Step 2: if we already hold it open, the device is definitely there.
            if (_port?.IsOpen == true) return true;

            // Step 3: port is listed but we don't hold it — confirm with a
            // throwaway open-close. This catches the "phantom COM port"
            // case where the registry still lists the name after the USB
            // device was unplugged.
            try
            {
                using var probe = new SerialPort(_options.ComPort, _options.BaudRate);
                probe.ReadTimeout = 100;
                probe.WriteTimeout = 100;
                probe.Open();
                probe.Close();
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    public void CloseIfOpen()
    {
        SerialPort? port;
        CancellationTokenSource? readerCts;
        lock (_lock)
        {
            port = _port;
            _port = null;
            readerCts = _readerCts;
            _readerCts = null;
            // _readerTask is intentionally not nulled here; closing the
            // port will make its ReadLine throw, the task exits on its
            // own. Nothing awaits it.
        }
        try { readerCts?.Cancel(); } catch { }
        try { readerCts?.Dispose(); } catch { }
        if (port is not null) ClosePortBestEffort(port);
    }

    /// <summary>
    /// Close a SerialPort without getting stuck on the classic USB-CDC hang:
    /// Close() internally waits for a WaitCommEvent thread that can block
    /// for tens of seconds when the CDC endpoint is pending I/O. We discard
    /// buffers first (helps most of the time) and cap the close at 500ms.
    /// If it's still wedged, the remaining Close/Dispose runs on the thread
    /// pool and we return anyway — process exit will clean up.
    /// </summary>
    private static void ClosePortBestEffort(SerialPort port)
    {
        try { if (port.IsOpen) port.DiscardInBuffer(); } catch { }
        try { if (port.IsOpen) port.DiscardOutBuffer(); } catch { }
        var closeTask = Task.Run(() =>
        {
            try { port.Close(); } catch { }
            try { port.Dispose(); } catch { }
        });
        try { closeTask.Wait(TimeSpan.FromMilliseconds(500)); } catch { }
    }

    public bool TryOpen()
    {
        SerialPort? port;
        CancellationTokenSource? cts;
        lock (_lock)
        {
            if (_port?.IsOpen == true) return true;
            try
            {
                _port?.Dispose();
                _port = new SerialPort(_options.ComPort, _options.BaudRate)
                {
                    Encoding = Encoding.UTF8,
                    NewLine = "\n",
                    // 200ms read timeout (was Infinite) so the reader
                    // loop can pulse: TimeoutException on every empty
                    // window lets it check cancellation and exit
                    // promptly when the port closes. SerialPort
                    // internally buffers partial reads across timeouts,
                    // so a NewLine that straddles a timeout window
                    // still resolves on the next ReadLine.
                    ReadTimeout = 200,
                    WriteTimeout = 1000,
                    // DtrEnable / RtsEnable intentionally left at their default
                    // (false). Asserting and then de-asserting these lines on
                    // open/close was wedging the ESP32-S3 firmware's render
                    // loop on bridge stop. If a future board genuinely needs
                    // hardware flow control, expose this as a config option.
                    DtrEnable = false,
                    RtsEnable = false,
                };
                _port.Open();
                _readerCts = new CancellationTokenSource();
                port = _port;
                cts = _readerCts;
            }
            catch (Exception ex)
            {
                Log.Warn($"[bridge] serial open failed: {ex.Message}");
                _port?.Dispose();
                _port = null;
                _readerCts?.Dispose();
                _readerCts = null;
                return false;
            }
        }
        // Started outside the lock to avoid holding the lock across the
        // Task.Run call (cheap but a clean discipline).
        _readerTask = Task.Run(() => ReaderLoop(port, cts.Token));
        return true;
    }

    private void ReaderLoop(SerialPort port, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            string? line;
            try
            {
                line = port.ReadLine();
            }
            catch (TimeoutException)
            {
                // Empty 200ms window — re-check cancellation and try again.
                continue;
            }
            catch (OperationCanceledException) { break; }
            catch (InvalidOperationException) { break; }   // port closed
            catch (System.IO.IOException)     { break; }   // port disappeared
            catch (Exception ex)
            {
                Log.Warn($"[bridge] serial read failed: {ex.Message}");
                break;
            }

            if (string.IsNullOrEmpty(line)) continue;

            // Tolerate non-JSON garbage (firmware boot logs, partial
            // frames, etc.) — drop without complaint.
            JsonNode? doc;
            try { doc = JsonNode.Parse(line); }
            catch { continue; }
            if (doc is null) continue;

            try { LineReceived?.Invoke(doc); }
            catch { /* swallow listener exceptions; reader is best-effort */ }
        }
    }

    public bool WriteLine(string line)
    {
        lock (_lock)
        {
            if (_port?.IsOpen != true) return false;
            try
            {
                _port.WriteLine(line);
                return true;
            }
            catch (Exception ex)
            {
                Log.Warn($"[bridge] serial write failed: {ex.Message}");
                try { _port.Close(); } catch { }
                _port = null;
                return false;
            }
        }
    }

    public void Dispose() => CloseIfOpen();
}
