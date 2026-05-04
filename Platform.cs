using System.Runtime.InteropServices;

namespace ClaudeStatusBridge;

/// <summary>
/// Resolves the right <see cref="IPlatform"/> implementation once at
/// startup based on <see cref="OperatingSystem"/>. The runtime check is
/// deliberately localized here so the analyzer can prove that platform-
/// guarded constructors are only called on their target OS.
/// </summary>
internal static class Platform
{
    public static IPlatform Current { get; } = Resolve();

    public static string Name
    {
        get
        {
            if (OperatingSystem.IsWindows()) return "windows";
            if (OperatingSystem.IsMacOS())   return "macos";
            if (OperatingSystem.IsLinux())   return "linux";
            return RuntimeInformation.OSDescription;
        }
    }

    private static IPlatform Resolve()
    {
        if (OperatingSystem.IsWindows()) return new WindowsPlatform();
        if (OperatingSystem.IsMacOS())   return new MacOSPlatform();
        if (OperatingSystem.IsLinux())   return new LinuxPlatform();
        throw new PlatformNotSupportedException(
            $"unsupported platform: {RuntimeInformation.OSDescription}");
    }
}
