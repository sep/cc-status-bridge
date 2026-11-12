using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace ClaudeStatusBridge;

/// <summary>
/// Device link over SSH (wire protocol v1.4). Runs the platform's OpenSSH
/// client headless with a local forward to the port the device holds on
/// the server, then talks NDJSON over a TCP socket to that local port:
///
///   ssh -N -p PORT -o BatchMode=yes -o ExitOnForwardFailure=yes
///       -o ServerAliveInterval=15 -o ServerAliveCountMax=3
///       -o StrictHostKeyChecking=accept-new
///       -L LOCAL:localhost:LISTEN USER@HOST
///
/// The user's own keys, agent and ~/.ssh/config apply; BatchMode makes a
/// missing key or passphrase a logged exit instead of a hang. The child is
/// started with CreateNoWindow (the bridge is a WinExe; see ProcessRunner).
///
/// Lifecycle: TryOpen() ensures the child is running (spawning with
/// exponential backoff 1 s → 30 s after each exit) and connects the socket
/// (own 2 s backoff). The socket drops when the device's end goes away —
/// ssh closes the local connection when the forwarded channel fails — and
/// BridgeRunner's monitor reopens it on its next poll. Dispose() kills the
/// child so a paused bridge holds no tunnel.
/// </summary>
public sealed class SshTunnelLink : IDeviceLink
{
    private readonly BridgeOptions _o;
    private readonly object _lock = new();

    private Process?       _ssh;
    private TcpClient?     _tcp;
    private NetworkStream? _stream;
    private CancellationTokenSource? _readerCts;
    private Task?          _readerTask;

    private DateTime _nextSpawnAt   = DateTime.MinValue;
    private int      _spawnBackoffMs = 1000;
    private DateTime _nextConnectAt = DateTime.MinValue;

    public event Action<JsonNode>? LineReceived;

    public SshTunnelLink(BridgeOptions options) { _o = options; }

    public string Describe =>
        $"ssh {_o.Ssh.User}@{_o.Ssh.Host}:{_o.Ssh.Port} (-L {_o.Ssh.LocalPort}:localhost:{_o.Ssh.ListenPort})";

    public bool IsOpen
    {
        get { lock (_lock) return _stream is not null && _tcp?.Connected == true; }
    }

    // "Available" = the ssh child is alive. The local port always accepts
    // while ssh runs (ssh fails the *channel*, not the accept, when the
    // device isn't there), so probing it would prove nothing and would
    // leave connect/close noise in the device log every poll.
    public bool IsDeviceAvailable()
    {
        if (IsOpen) return true;
        return EnsureSshProcess();
    }

    public bool TryOpen()
    {
        lock (_lock)
        {
            if (_stream is not null && _tcp?.Connected == true) return true;
            if (!EnsureSshProcess()) return false;
            if (DateTime.UtcNow < _nextConnectAt) return false;
            _nextConnectAt = DateTime.UtcNow.AddSeconds(2);

            CloseSocketLocked();
            try
            {
                var tcp = new TcpClient { NoDelay = true };
                if (!tcp.ConnectAsync("127.0.0.1", _o.Ssh.LocalPort).Wait(2000))
                {
                    tcp.Dispose();
                    return false;
                }
                _tcp    = tcp;
                _stream = tcp.GetStream();
                _readerCts = new CancellationTokenSource();
                var cts = _readerCts;
                var stream = _stream;
                _readerTask = Task.Run(() => ReaderLoop(stream, cts.Token));
                _spawnBackoffMs = 1000;
                Log.Info($"[ssh] connected to local forward 127.0.0.1:{_o.Ssh.LocalPort}");
                return true;
            }
            catch (Exception ex)
            {
                Log.Warn($"[ssh] connect to 127.0.0.1:{_o.Ssh.LocalPort} failed: {ex.GetBaseException().Message}");
                CloseSocketLocked();
                return false;
            }
        }
    }

    public void CloseIfOpen()
    {
        lock (_lock) CloseSocketLocked();
    }

    public bool WriteLine(string line)
    {
        lock (_lock)
        {
            if (_stream is null) return false;
            try
            {
                var bytes = Encoding.UTF8.GetBytes(line + "\n");
                _stream.Write(bytes, 0, bytes.Length);
                _stream.Flush();
                return true;
            }
            catch (Exception ex)
            {
                Log.Warn($"[ssh] write failed: {ex.GetBaseException().Message}");
                CloseSocketLocked();
                return false;
            }
        }
    }

    public void Dispose()
    {
        CloseIfOpen();
        KillSsh();
    }

    // ------------------------------------------------------------------

    private bool EnsureSshProcess()
    {
        lock (_lock)
        {
            if (_ssh is not null && !_ssh.HasExited) return true;
            if (DateTime.UtcNow < _nextSpawnAt) return false;

            if (string.IsNullOrWhiteSpace(_o.Ssh.Host) || string.IsNullOrWhiteSpace(_o.Ssh.User))
            {
                _nextSpawnAt = DateTime.UtcNow.AddSeconds(30);
                Log.Warn("[ssh] Bridge:Ssh:Host / User not configured; transport idle");
                return false;
            }

            var psi = new ProcessStartInfo
            {
                FileName               = string.IsNullOrWhiteSpace(_o.Ssh.Executable) ? "ssh" : _o.Ssh.Executable,
                UseShellExecute        = false,
                CreateNoWindow         = true,
                RedirectStandardInput  = true,   // never a tty: ssh can't prompt
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
            };
            foreach (var a in new[]
            {
                "-N",
                "-p", _o.Ssh.Port.ToString(),
                "-o", "BatchMode=yes",
                "-o", "ExitOnForwardFailure=yes",
                "-o", "ServerAliveInterval=15",
                "-o", "ServerAliveCountMax=3",
                "-o", "StrictHostKeyChecking=accept-new",
                "-L", $"{_o.Ssh.LocalPort}:localhost:{_o.Ssh.ListenPort}",
            }) psi.ArgumentList.Add(a);
            if (!string.IsNullOrWhiteSpace(_o.Ssh.IdentityFile))
            {
                var key = _o.Ssh.IdentityFile.Trim();
                if (key.StartsWith("~/") || key == "~")
                    key = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), key.Length > 2 ? key[2..] : "");
                if (!File.Exists(key))
                    Log.Warn($"[ssh] identity file not found: {key}");
                psi.ArgumentList.Add("-i");
                psi.ArgumentList.Add(key);
                psi.ArgumentList.Add("-o");
                psi.ArgumentList.Add("IdentitiesOnly=yes");
            }
            if (!string.IsNullOrWhiteSpace(_o.Ssh.ExtraArgs))
                foreach (var a in _o.Ssh.ExtraArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    psi.ArgumentList.Add(a);
            psi.ArgumentList.Add($"{_o.Ssh.User}@{_o.Ssh.Host}");

            try
            {
                var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
                p.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) Log.Warn($"[ssh] {e.Data}"); };
                p.OutputDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) Log.Info($"[ssh] {e.Data}"); };
                p.Exited += (_, _) =>
                {
                    int code;
                    try { code = p.ExitCode; } catch { code = -1; }
                    lock (_lock)
                    {
                        Log.Warn($"[ssh] exited with code {code}; respawn in {_spawnBackoffMs} ms");
                        _nextSpawnAt    = DateTime.UtcNow.AddMilliseconds(_spawnBackoffMs);
                        _spawnBackoffMs = Math.Min(_spawnBackoffMs * 2, 30_000);
                        CloseSocketLocked();
                    }
                };
                if (!p.Start()) { _nextSpawnAt = DateTime.UtcNow.AddSeconds(5); return false; }
                p.BeginErrorReadLine();
                p.BeginOutputReadLine();
                _ssh = p;
                Log.Info($"[ssh] started: {psi.FileName} {string.Join(' ', psi.ArgumentList)}");
                // Give the handshake a moment before the first connect attempt.
                _nextConnectAt = DateTime.UtcNow.AddMilliseconds(1500);
                return true;
            }
            catch (Exception ex)
            {
                Log.Warn($"[ssh] failed to start '{psi.FileName}': {ex.Message} (is OpenSSH installed and on PATH?)");
                _nextSpawnAt    = DateTime.UtcNow.AddMilliseconds(_spawnBackoffMs);
                _spawnBackoffMs = Math.Min(_spawnBackoffMs * 2, 30_000);
                return false;
            }
        }
    }

    private void KillSsh()
    {
        Process? p;
        lock (_lock) { p = _ssh; _ssh = null; }
        if (p is null) return;
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
        try { p.Dispose(); } catch { }
    }

    private void CloseSocketLocked()
    {
        try { _readerCts?.Cancel(); } catch { }
        try { _stream?.Dispose(); } catch { }
        try { _tcp?.Dispose(); } catch { }
        _stream = null;
        _tcp = null;
        _readerCts = null;
        _readerTask = null;
    }

    private void ReaderLoop(NetworkStream stream, CancellationToken ct)
    {
        try
        {
            using var reader = new StreamReader(stream, Encoding.UTF8);
            while (!ct.IsCancellationRequested)
            {
                var line = reader.ReadLine();
                if (line is null) break;                       // EOF: device end went away
                if (line.Length == 0) continue;
                JsonNode? doc;
                try { doc = JsonNode.Parse(line); } catch { continue; }
                if (doc is null) continue;
                try { LineReceived?.Invoke(doc); } catch { }
            }
        }
        catch (Exception) { /* socket closed under us */ }
        if (!ct.IsCancellationRequested)
        {
            Log.Warn("[ssh] forwarded connection closed by the far end");
            lock (_lock) CloseSocketLocked();
        }
    }
}
