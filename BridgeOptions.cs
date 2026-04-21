namespace ClaudeStatusBridge;

public sealed class BridgeOptions
{
    public string MirrorDir { get; set; } = @"%USERPROFILE%\.claude-status";
    public string ComPort { get; set; } = "COM4";
    public int BaudRate { get; set; } = 115200;
    public int ReconnectDelayMs { get; set; } = 2000;
    public int SerialReopenDelayMs { get; set; } = 1000;

    public string ResolvedMirrorDir =>
        Environment.ExpandEnvironmentVariables(MirrorDir);
}
