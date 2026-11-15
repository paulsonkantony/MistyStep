using MistyStep.Core.Settings;
using MistyStep.Windows.Shell;

namespace MistyStep.Windows.Tests;

public class ShellTests
{
    [Fact]
    public void Hotkey_registers_and_a_second_owner_is_refused()
    {
        // An unusual combination, so the test does not fight a real app's hotkey.
        var hotkey = new Hotkey(HotkeyModifiers.Ctrl | HotkeyModifiers.Alt | HotkeyModifiers.Shift, 0x87); // F24
        var registered = false;
        var thread = new Thread(() =>
        {
            using var first = new GlobalHotkey();
            using var second = new GlobalHotkey();
            registered = first.TryRegister(hotkey) && first.Current == hotkey && !second.TryRegister(hotkey) && second.Current is null;
            first.Unregister();
            registered &= first.Current is null && second.TryRegister(hotkey);
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Assert.True(registered);
    }

    [Fact]
    public void Autostart_command_quotes_the_path_and_starts_minimized()
    {
        Assert.Equal("\"C:\\Program Files\\Misty Step\\MistyStep.exe\" --minimized", Autostart.Command(@"C:\Program Files\Misty Step\MistyStep.exe", portable: false));
        Assert.EndsWith("--minimized --portable", Autostart.Command(@"D:\MistyStep.exe", portable: true), StringComparison.Ordinal);
    }
}
