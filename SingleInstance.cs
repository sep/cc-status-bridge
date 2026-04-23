namespace ClaudeStatusBridge;

/// <summary>
/// Per-user single-instance lock. Acquires an exclusive OS-level file lock
/// at startup; if another bridge process holds it, returns null and the
/// caller exits cleanly. The lock is released when the process exits
/// (the OS reclaims file handles).
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    private FileStream? _lock;

    public string LockPath { get; }

    private SingleInstance(string lockPath, FileStream lockStream)
    {
        LockPath = lockPath;
        _lock = lockStream;
    }

    public static SingleInstance? TryAcquire()
    {
        var dir = ResolveLockDir();
        Directory.CreateDirectory(dir);
        var lockPath = Path.Combine(dir, "bridge.lock");
        try
        {
            var fs = new FileStream(
                lockPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None);
            fs.SetLength(0);
            var bytes = System.Text.Encoding.UTF8.GetBytes(Environment.ProcessId.ToString());
            fs.Write(bytes);
            fs.Flush();
            return new SingleInstance(lockPath, fs);
        }
        catch (IOException)
        {
            return null;  // another instance holds the lock
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string ResolveLockDir()
    {
        if (OperatingSystem.IsWindows())
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "claude-status-bridge");
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsMacOS())
            return Path.Combine(home, "Library", "Caches", "claude-status-bridge");
        return Path.Combine(home, ".local", "state", "claude-status-bridge");
    }

    public void Dispose()
    {
        try { _lock?.Dispose(); } catch { }
        _lock = null;
        try { File.Delete(LockPath); } catch { }
    }
}
