namespace MistyStep.Core.Capture;

/// <summary>Clipboard format names as seen by Core. Standard formats use their CF_ constant name.</summary>
public static class ClipboardFormats
{
    public const string UnicodeText = "CF_UNICODETEXT";
    public const string Dib = "CF_DIB";
    public const string DibV5 = "CF_DIBV5";
    public const string HDrop = "CF_HDROP";
    public const string Png = "PNG";

    // Secrecy markers set by password managers and similar apps.
    public const string ExcludeFromMonitor = "ExcludeClipboardContentFromMonitorProcessing";
    public const string ViewerIgnore = "Clipboard Viewer Ignore";
    public const string CanIncludeInHistory = "CanIncludeInClipboardHistory";
    public const string CanUploadToCloud = "CanUploadToCloudClipboard";
}

/// <summary>
/// One clipboard read. <see cref="Formats"/> is listed up front; the data members read lazily so
/// the decider only pulls the format it picks. An instance is valid only while the clipboard is open,
/// i.e. during the <see cref="IClipboardSource.Captured"/> callback. Every returned array is a copy.
/// </summary>
public abstract class RawClip
{
    /// <summary>Names of every format on the clipboard (see <see cref="ClipboardFormats"/>).</summary>
    public abstract IReadOnlySet<string> Formats { get; }

    /// <summary>Executable file name of the clipboard owner's process (e.g. "KeePass.exe"), or null if unknown.</summary>
    public abstract string? OwnerProcessName { get; }

    /// <summary>A format's data read as a 32-bit value, or null if absent or unreadable.</summary>
    public abstract uint? GetDword(string format);

    /// <summary>CF_UNICODETEXT as a string, or null if absent.</summary>
    public abstract string? GetUnicodeText();

    /// <summary>A copy of a format's bytes, or null if absent.</summary>
    public abstract byte[]? GetBytes(string format);

    /// <summary>The CF_HDROP file list, or null if absent.</summary>
    public abstract IReadOnlyList<string>? GetFileDrop();

    /// <summary>Length of a dropped file in bytes, or null if it cannot be read.</summary>
    public abstract long? GetFileLength(string path);
}

/// <summary>Why a clipboard read happened.</summary>
public enum ReadTrigger
{
    /// <summary>A debounced clipboard-change notification.</summary>
    ClipboardChange,

    /// <summary>The 30-second watchdog saw the sequence number move without a notification.</summary>
    Watchdog,

    /// <summary>The user asked for it: the global hotkey or "Share current clipboard now".</summary>
    Hotkey,
}

/// <summary>
/// Raises <see cref="Captured"/> once per read, with the clipboard open. The <see cref="RawClip"/> is
/// only valid during the callback.
/// </summary>
public interface IClipboardSource
{
    event Action<RawClip, ReadTrigger> Captured;
}
