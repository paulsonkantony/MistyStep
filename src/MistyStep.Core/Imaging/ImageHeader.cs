using System.Buffers.Binary;
using MistyStep.Core.Capture;

namespace MistyStep.Core.Imaging;

/// <summary>
/// Reads width and height from image headers without decoding pixels. Used for image files served
/// unchanged, so <c>/v1/meta</c> can report a size. Also confirms the bytes match the claimed type.
/// </summary>
public static class ImageHeader
{
    public static bool TryReadSize(ReadOnlySpan<byte> data, string mime, out int width, out int height)
    {
        width = 0;
        height = 0;
        return mime switch
        {
            "image/png" => PngInfo.TryRead(data, out width, out height),
            "image/gif" => TryReadGif(data, out width, out height),
            "image/jpeg" => TryReadJpeg(data, out width, out height),
            "image/webp" => TryReadWebP(data, out width, out height),
            _ => false,
        };
    }

    private static bool TryReadGif(ReadOnlySpan<byte> data, out int width, out int height)
    {
        width = height = 0;
        if (data.Length < 10 || !(data[..6].SequenceEqual("GIF87a"u8) || data[..6].SequenceEqual("GIF89a"u8))) return false;
        width = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(6, 2));
        height = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(8, 2));
        return width > 0 && height > 0;
    }

    private static bool TryReadJpeg(ReadOnlySpan<byte> data, out int width, out int height)
    {
        width = height = 0;
        if (data.Length < 4 || data[0] != 0xFF || data[1] != 0xD8) return false;

        var i = 2;
        while (i + 4 <= data.Length)
        {
            if (data[i] != 0xFF) return false;
            var marker = data[i + 1];
            if (marker == 0xFF) { i++; continue; }                              // fill byte
            if (marker is 0x01 or (>= 0xD0 and <= 0xD7)) { i += 2; continue; } // standalone markers
            if (marker is 0xD9 or 0xDA) return false;                           // end of image / start of scan before a frame header

            var length = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(i + 2, 2));
            if (length < 2) return false;

            var isFrameHeader = marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC;
            if (isFrameHeader)
            {
                if (i + 9 > data.Length) return false;
                height = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(i + 5, 2));
                width = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(i + 7, 2));
                return width > 0 && height > 0;
            }

            i += 2 + length;
        }

        return false;
    }

    private static bool TryReadWebP(ReadOnlySpan<byte> data, out int width, out int height)
    {
        width = height = 0;
        if (data.Length < 30 || !data[..4].SequenceEqual("RIFF"u8) || !data.Slice(8, 4).SequenceEqual("WEBP"u8)) return false;

        var chunk = data.Slice(12, 4);
        if (chunk.SequenceEqual("VP8 "u8))
        {
            // Lossy: frame tag (3 bytes) then start code 9D 01 2A, then 14-bit width and height.
            if (data[23] != 0x9D || data[24] != 0x01 || data[25] != 0x2A) return false;
            width = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(26, 2)) & 0x3FFF;
            height = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(28, 2)) & 0x3FFF;
        }
        else if (chunk.SequenceEqual("VP8L"u8))
        {
            // Lossless: signature 0x2F, then 14 bits (width - 1) and 14 bits (height - 1).
            if (data[20] != 0x2F) return false;
            var bits = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(21, 4));
            width = (int)(bits & 0x3FFF) + 1;
            height = (int)((bits >> 14) & 0x3FFF) + 1;
        }
        else if (chunk.SequenceEqual("VP8X"u8))
        {
            // Extended: 24-bit (canvas width - 1) and (canvas height - 1) at offsets 24 and 27.
            width = (data[24] | (data[25] << 8) | (data[26] << 16)) + 1;
            height = (data[27] | (data[28] << 8) | (data[29] << 16)) + 1;
        }
        else
        {
            return false;
        }

        return width > 0 && height > 0;
    }
}
