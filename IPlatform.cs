namespace ClaudeStatusBridge;

/// <summary>
/// Single abstraction for every OS-divergent concern in the bridge.
/// One concrete implementation per supported platform; a single factory
/// (<see cref="Platform.Current"/>) resolves the right one at startup.
/// Call sites elsewhere never branch on the OS themselves — they call
/// methods on this interface.
///
/// When a new feature needs OS-specific behavior, add a method here and
/// implement it on each platform class. Don't reach for
/// <c>OperatingSystem.IsXxx()</c> in feature code.
/// </summary>
internal interface IPlatform
{
    // ---- Autostart / service-style lifecycle -----------------------

    /// <summary>Register autostart and start the bridge now.</summary>
    int Install(string exePath);

    /// <summary>Deregister autostart and stop the bridge now.</summary>
    int Uninstall();

    /// <summary>Start a running instance of the bridge.</summary>
    int Start();

    /// <summary>Stop the running instance of the bridge.</summary>
    int Stop();

    /// <summary>Print platform-specific install / running status lines.</summary>
    int Status();

    /// <summary>True iff autostart is currently registered for this user.</summary>
    bool IsRegistered();

    // ---- Serial port discovery -------------------------------------

    /// <summary>
    /// Filter the raw output of SerialPort.GetPortNames() down to the
    /// names that are plausibly USB-serial devices on this OS (i.e.,
    /// drop bluetooth-incoming, motherboard UARTs, classic /dev/tty
    /// blocking variants, etc.).
    /// </summary>
    IEnumerable<string> FilterSerialPorts(IEnumerable<string> raw);

    /// <summary>
    /// True iff <paramref name="name"/> matches the conventional naming
    /// pattern for a USB-serial port on this OS (regex / prefix match,
    /// not an existence check). Used by the first-run picker to decide
    /// whether the configured port is structurally valid for the
    /// current platform — `"COM4"` on macOS returns false even though
    /// the string is non-empty, which is exactly the trigger we want
    /// for the picker dialog. Empty string also returns false.
    /// </summary>
    bool PortNameLooksValid(string name);

    // ---- Per-user data directories ---------------------------------

    /// <summary>
    /// Where the bridge writes its log file. Follows the OS convention
    /// for application logs (LocalAppData on Windows, ~/Library/Logs on
    /// macOS, $XDG_STATE_HOME on Linux).
    /// </summary>
    string LogDir { get; }

    /// <summary>
    /// Where the bridge writes its single-instance lock file. May share
    /// a directory with logs on platforms whose conventions don't
    /// distinguish between "state" and "logs."
    /// </summary>
    string StateDir { get; }
}
