using System.Runtime.InteropServices;

namespace ClaudeStatusBridge;

/// <summary>
/// Windows console hygiene. We compile as a Console-subsystem app so that
/// pwsh / cmd wait for the process and output flows correctly. The cost is
/// that when the OS creates a console for us (because we were launched
/// from a parent that doesn't have one — typically a scheduled task), a
/// brief window flash appears.
///
/// HideConsoleIfOwned solves that: it asks the OS how many processes are
/// attached to our console. If it's just us (count == 1), we own that
/// console (the OS allocated it for our process), so we hide its window.
/// If multiple PIDs are attached (count > 1), we share a console with our
/// parent (typically pwsh / cmd), and we leave the window alone.
///
/// No-op on macOS / Linux.
/// </summary>
internal static class ConsoleAttach
{
    private const int SW_HIDE = 0;

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("kernel32.dll")]
    private static extern uint GetConsoleProcessList(uint[] processList, uint count);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    public static void HideConsoleIfOwned()
    {
        if (!OperatingSystem.IsWindows()) return;

        try
        {
            var pids = new uint[16];
            var count = GetConsoleProcessList(pids, (uint)pids.Length);
            if (count != 1) return;  // shared console (parent has one); leave alone

            var hwnd = GetConsoleWindow();
            if (hwnd != IntPtr.Zero)
                ShowWindow(hwnd, SW_HIDE);
        }
        catch
        {
            // never let console-hide failures take down the bridge
        }
    }
}
