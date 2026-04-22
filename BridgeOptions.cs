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

    public string ComPort { get; set; } = "COM4";
    public int BaudRate { get; set; } = 115200;
    public int ReconnectDelayMs { get; set; } = 2000;
    public int SerialReopenDelayMs { get; set; } = 1000;
    public int RescanIntervalMs { get; set; } = 5000;
    public int ThinkingIdleMs { get; set; } = 8000;
    public int SerialPollIntervalMs { get; set; } = 2000;

    public string? ResolvedMirrorDir =>
        string.IsNullOrWhiteSpace(MirrorDir)
            ? null
            : Environment.ExpandEnvironmentVariables(MirrorDir);
}
