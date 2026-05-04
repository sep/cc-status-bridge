namespace ClaudeStatusBridge;

internal static class Log
{
    private const string IsoFormat = "yyyy-MM-ddTHH:mm:ss.fffzzz";
    private static readonly object FileLock = new();
    private static readonly string? LogPath = ResolveLogPath();

    public static string? FilePath => LogPath;

    public static void Info(string msg) => Write(Console.Out, msg);

    public static void Warn(string msg) => Write(Console.Error, msg);

    private static void Write(TextWriter writer, string msg)
    {
        var line = $"{DateTimeOffset.Now.ToString(IsoFormat)} {msg}";
        try { writer.WriteLine(line); } catch { /* no console attached */ }

        if (LogPath is not null)
        {
            try
            {
                lock (FileLock)
                {
                    File.AppendAllText(LogPath, line + Environment.NewLine);
                }
            }
            catch
            {
                // never let a log-file failure crash the bridge
            }
        }
    }

    /// <summary>
    /// Resolve the log file path. Returns null if it can't be determined
    /// or the directory can't be created.
    /// </summary>
    private static string? ResolveLogPath()
    {
        try
        {
            var baseDir = Platform.Current.LogDir;
            Directory.CreateDirectory(baseDir);
            return Path.Combine(baseDir, "bridge.log");
        }
        catch
        {
            return null;
        }
    }
}
