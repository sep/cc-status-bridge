namespace ClaudeStatusBridge;

public sealed class SshOptions
{
    public string Host       { get; set; } = "";
    public int    Port       { get; set; } = 22;
    public string User       { get; set; } = "claudepanel";
    /// <summary>Port the device holds on the server's loopback (its listen_port).</summary>
    public int    ListenPort { get; set; } = 7777;
    /// <summary>Port on this machine's loopback that ssh -L binds.</summary>
    public int    LocalPort  { get; set; } = 7777;
    /// <summary>OpenSSH client; "ssh" on PATH by default.</summary>
    public string Executable { get; set; } = "ssh";
    /// <summary>Extra ssh arguments, space-separated (e.g. "-i C:/keys/bridge_ed25519").</summary>
    public string ExtraArgs  { get; set; } = "";
}

public sealed class BridgeOptions
{
    /// <summary>
    /// "serial" (USB-Serial-JTAG; the default) or "ssh" (wire v1.4 remote
    /// link through <see cref="Ssh"/>). Selects the <see cref="IDeviceLink"/>.
    /// </summary>
    public string Transport { get; set; } = "serial";
    public SshOptions Ssh { get; set; } = new();

    /// <summary>
    /// MANAGEMENT.md: when a session appears with no route, bind it to the
    /// lowest free offered slot so it lights up without a slash command.
    /// </summary>
    public bool AutoAssignNewSessions { get; set; } = true;

    /// <summary>
    /// Panel numbers the management window offers as two half slots (Na/Nb)
    /// instead of one full slot. A UI notion only; the firmware renders
    /// whichever slots receive state.
    /// </summary>
    public int[] SplitPanels { get; set; } = Array.Empty<int>();
    public bool UsesSsh => string.Equals(Transport, "ssh", StringComparison.OrdinalIgnoreCase);

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
    // Setting PingIntervalMs <= 0 disables the ping ticker. The
    // device→host serial reader runs independently (it's the
    // subscription surface for any device→host JSON, not just pongs),
    // so disabling pings doesn't disable reads.
    public int PingIntervalMs { get; set; } = 5000;
    public int PingTimeoutMs { get; set; } = 2000;
    public int PingMissedThreshold { get; set; } = 3;

    public string? ResolvedMirrorDir =>
        string.IsNullOrWhiteSpace(MirrorDir)
            ? null
            : Environment.ExpandEnvironmentVariables(MirrorDir);
}
