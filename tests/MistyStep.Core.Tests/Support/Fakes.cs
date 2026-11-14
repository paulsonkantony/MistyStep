using System.Buffers.Binary;
using MistyStep.Core.Capture;
using MistyStep.Core.Time;

namespace MistyStep.Core.Tests.Support;

public sealed class FakeClock : IClock
{
    public long MonotonicMs { get; set; } = 1_000_000;

    // Midday UTC so the local date is Nov 14 in nearly every time zone (device names use local date).
    public DateTimeOffset UtcNow { get; set; } = new(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);

    public void Advance(long ms)
    {
        MonotonicMs += ms;
        UtcNow = UtcNow.AddMilliseconds(ms);
    }
}

/// <summary>An in-memory clipboard. Records which formats' data were actually read.</summary>
public sealed class FakeRawClip : RawClip
{
    private readonly Dictionary<string, object> _data = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _fileLengths = new(StringComparer.OrdinalIgnoreCase);
    private string? _owner;

    public List<string> Reads { get; } = [];

    public override IReadOnlySet<string> Formats => _data.Keys.ToHashSet(StringComparer.Ordinal);

    public override string? OwnerProcessName => _owner;

    public FakeRawClip Owner(string? process)
    {
        _owner = process;
        return this;
    }

    public FakeRawClip Text(string text) => With(ClipboardFormats.UnicodeText, text);

    public FakeRawClip Bytes(string format, byte[] bytes) => With(format, bytes);

    public FakeRawClip Dword(string format, uint value) => With(format, value);

    /// <summary>A format that is present but whose data cannot be read.</summary>
    public FakeRawClip Unreadable(string format) => With(format, new object());

    /// <summary>A format with no meaningful data (e.g. RTF, HTML, a bare marker).</summary>
    public FakeRawClip Marker(string format) => With(format, Array.Empty<byte>());

    public FakeRawClip Files(params (string Path, long Length)[] files)
    {
        foreach (var (path, length) in files) _fileLengths[path] = length;
        return With(ClipboardFormats.HDrop, files.Select(f => f.Path).ToArray());
    }

    public override uint? GetDword(string format)
    {
        Reads.Add(format);
        return _data.TryGetValue(format, out var value) && value is uint dword ? dword : null;
    }

    public override string? GetUnicodeText()
    {
        Reads.Add(ClipboardFormats.UnicodeText);
        return _data.TryGetValue(ClipboardFormats.UnicodeText, out var value) ? value as string : null;
    }

    public override byte[]? GetBytes(string format)
    {
        Reads.Add(format);
        return _data.TryGetValue(format, out var value) && value is byte[] bytes ? (byte[])bytes.Clone() : null;
    }

    public override IReadOnlyList<string>? GetFileDrop()
    {
        Reads.Add(ClipboardFormats.HDrop);
        return _data.TryGetValue(ClipboardFormats.HDrop, out var value) ? value as string[] : null;
    }

    public override long? GetFileLength(string path) => _fileLengths.TryGetValue(path, out var length) ? length : null;

    private FakeRawClip With(string format, object value)
    {
        _data[format] = value;
        return this;
    }
}

public static class TestImages
{
    /// <summary>A PNG signature and IHDR header for the given size, followed by filler bytes.</summary>
    public static byte[] PngHeader(uint width, uint height, int filler = 16)
    {
        var bytes = new byte[24 + filler];
        byte[] signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        signature.CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8), 13);
        "IHDR"u8.CopyTo(bytes.AsSpan(12));
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(16), width);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(20), height);
        return bytes;
    }

    public static byte[] FakeDib(int length = 64) => Enumerable.Repeat((byte)0x7F, length).ToArray();
}
