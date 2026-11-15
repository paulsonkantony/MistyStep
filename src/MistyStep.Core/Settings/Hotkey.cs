using System.Text;

namespace MistyStep.Core.Settings;

/// <summary>Modifier keys, with the same values as RegisterHotKey's MOD_* flags.</summary>
[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Alt = 0x1,
    Ctrl = 0x2,
    Shift = 0x4,
    Win = 0x8,
}

/// <summary>
/// A global hotkey such as <c>Ctrl+Alt+C</c>: modifiers plus one key, held as a Windows virtual-key code.
/// At least one of Ctrl, Alt or Win is required, so plain typing is never captured.
/// </summary>
public sealed record Hotkey(HotkeyModifiers Modifiers, int VirtualKey)
{
    public static readonly Hotkey Default = new(HotkeyModifiers.Ctrl | HotkeyModifiers.Alt, 'C');

    public static bool TryParse(string? text, out Hotkey? hotkey)
    {
        hotkey = null;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var modifiers = HotkeyModifiers.None;
        int? key = null;
        foreach (var raw in text.Split('+'))
        {
            var part = raw.Trim();
            if (part.Length == 0) return false;

            var modifier = part.ToUpperInvariant() switch
            {
                "CTRL" or "CONTROL" => HotkeyModifiers.Ctrl,
                "ALT" => HotkeyModifiers.Alt,
                "SHIFT" => HotkeyModifiers.Shift,
                "WIN" or "WINDOWS" => HotkeyModifiers.Win,
                _ => HotkeyModifiers.None,
            };

            if (modifier != HotkeyModifiers.None)
            {
                if (modifiers.HasFlag(modifier)) return false;
                modifiers |= modifier;
                continue;
            }

            if (key is not null || KeyCode(part) is not { } code) return false;
            key = code;
        }

        if (key is null || (modifiers & (HotkeyModifiers.Ctrl | HotkeyModifiers.Alt | HotkeyModifiers.Win)) == 0) return false;
        hotkey = new Hotkey(modifiers, key.Value);
        return true;
    }

    /// <summary>The canonical text, e.g. <c>Ctrl+Alt+C</c>, as stored in settings and shown in the menu.</summary>
    public override string ToString()
    {
        var text = new StringBuilder();
        if (Modifiers.HasFlag(HotkeyModifiers.Ctrl)) text.Append("Ctrl+");
        if (Modifiers.HasFlag(HotkeyModifiers.Alt)) text.Append("Alt+");
        if (Modifiers.HasFlag(HotkeyModifiers.Shift)) text.Append("Shift+");
        if (Modifiers.HasFlag(HotkeyModifiers.Win)) text.Append("Win+");
        text.Append(KeyName(VirtualKey));
        return text.ToString();
    }

    /// <summary>Virtual-key code for a key name: A-Z, 0-9, F1-F24.</summary>
    public static int? KeyCode(string name)
    {
        var upper = name.Trim().ToUpperInvariant();
        if (upper.Length == 1 && upper[0] is >= 'A' and <= 'Z' or >= '0' and <= '9') return upper[0];
        if (upper.Length is 2 or 3 && upper[0] == 'F' && int.TryParse(upper.AsSpan(1), out var n) && n is >= 1 and <= 24)
            return 0x70 + n - 1;
        return null;
    }

    /// <summary>Key name for a virtual-key code accepted by <see cref="KeyCode"/>, else null.</summary>
    public static string? KeyNameOrNull(int virtualKey) => virtualKey switch
    {
        >= 'A' and <= 'Z' or >= '0' and <= '9' => ((char)virtualKey).ToString(),
        >= 0x70 and <= 0x87 => "F" + (virtualKey - 0x70 + 1),
        _ => null,
    };

    private static string KeyName(int virtualKey) => KeyNameOrNull(virtualKey) ?? "?";
}
