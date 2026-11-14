using System.Buffers.Binary;

namespace MistyStep.Core.Imaging;

/// <summary>
/// Decodes a clipboard DIB (CF_DIB or CF_DIBV5: a BITMAPINFOHEADER-family header, optional masks and
/// palette, then pixels). 32-bit DIBs are decoded here so the alpha rule can be applied: alpha counts
/// only if at least one pixel has non-zero alpha, otherwise the image is forced opaque (many apps leave
/// alpha at zero). Other bit depths are wrapped as a BMP file for the platform decoder.
/// </summary>
public static class DibDecoder
{
    /// <summary>Refuse absurd sizes before allocating: 64 megapixels is a 256 MB BGRA buffer.</summary>
    public const long MaxPixels = 64_000_000;

    private const uint BiRgb = 0;
    private const uint BiBitfields = 3;
    private const int InfoHeaderSize = 40;
    private const int BmpFileHeaderSize = 14;

    public static DibDecodeResult Decode(ReadOnlySpan<byte> dib)
    {
        if (dib.Length < InfoHeaderSize) return DibDecodeResult.Invalid.Instance;

        var headerSize = BinaryPrimitives.ReadInt32LittleEndian(dib);
        var width = BinaryPrimitives.ReadInt32LittleEndian(dib[4..]);
        var rawHeight = BinaryPrimitives.ReadInt32LittleEndian(dib[8..]);
        var bitCount = BinaryPrimitives.ReadUInt16LittleEndian(dib[14..]);
        var compression = BinaryPrimitives.ReadUInt32LittleEndian(dib[16..]);
        var colorsUsed = BinaryPrimitives.ReadUInt32LittleEndian(dib[32..]);

        if (headerSize < InfoHeaderSize || headerSize > dib.Length) return DibDecodeResult.Invalid.Instance;
        if (width <= 0 || rawHeight == 0 || rawHeight == int.MinValue) return DibDecodeResult.Invalid.Instance;

        var topDown = rawHeight < 0;
        var height = Math.Abs(rawHeight);
        if ((long)width * height > MaxPixels) return DibDecodeResult.TooLarge.Instance;
        if (bitCount is not (1 or 4 or 8 or 16 or 24 or 32)) return DibDecodeResult.Invalid.Instance;
        if (compression is not (BiRgb or BiBitfields)) return DibDecodeResult.Invalid.Instance;

        // Masks sit after a plain 40-byte header when compression is BI_BITFIELDS; larger headers include them.
        var masksAfterHeader = compression == BiBitfields && headerSize == InfoHeaderSize ? 12 : 0;
        var paletteEntries = colorsUsed != 0 ? colorsUsed : bitCount <= 8 ? 1u << bitCount : 0u;
        if (paletteEntries > 256) return DibDecodeResult.Invalid.Instance;
        var pixelOffset = headerSize + masksAfterHeader + (int)paletteEntries * 4;

        var stride = ((long)width * bitCount + 31) / 32 * 4;
        if (pixelOffset + stride * height > dib.Length) return DibDecodeResult.Invalid.Instance;

        if (bitCount == 32 && HasStandardMasks(dib, headerSize, compression))
            return Decode32(dib[pixelOffset..], width, height, topDown);

        return new DibDecodeResult.BmpFile(WrapAsBmpFile(dib, pixelOffset), width, height);
    }

    private static bool HasStandardMasks(ReadOnlySpan<byte> dib, int headerSize, uint compression)
    {
        if (compression == BiRgb) return true;
        // Red, green, blue masks at offset 40 (in the header for V4/V5, right after it for a 40-byte header).
        if (dib.Length < 52) return false;
        var red = BinaryPrimitives.ReadUInt32LittleEndian(dib[40..]);
        var green = BinaryPrimitives.ReadUInt32LittleEndian(dib[44..]);
        var blue = BinaryPrimitives.ReadUInt32LittleEndian(dib[48..]);
        if (red != 0x00FF0000 || green != 0x0000FF00 || blue != 0x000000FF) return false;
        if (headerSize >= 56)
        {
            var alpha = BinaryPrimitives.ReadUInt32LittleEndian(dib[52..]);
            if (alpha is not (0 or 0xFF000000)) return false;
        }

        return true;
    }

    private static DibDecodeResult Decode32(ReadOnlySpan<byte> pixels, int width, int height, bool topDown)
    {
        var rowBytes = width * 4;
        var bgra = new byte[(long)rowBytes * height];
        var anyAlpha = false;

        for (var y = 0; y < height; y++)
        {
            var sourceRow = topDown ? y : height - 1 - y;
            var source = pixels.Slice(sourceRow * rowBytes, rowBytes);
            source.CopyTo(bgra.AsSpan(y * rowBytes, rowBytes));
            if (!anyAlpha)
            {
                for (var x = 3; x < rowBytes; x += 4)
                {
                    if (source[x] != 0) { anyAlpha = true; break; }
                }
            }
        }

        if (!anyAlpha)
        {
            for (var i = 3; i < bgra.Length; i += 4) bgra[i] = 0xFF;
        }

        return new DibDecodeResult.Pixels(width, height, bgra, anyAlpha);
    }

    /// <summary>Prepends a BITMAPFILEHEADER so a standard BMP decoder can read the DIB.</summary>
    internal static byte[] WrapAsBmpFile(ReadOnlySpan<byte> dib, int pixelOffset)
    {
        var file = new byte[BmpFileHeaderSize + dib.Length];
        file[0] = (byte)'B';
        file[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(2), file.Length);
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(10), BmpFileHeaderSize + pixelOffset);
        dib.CopyTo(file.AsSpan(BmpFileHeaderSize));
        return file;
    }
}

public abstract record DibDecodeResult
{
    private DibDecodeResult() { }

    /// <summary>Top-down BGRA pixels (4 bytes each). <see cref="HasAlpha"/> false means alpha was forced opaque.</summary>
    public sealed record Pixels(int Width, int Height, byte[] Bgra, bool HasAlpha) : DibDecodeResult;

    /// <summary>A BMP file for the platform decoder (bit depths other than plain 32-bit).</summary>
    public sealed record BmpFile(byte[] Bytes, int Width, int Height) : DibDecodeResult;

    public sealed record TooLarge : DibDecodeResult
    {
        public static readonly TooLarge Instance = new();
    }

    public sealed record Invalid : DibDecodeResult
    {
        public static readonly Invalid Instance = new();
    }
}
