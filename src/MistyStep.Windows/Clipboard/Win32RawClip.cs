using System.Diagnostics;
using System.Runtime.InteropServices;
using MistyStep.Core.Capture;
using static MistyStep.Windows.Clipboard.NativeMethods;

namespace MistyStep.Windows.Clipboard;

/// <summary>
/// A <see cref="RawClip"/> over the open Win32 clipboard. Formats are listed up front; data is read
/// only when the decider asks, and every value is copied out of clipboard memory. Valid only until
/// <see cref="Invalidate"/> is called, just before the clipboard is closed.
/// </summary>
internal sealed class Win32RawClip : RawClip
{
    private readonly Dictionary<string, uint> _formatIds;
    private bool _valid = true;
    private string? _owner;
    private bool _ownerResolved;

    private Win32RawClip(Dictionary<string, uint> formatIds)
    {
        _formatIds = formatIds;
        Formats = formatIds.Keys.ToHashSet(StringComparer.Ordinal);
    }

    public override IReadOnlySet<string> Formats { get; }

    /// <summary>Lists the formats. Call only while the clipboard is open.</summary>
    public static Win32RawClip ListFormats()
    {
        var ids = new Dictionary<string, uint>(StringComparer.Ordinal);
        uint format = 0;
        while ((format = EnumClipboardFormats(format)) != 0)
        {
            ids.TryAdd(FormatName(format), format);
        }

        return new Win32RawClip(ids);
    }

    public void Invalidate() => _valid = false;

    public override string? OwnerProcessName
    {
        get
        {
            EnsureValid();
            if (_ownerResolved) return _owner;
            _ownerResolved = true;

            var hwnd = GetClipboardOwner();
            if (hwnd == 0) return null;
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == 0) return null;
            try
            {
                using var process = Process.GetProcessById((int)pid);
                _owner = process.ProcessName + ".exe";
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                _owner = null; // the owner exited
            }

            return _owner;
        }
    }

    public override uint? GetDword(string format)
    {
        var bytes = GetBytes(format);
        return bytes is { Length: >= 4 } ? BitConverter.ToUInt32(bytes, 0) : null;
    }

    public override unsafe string? GetUnicodeText()
    {
        EnsureValid();
        return WithLockedData(CF_UNICODETEXT, (pointer, size) =>
        {
            var chars = new ReadOnlySpan<char>((void*)pointer, (int)(size / sizeof(char)));
            var end = chars.IndexOf('\0'); // GlobalSize may exceed the string; stop at the terminator
            return new string(end >= 0 ? chars[..end] : chars);
        });
    }

    public override byte[]? GetBytes(string format)
    {
        EnsureValid();
        if (!_formatIds.TryGetValue(format, out var id)) return null;
        return WithLockedData(id, (pointer, size) =>
        {
            var bytes = new byte[size];
            Marshal.Copy(pointer, bytes, 0, bytes.Length);
            return bytes;
        });
    }

    public override unsafe IReadOnlyList<string>? GetFileDrop()
    {
        EnsureValid();
        var handle = GetClipboardData(CF_HDROP);
        if (handle == 0) return null;

        var count = DragQueryFile(handle, uint.MaxValue, null, 0);
        var files = new List<string>((int)Math.Min(count, 1024));
        for (uint i = 0; i < count && i < 1024; i++)
        {
            var length = DragQueryFile(handle, i, null, 0);
            if (length == 0) continue;
            var buffer = new char[length + 1];
            fixed (char* p = buffer)
            {
                var copied = DragQueryFile(handle, i, p, (uint)buffer.Length);
                files.Add(new string(buffer, 0, (int)copied));
            }
        }

        return files;
    }

    public override long? GetFileLength(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? info.Length : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static T? WithLockedData<T>(uint format, Func<nint, nuint, T> read) where T : class
    {
        var handle = GetClipboardData(format);
        if (handle == 0) return null;
        var pointer = GlobalLock(handle);
        if (pointer == 0) return null;
        try
        {
            var size = GlobalSize(handle);
            if (size == 0 || size > int.MaxValue) return null;
            return read(pointer, size);
        }
        finally
        {
            GlobalUnlock(handle);
        }
    }

    private void EnsureValid() => ObjectDisposedException.ThrowIf(!_valid, this);

    internal static unsafe string FormatName(uint format)
    {
        switch (format)
        {
            case CF_TEXT: return "CF_TEXT";
            case CF_BITMAP: return "CF_BITMAP";
            case CF_METAFILEPICT: return "CF_METAFILEPICT";
            case CF_OEMTEXT: return "CF_OEMTEXT";
            case CF_DIB: return ClipboardFormats.Dib;
            case CF_UNICODETEXT: return ClipboardFormats.UnicodeText;
            case CF_ENHMETAFILE: return "CF_ENHMETAFILE";
            case CF_HDROP: return ClipboardFormats.HDrop;
            case CF_LOCALE: return "CF_LOCALE";
            case CF_DIBV5: return ClipboardFormats.DibV5;
        }

        if (format >= 0xC000)
        {
            Span<char> buffer = stackalloc char[256];
            fixed (char* p = buffer)
            {
                var length = GetClipboardFormatName(format, p, buffer.Length);
                if (length > 0) return new string(buffer[..length]);
            }
        }

        return "CF_" + format.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}
