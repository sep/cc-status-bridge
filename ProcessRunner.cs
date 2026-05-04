using System.Diagnostics;

namespace ClaudeStatusBridge;

/// <summary>
/// Thin wrapper around <see cref="Process.Start(ProcessStartInfo)"/> that
/// captures stdio, optionally suppresses output, and matters now that the
/// bridge is WinExe (CreateNoWindow=true keeps console-subsystem children
/// from getting a fresh OS-allocated window when our process has none).
/// Used by the launchctl / systemctl / schtasks paths in the platform
/// implementations.
/// </summary>
internal static class ProcessRunner
{
    public static int Run(string fileName, params string[] args)
        => RunCore(fileName, suppress: false, args);

    public static int RunQuiet(string fileName, params string[] args)
        => RunCore(fileName, suppress: true, args);

    private static int RunCore(string fileName, bool suppress, string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName               = fileName,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);

            using var proc = Process.Start(psi);
            if (proc is null) return -1;

            var stdout = proc.StandardOutput.ReadToEnd();
            var stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit();

            if (!suppress)
            {
                if (!string.IsNullOrWhiteSpace(stdout)) Console.Write(stdout);
                if (!string.IsNullOrWhiteSpace(stderr)) Console.Error.Write(stderr);
            }
            return proc.ExitCode;
        }
        catch (Exception ex)
        {
            if (!suppress)
                Console.Error.WriteLine($"[installer] failed to run {fileName}: {ex.Message}");
            return -1;
        }
    }
}
