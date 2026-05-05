namespace ClaudeStatusBridge;

public sealed class BridgeOptions
{
    /// <summary>
    /// Optional explicit override for the state directory. When set, the
    /// bridge looks ONLY here. When empty (default), the bridge
    /// auto-discovers data roots across all supported platforms — see
    /// <see cref="BrokerClient.CandidateDataRoots"/>.
    /// </summary>
    public string MirrorDir { get; set; } = "";

    /// <summary>
    /// Configured serial port. Empty string = "no port chosen yet" — at
    /// startup the tray host treats that (or any value that doesn't look
    /// like a valid port for the running OS, e.g. "COM4" on macOS) as a
    /// trigger to pop the first-run device picker rather than silently
    /// failing to open a non-existent port.
    /// </summary>
    public string ComPort { get; set; } = "";
    public int BaudRate { get; set; } = 115200;
    public int ReconnectDelayMs { get; set; } = 2000;
    public int SerialReopenDelayMs { get; set; } = 1000;
    public int RescanIntervalMs { get; set; } = 5000;
    public int ThinkingIdleMs { get; set; } = 8000;
    public int InterruptIdleMs { get; set; } = 300000;  // 5 minutes
    public int SerialPollIntervalMs { get; set; } = 2000;

    public string? ResolvedMirrorDir =>
        string.IsNullOrWhiteSpace(MirrorDir)
            ? null
            : Environment.ExpandEnvironmentVariables(MirrorDir);
}
