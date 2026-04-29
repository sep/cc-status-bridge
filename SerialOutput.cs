using System.IO.Ports;
using System.Text;

namespace ClaudeStatusBridge;

public sealed class SerialOutput : IDisposable
{
    private readonly BridgeOptions _options;
    private SerialPort? _port;
    private readonly object _lock = new();

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

        // Step 2: if we already hold it open, the device is definitely there.
        lock (_lock)
        {
            if (_port?.IsOpen == true) return true;
        }

        // Step 3: port is listed but we don't hold it — confirm with a
        // throwaway open-close. This catches the "phantom COM port" case
        // where the registry still lists the name after the USB device
        // was unplugged.
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

    public void CloseIfOpen()
    {
        SerialPort? port;
        lock (_lock)
        {
            port = _port;
            _port = null;
        }
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
                    ReadTimeout = SerialPort.InfiniteTimeout,
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
                return true;
            }
            catch (Exception ex)
            {
                Log.Warn($"[bridge] serial open failed: {ex.Message}");
                _port?.Dispose();
                _port = null;
                return false;
            }
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
