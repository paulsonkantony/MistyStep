using System.Runtime.InteropServices;

namespace MistyStep.Windows.Shell;

/// <summary>
/// Lets another process take the foreground. A second launch calls this before signalling the running copy,
/// so that copy's tray menu can become the foreground window and closes when the user clicks elsewhere.
/// Only the process that currently owns the foreground (the one the user just started) can grant this.
/// </summary>
public static partial class ForegroundPermission
{
    private const uint ASFW_ANY = unchecked((uint)-1);

    /// <summary>Allows the process with <paramref name="processId"/>, or any process when null, to set the foreground window.</summary>
    public static bool Allow(int? processId = null) => AllowSetForegroundWindow(processId is { } id ? (uint)id : ASFW_ANY);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AllowSetForegroundWindow(uint dwProcessId);
}
