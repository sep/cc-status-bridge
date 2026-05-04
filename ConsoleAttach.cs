using System.Runtime.InteropServices;

namespace ClaudeStatusBridge;

/// <summary>
/// Bridge is compiled as WinExe (Windows subsystem) so that the default
/// tray-app launch path doesn't allocate a console window. CLI subcommands
/// (status / logs / install / etc.) still need stdout/stderr to flow into
/// the user's shell, so when one of those is dispatched we attach to the
/// parent process's console and rebind Console.Out / Error / In.
///
/// Caveat: because we're WinExe, cmd.exe / pwsh don't block waiting for us
/// the way they do for console-subsystem apps. Output written after the
/// shell has already drawn its next prompt will interleave. For
/// interactive CLI (`bridge find`) users should invoke us with
/// `Start-Process -Wait` (pwsh) or `start /wait` (cmd).
///
/// No-op on macOS / Linux — those subsystem mechanics are Windows-only.
/// </summary>
internal static class ConsoleAttach
{
    private const uint ATTACH_PARENT_PROCESS = 0xFFFFFFFFu;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(uint dwProcessId);

    public static void AttachToParentIfCli()
    {
        if (!OperatingSystem.IsWindows()) return;

        try
        {
            if (!AttachConsole(ATTACH_PARENT_PROCESS)) return;

            // .NET's Console streams were initialized at process start when
            // there was no console; they're bound to Stream.Null. Rebind
            // them to the now-attached console so writes/reads work.
            var stdout = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
            Console.SetOut(stdout);
            var stderr = new StreamWriter(Console.OpenStandardError()) { AutoFlush = true };
            Console.SetError(stderr);
            Console.SetIn(new StreamReader(Console.OpenStandardInput()));
        }
        catch
        {
            // never let console-attach failures take down a CLI run
        }
    }
}
