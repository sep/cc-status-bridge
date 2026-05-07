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

    // PING/PONG liveness (FIRMWARE.md §8). Bridge emits a ping every
    // PingIntervalMs; if no pong arrives within PingTimeoutMs of any
    // sent ping, the missed-pong counter increments. Crossing
    // PingMissedThreshold logs `device unresponsive` once; recovery
    // logs `device responsive again` once. Routine ping/pong traffic
    // is intentionally NOT logged — would be a per-5-second log line
    // for every bridge install. State updates keep flowing regardless;
    // PING/PONG is purely informational.
    //
    // Setting PingIntervalMs <= 0 disables BOTH the ping ticker AND
    // the device→host serial reader (which currently exists only to
    // surface pongs to the pinger). That switch is the single
    // diagnostic knob for "turn off all v1.1+ device-half behavior" —
    // useful when the deployed firmware predates §8 and ends up with
    // wonky animation in disconnected state from the host's read
    // activity alone.
    public int PingIntervalMs { get; set; } = 5000;
    public int PingTimeoutMs { get; set; } = 2000;
    public int PingMissedThreshold { get; set; } = 3;

    public string? ResolvedMirrorDir =>
        string.IsNullOrWhiteSpace(MirrorDir)
            ? null
            : Environment.ExpandEnvironmentVariables(MirrorDir);
}
