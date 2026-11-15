using System.Runtime.InteropServices;

namespace MistyStep.Windows.Shell;

/// <summary>Asks Windows to restart the app after a crash or hang (RegisterApplicationRestart).</summary>
public static partial class RestartManager
{
    private const int RESTART_NO_PATCH = 4;
    private const int RESTART_NO_REBOOT = 8;

    /// <summary>Registers with the given command line (without the program name). Windows only restarts processes that ran at least 60 s.</summary>
    public static bool Register(string arguments) =>
        RegisterApplicationRestart(arguments, RESTART_NO_PATCH | RESTART_NO_REBOOT) == 0;

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int RegisterApplicationRestart(string? pwzCommandline, int dwFlags);
}
