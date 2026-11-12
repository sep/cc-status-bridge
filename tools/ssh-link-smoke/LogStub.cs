namespace ClaudeStatusBridge;
// Stand-in for the bridge's Log (which resolves a platform log path); the
// harness just wants the lines on the console.
internal static class Log
{
    public static void Info(string msg) => Console.WriteLine($"  [log] {msg}");
    public static void Warn(string msg) => Console.WriteLine($"  [log!] {msg}");
}
