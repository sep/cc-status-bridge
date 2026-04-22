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
                    DtrEnable = true,
                    RtsEnable = true,
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

    public void Dispose()
    {
        lock (_lock)
        {
            try { _port?.Close(); } catch { }
            _port?.Dispose();
            _port = null;
        }
    }
}
