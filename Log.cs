namespace ClaudeStatusBridge;

internal static class Log
{
    private const string IsoFormat = "yyyy-MM-ddTHH:mm:ss.fffzzz";

    public static void Info(string msg) => Write(Console.Out, msg);

    public static void Warn(string msg) => Write(Console.Error, msg);

    private static void Write(TextWriter writer, string msg)
    {
        writer.WriteLine($"{DateTimeOffset.Now.ToString(IsoFormat)} {msg}");
    }
}
