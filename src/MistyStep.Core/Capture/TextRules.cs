using System.Buffers.Binary;

namespace MistyStep.Core.Capture;

public static class TextNormalizer
{
    /// <summary>Drops trailing NULs and converts CRLF to LF.</summary>
    public static string Normalize(string raw) => raw.TrimEnd('\0').Replace("\r\n", "\n", StringComparison.Ordinal);

    public static bool IsBlank(string text) => string.IsNullOrWhiteSpace(text);
}

public static class UrlDetector
{
    /// <summary>True when the text, trimmed, is exactly one absolute http or https URL.</summary>
    public static bool IsUrl(string? text)
    {
        if (text is null) return false;
        var trimmed = text.Trim();
        if (trimmed.Length == 0) return false;
        foreach (var c in trimmed)
        {
            if (char.IsWhiteSpace(c)) return false;
        }

        return Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            && uri.Host.Length > 0;
    }
}

public static class PngInfo
{
    private static ReadOnlySpan<byte> Signature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>Validates the PNG signature and reads width and height from the IHDR chunk.</summary>
    public static bool TryRead(ReadOnlySpan<byte> data, out int width, out int height)
    {
        width = 0;
        height = 0;
        // signature(8) + length(4) + "IHDR"(4) + width(4) + height(4)
        if (data.Length < 24 || !data[..8].SequenceEqual(Signature)) return false;
        if (!data.Slice(12, 4).SequenceEqual("IHDR"u8)) return false;

        var w = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(16, 4));
        var h = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(20, 4));
        if (w == 0 || h == 0 || w > int.MaxValue || h > int.MaxValue) return false;

        width = (int)w;
        height = (int)h;
        return true;
    }
}
