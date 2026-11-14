using System.Buffers.Binary;
using MistyStep.Core.Imaging;
using MistyStep.Core.Tests.Support;

namespace MistyStep.Core.Tests.Imaging;

public class ImageHeaderTests
{
    [Fact]
    public void Png()
    {
        Assert.True(ImageHeader.TryReadSize(TestImages.PngHeader(640, 480), "image/png", out var w, out var h));
        Assert.Equal((640, 480), (w, h));
    }

    [Theory]
    [InlineData("GIF87a")]
    [InlineData("GIF89a")]
    public void Gif(string signature)
    {
        var bytes = new byte[16];
        System.Text.Encoding.ASCII.GetBytes(signature).CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(6), 300);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(8), 200);
        Assert.True(ImageHeader.TryReadSize(bytes, "image/gif", out var w, out var h));
        Assert.Equal((300, 200), (w, h));
    }

    [Theory]
    [InlineData(0xC0)] // baseline
    [InlineData(0xC2)] // progressive
    public void Jpeg_finds_the_frame_header_after_other_segments(byte sof)
    {
        byte[] bytes =
        [
            0xFF, 0xD8,                                     // SOI
            0xFF, 0xE0, 0x00, 0x06, (byte)'J', (byte)'F', (byte)'I', (byte)'F', // APP0, length 6
            0xFF, 0xC4, 0x00, 0x04, 0x00, 0x00,             // DHT (not a frame header)
            0xFF, 0xFF,                                     // fill byte
            0xFF, sof, 0x00, 0x11, 0x08, 0x04, 0x38, 0x07, 0x80, 0x03, // SOFn: precision 8, h=1080, w=1920
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        ];
        Assert.True(ImageHeader.TryReadSize(bytes, "image/jpeg", out var w, out var h));
        Assert.Equal((1920, 1080), (w, h));
    }

    [Fact]
    public void Jpeg_without_frame_header_is_rejected()
    {
        byte[] bytes = [0xFF, 0xD8, 0xFF, 0xD9, 0x00, 0x00];
        Assert.False(ImageHeader.TryReadSize(bytes, "image/jpeg", out _, out _));
    }

    [Fact]
    public void WebP_lossy()
    {
        var bytes = WebP("VP8 ");
        bytes[23] = 0x9D; bytes[24] = 0x01; bytes[25] = 0x2A;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(26), 800);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(28), 600);
        Assert.True(ImageHeader.TryReadSize(bytes, "image/webp", out var w, out var h));
        Assert.Equal((800, 600), (w, h));
    }

    [Fact]
    public void WebP_lossless()
    {
        var bytes = WebP("VP8L");
        bytes[20] = 0x2F;
        uint bits = (800 - 1) | ((600u - 1) << 14);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(21), bits);
        Assert.True(ImageHeader.TryReadSize(bytes, "image/webp", out var w, out var h));
        Assert.Equal((800, 600), (w, h));
    }

    [Fact]
    public void WebP_extended()
    {
        var bytes = WebP("VP8X");
        WriteUInt24(bytes, 24, 5000 - 1);
        WriteUInt24(bytes, 27, 3000 - 1);
        Assert.True(ImageHeader.TryReadSize(bytes, "image/webp", out var w, out var h));
        Assert.Equal((5000, 3000), (w, h));
    }

    [Theory]
    [InlineData("image/png")]
    [InlineData("image/gif")]
    [InlineData("image/jpeg")]
    [InlineData("image/webp")]
    [InlineData("image/bmp")]
    public void Wrong_or_truncated_data_is_rejected(string mime)
    {
        Assert.False(ImageHeader.TryReadSize("not an image at all, just text bytes"u8, mime, out _, out _));
        Assert.False(ImageHeader.TryReadSize([], mime, out _, out _));
    }

    [Fact]
    public void Png_bytes_claimed_as_jpeg_are_rejected() =>
        Assert.False(ImageHeader.TryReadSize(TestImages.PngHeader(10, 10), "image/jpeg", out _, out _));

    private static byte[] WebP(string chunk)
    {
        var bytes = new byte[40];
        "RIFF"u8.CopyTo(bytes);
        "WEBP"u8.CopyTo(bytes.AsSpan(8));
        System.Text.Encoding.ASCII.GetBytes(chunk).CopyTo(bytes, 12);
        return bytes;
    }

    private static void WriteUInt24(byte[] bytes, int offset, int value)
    {
        bytes[offset] = (byte)value;
        bytes[offset + 1] = (byte)(value >> 8);
        bytes[offset + 2] = (byte)(value >> 16);
    }
}
