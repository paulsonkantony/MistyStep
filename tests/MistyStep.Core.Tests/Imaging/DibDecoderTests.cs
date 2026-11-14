using System.Buffers.Binary;
using MistyStep.Core.Imaging;

namespace MistyStep.Core.Tests.Imaging;

public class DibDecoderTests
{
    /// <summary>Builds a DIB: header (40 or 124 bytes), optional masks after a 40-byte header, then pixel rows.</summary>
    internal static byte[] Dib(int width, int height, ushort bitCount, byte[] pixels, uint compression = 0,
        int headerSize = 40, bool masksAfterHeader = false, uint[]? masks = null, int paletteEntries = 0)
    {
        var maskBytes = masksAfterHeader ? 12 : 0;
        var bytes = new byte[headerSize + maskBytes + paletteEntries * 4 + pixels.Length];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, headerSize);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), width);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), height);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(12), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(14), bitCount);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), compression);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(32), (uint)paletteEntries);
        if (masks is not null)
        {
            for (var i = 0; i < masks.Length; i++) BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(40 + i * 4), masks[i]);
        }

        pixels.CopyTo(bytes, headerSize + maskBytes + paletteEntries * 4);
        return bytes;
    }

    /// <summary>2x2 BGRA pixels, rows in the order given.</summary>
    private static byte[] TwoByTwo(byte alpha) =>
    [
        1, 2, 3, alpha,   4, 5, 6, alpha,      // first stored row
        7, 8, 9, alpha,   10, 11, 12, alpha,   // second stored row
    ];

    [Fact]
    public void Bottom_up_32bit_rows_are_flipped_and_zero_alpha_forced_opaque()
    {
        var result = Assert.IsType<DibDecodeResult.Pixels>(DibDecoder.Decode(Dib(2, 2, 32, TwoByTwo(0))));

        Assert.Equal(2, result.Width);
        Assert.Equal(2, result.Height);
        Assert.False(result.HasAlpha);
        // The last stored row comes first; every alpha is now 255.
        Assert.Equal(new byte[] { 7, 8, 9, 255, 10, 11, 12, 255, 1, 2, 3, 255, 4, 5, 6, 255 }, result.Bgra);
    }

    [Fact]
    public void Top_down_32bit_keeps_row_order()
    {
        var result = Assert.IsType<DibDecodeResult.Pixels>(DibDecoder.Decode(Dib(2, -2, 32, TwoByTwo(0))));
        Assert.Equal(new byte[] { 1, 2, 3, 255, 4, 5, 6, 255, 7, 8, 9, 255, 10, 11, 12, 255 }, result.Bgra);
    }

    [Fact]
    public void Real_alpha_is_kept_when_any_pixel_has_it()
    {
        var pixels = TwoByTwo(0);
        pixels[7] = 128; // one semi-transparent pixel
        var result = Assert.IsType<DibDecodeResult.Pixels>(DibDecoder.Decode(Dib(2, -2, 32, pixels)));
        Assert.True(result.HasAlpha);
        Assert.Equal(0, result.Bgra[3]);
        Assert.Equal(128, result.Bgra[7]);
    }

    [Fact]
    public void Bitfields_with_masks_after_a_40_byte_header()
    {
        var dib = Dib(2, -2, 32, TwoByTwo(255), compression: 3, masksAfterHeader: true,
            masks: [0x00FF0000, 0x0000FF00, 0x000000FF]);
        var result = Assert.IsType<DibDecodeResult.Pixels>(DibDecoder.Decode(dib));
        Assert.Equal(1, result.Bgra[0]);
        Assert.True(result.HasAlpha);
    }

    [Fact]
    public void V5_header_with_bitfields_and_alpha_mask()
    {
        var dib = Dib(2, 2, 32, TwoByTwo(200), compression: 3, headerSize: 124,
            masks: [0x00FF0000, 0x0000FF00, 0x000000FF, 0xFF000000]);
        var result = Assert.IsType<DibDecodeResult.Pixels>(DibDecoder.Decode(dib));
        Assert.True(result.HasAlpha);
        Assert.Equal(7, result.Bgra[0]);
    }

    [Fact]
    public void Non_standard_32bit_masks_fall_back_to_a_bmp_file()
    {
        var dib = Dib(2, 2, 32, TwoByTwo(0), compression: 3, masksAfterHeader: true,
            masks: [0x000000FF, 0x0000FF00, 0x00FF0000]);
        Assert.IsType<DibDecodeResult.BmpFile>(DibDecoder.Decode(dib));
    }

    [Fact]
    public void Twenty_four_bit_is_wrapped_as_a_bmp_file_with_correct_offsets()
    {
        // 2x1 at 24 bpp: 6 bytes of pixels padded to 8.
        var dib = Dib(2, 1, 24, [1, 2, 3, 4, 5, 6, 0, 0]);
        var bmp = Assert.IsType<DibDecodeResult.BmpFile>(DibDecoder.Decode(dib));

        Assert.Equal((byte)'B', bmp.Bytes[0]);
        Assert.Equal((byte)'M', bmp.Bytes[1]);
        Assert.Equal(14 + dib.Length, BinaryPrimitives.ReadInt32LittleEndian(bmp.Bytes.AsSpan(2)));
        Assert.Equal(14 + 40, BinaryPrimitives.ReadInt32LittleEndian(bmp.Bytes.AsSpan(10)));
        Assert.Equal(dib, bmp.Bytes[14..]);
    }

    [Fact]
    public void Eight_bit_palette_is_counted_in_the_pixel_offset()
    {
        var dib = Dib(4, 1, 8, [0, 1, 2, 3], paletteEntries: 0);
        // colorsUsed 0 at 8 bpp means a full 256-entry palette; this DIB is too short for that.
        Assert.IsType<DibDecodeResult.Invalid>(DibDecoder.Decode(dib));

        var withPalette = Dib(4, 1, 8, [0, 1, 2, 3], paletteEntries: 4);
        var bmp = Assert.IsType<DibDecodeResult.BmpFile>(DibDecoder.Decode(withPalette));
        Assert.Equal(14 + 40 + 16, BinaryPrimitives.ReadInt32LittleEndian(bmp.Bytes.AsSpan(10)));
    }

    [Fact]
    public void Truncated_pixel_data_is_invalid() =>
        Assert.IsType<DibDecodeResult.Invalid>(DibDecoder.Decode(Dib(2, 2, 32, new byte[15])));

    [Theory]
    [InlineData(0, 2)]
    [InlineData(-2, 2)]
    [InlineData(2, 0)]
    public void Bad_dimensions_are_invalid(int width, int height) =>
        Assert.IsType<DibDecodeResult.Invalid>(DibDecoder.Decode(Dib(width, height, 32, new byte[64])));

    [Fact]
    public void Too_short_for_a_header_is_invalid() =>
        Assert.IsType<DibDecodeResult.Invalid>(DibDecoder.Decode(new byte[39]));

    [Fact]
    public void Unsupported_compression_is_invalid() =>
        Assert.IsType<DibDecodeResult.Invalid>(DibDecoder.Decode(Dib(2, 2, 32, new byte[16], compression: 1)));

    [Fact]
    public void Huge_dimensions_are_refused_before_allocating() =>
        Assert.IsType<DibDecodeResult.TooLarge>(DibDecoder.Decode(Dib(20_000, 20_000, 32, new byte[16])));
}
