using Microsoft.Win32;

namespace MistyStep.Windows.Shell;

/// <summary>
/// "Start with Windows": the <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c> value is the single
/// source of truth (not stored in config.json). The command starts the app with <c>--minimized</c>.
/// </summary>
public static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "MistyStep";

    public static string Command(string exePath, bool portable) =>
        $"\"{exePath}\" --minimized" + (portable ? " --portable" : "");

    /// <summary>True when the Run value exists and points at this executable.</summary>
    public static bool IsEnabled(string exePath)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string command
                   && command.Contains(exePath, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    /// <summary>Writes or removes the Run value. False if the registry could not be changed.</summary>
    public static bool Set(bool enabled, string exePath, bool portable)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (enabled) key.SetValue(ValueName, Command(exePath, portable), RegistryValueKind.String);
            else key.DeleteValue(ValueName, throwOnMissingValue: false);
            return true;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }
}
