using System.Buffers.Binary;
using System.Drawing;
using System.Drawing.Imaging;
using MistyStep.Core.Capture;
using MistyStep.Core.Imaging;
using MistyStep.Windows.Imaging;

namespace MistyStep.Windows.Tests;

public sealed class GdiImageEncoderTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "mistystep-enc-" + Guid.NewGuid().ToString("N"));
    private readonly GdiImageEncoder _encoder = new();
    private static readonly CaptureLimits Defaults = new();

    public GdiImageEncoderTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    // --- helpers ---

    private static Bitmap Solid(int width, int height, Color color)
    {
        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bitmap);
        g.Clear(color);
        return bitmap;
    }

    /// <summary>A photo-like image: smooth gradients plus mild noise. Large as PNG, small as JPEG.</summary>
    private static Bitmap Photo(int width, int height, int alpha = 255)
    {
        var random = new Random(42);
        var bgra = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var i = (y * width + x) * 4;
            bgra[i] = (byte)Math.Clamp(x * 255 / width + random.Next(-12, 12), 0, 255);
            bgra[i + 1] = (byte)Math.Clamp(y * 255 / height + random.Next(-12, 12), 0, 255);
            bgra[i + 2] = (byte)Math.Clamp((x + y) * 255 / (width + height) + random.Next(-12, 12), 0, 255);
            bgra[i + 3] = (byte)alpha;
        }

        return GdiImageEncoder.FromBgra(width, height, bgra);
    }

    private static byte[] Encode(Image image, ImageFormat format)
    {
        using var ms = new MemoryStream();
        image.Save(ms, format);
        return ms.ToArray();
    }

    private static Bitmap Decode(byte[] bytes) => new(new MemoryStream(bytes));

    private static byte[] Dib32(int width, int height, byte b, byte g, byte r, byte a)
    {
        var bytes = new byte[40 + width * height * 4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, 40);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), width);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), height);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(12), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(14), 32);
        for (var i = 40; i < bytes.Length; i += 4) { bytes[i] = b; bytes[i + 1] = g; bytes[i + 2] = r; bytes[i + 3] = a; }
        return bytes;
    }

    private static byte[] Dib24(int width, int height, byte b, byte g, byte r)
    {
        var stride = (width * 3 + 3) / 4 * 4;
        var bytes = new byte[40 + stride * height];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, 40);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), width);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), height);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(12), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(14), 24);
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var i = 40 + y * stride + x * 3;
            bytes[i] = b; bytes[i + 1] = g; bytes[i + 2] = r;
        }

        return bytes;
    }

    private static ImageEncodeResult.Ok AssertOk(ImageEncodeResult result) => Assert.IsType<ImageEncodeResult.Ok>(result);

    // --- PNG from the clipboard ---

    [Fact]
    public void Png_within_limits_is_served_unchanged()
    {
        using var bitmap = Solid(300, 200, Color.Teal);
        var png = Encode(bitmap, ImageFormat.Png);
        var ok = AssertOk(_encoder.Encode(new ImageCandidate.PngBytes(png, 300, 200), Defaults));
        Assert.Same(png, ok.Bytes);
        Assert.Equal(("image/png", 300, 200), (ok.Mime, ok.Width, ok.Height));
    }

    [Fact]
    public void Png_above_the_edge_limit_is_downscaled_keeping_aspect()
    {
        using var bitmap = Solid(5000, 1000, Color.Orange);
        var png = Encode(bitmap, ImageFormat.Png);
        var ok = AssertOk(_encoder.Encode(new ImageCandidate.PngBytes(png, 5000, 1000), Defaults));

        Assert.Equal("image/png", ok.Mime);
        Assert.Equal((4096, 819), (ok.Width, ok.Height));
        using var decoded = Decode(ok.Bytes);
        Assert.Equal((4096, 819), (decoded.Width, decoded.Height));
    }

    [Fact]
    public void Custom_edge_limit_is_honoured()
    {
        using var bitmap = Solid(800, 600, Color.Orange);
        var png = Encode(bitmap, ImageFormat.Png);
        var ok = AssertOk(_encoder.Encode(new ImageCandidate.PngBytes(png, 800, 600), Defaults with { MaxImageEdge = 400 }));
        Assert.Equal((400, 300), (ok.Width, ok.Height));
    }

    [Fact]
    public void Opaque_image_too_big_as_png_falls_back_to_jpeg()
    {
        using var photo = Photo(1200, 900);
        var png = Encode(photo, ImageFormat.Png);
        var limits = Defaults with { MaxImageBytes = png.Length / 3 };

        var result = AssertOk(_encoder.Encode(new ImageCandidate.PngBytes(png, 1200, 900), limits));
        Assert.Equal("image/jpeg", result.Mime);
        Assert.True(result.Bytes.Length <= limits.MaxImageBytes);
        Assert.True(ImageHeader.TryReadSize(result.Bytes, "image/jpeg", out var w, out var h));
        Assert.Equal((1200, 900), (w, h));
    }

    [Fact]
    public void Transparent_image_too_big_as_png_is_too_large_not_jpeg()
    {
        using var photo = Photo(600, 400, alpha: 128);
        var png = Encode(photo, ImageFormat.Png);
        var result = _encoder.Encode(new ImageCandidate.PngBytes(png, 600, 400), Defaults with { MaxImageBytes = 1000, MaxImageEdge = 300 });
        Assert.IsType<ImageEncodeResult.TooLarge>(result);
    }

    [Fact]
    public void Image_that_never_fits_is_too_large()
    {
        using var photo = Photo(400, 400);
        var png = Encode(photo, ImageFormat.Png);
        Assert.IsType<ImageEncodeResult.TooLarge>(_encoder.Encode(new ImageCandidate.PngBytes(png, 400, 400), Defaults with { MaxImageBytes = 100, MaxImageEdge = 300 }));
    }

    [Fact]
    public void Corrupt_png_is_unreadable()
    {
        var bogus = new byte[64];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(bogus, 0);
        Assert.IsType<ImageEncodeResult.Unreadable>(_encoder.Encode(new ImageCandidate.PngBytes(bogus, 9000, 9000), Defaults));
    }

    // --- DIBs ---

    [Fact]
    public void Dib_32bit_with_zero_alpha_becomes_an_opaque_png()
    {
        var ok = AssertOk(_encoder.Encode(new ImageCandidate.Dib(Dib32(20, 10, 10, 20, 200, 0), IsV5: false), Defaults));
        Assert.Equal(("image/png", 20, 10), (ok.Mime, ok.Width, ok.Height));
        using var decoded = Decode(ok.Bytes);
        Assert.Equal(Color.FromArgb(255, 200, 20, 10), decoded.GetPixel(5, 5));
    }

    [Fact]
    public void Dib_32bit_with_real_alpha_keeps_transparency()
    {
        var ok = AssertOk(_encoder.Encode(new ImageCandidate.Dib(Dib32(8, 8, 0, 0, 255, 100), IsV5: true), Defaults));
        using var decoded = Decode(ok.Bytes);
        Assert.Equal(100, decoded.GetPixel(3, 3).A);
    }

    [Fact]
    public void Dib_24bit_converts_through_the_bmp_path()
    {
        var ok = AssertOk(_encoder.Encode(new ImageCandidate.Dib(Dib24(7, 5, 255, 0, 0), IsV5: false), Defaults));
        Assert.Equal((7, 5), (ok.Width, ok.Height));
        using var decoded = Decode(ok.Bytes);
        Assert.Equal(Color.FromArgb(255, 0, 0, 255), decoded.GetPixel(3, 2));
    }

    [Fact]
    public void Large_dib_is_downscaled()
    {
        var ok = AssertOk(_encoder.Encode(new ImageCandidate.Dib(Dib32(5000, 100, 1, 2, 3, 0), IsV5: false), Defaults));
        Assert.Equal(4096, ok.Width);
    }

    [Fact]
    public void Invalid_dib_is_unreadable() =>
        Assert.IsType<ImageEncodeResult.Unreadable>(_encoder.Encode(new ImageCandidate.Dib(new byte[10], IsV5: false), Defaults));

    // --- Files ---

    [Theory]
    [InlineData("jpg", "image/jpeg")]
    [InlineData("gif", "image/gif")]
    [InlineData("png", "image/png")]
    public void Image_file_is_served_unchanged_with_its_size(string extension, string mime)
    {
        using var bitmap = Solid(64, 48, Color.Purple);
        var format = extension switch { "jpg" => ImageFormat.Jpeg, "gif" => ImageFormat.Gif, _ => ImageFormat.Png };
        var path = Path.Combine(_directory, "pic." + extension);
        bitmap.Save(path, format);

        var ok = AssertOk(_encoder.Encode(new ImageCandidate.File(path, mime, ConvertToPng: false), Defaults));
        Assert.Equal(File.ReadAllBytes(path), ok.Bytes);
        Assert.Equal((mime, 64, 48), (ok.Mime, ok.Width, ok.Height));
    }

    [Fact]
    public void Large_image_file_is_served_unchanged_without_downscaling()
    {
        using var bitmap = Solid(5000, 10, Color.Purple);
        var path = Path.Combine(_directory, "wide.png");
        bitmap.Save(path, ImageFormat.Png);
        var ok = AssertOk(_encoder.Encode(new ImageCandidate.File(path, "image/png", false), Defaults));
        Assert.Equal(5000, ok.Width);
    }

    [Fact]
    public void Bmp_file_is_converted_to_png()
    {
        using var bitmap = Solid(30, 20, Color.Green);
        var path = Path.Combine(_directory, "pic.bmp");
        bitmap.Save(path, ImageFormat.Bmp);

        var ok = AssertOk(_encoder.Encode(new ImageCandidate.File(path, "image/png", ConvertToPng: true), Defaults));
        Assert.Equal(("image/png", 30, 20), (ok.Mime, ok.Width, ok.Height));
        Assert.True(PngInfo.TryRead(ok.Bytes, out _, out _));
        File.Delete(path); // not left locked
    }

    [Fact]
    public void Image_file_over_the_limit_is_too_large()
    {
        var path = Path.Combine(_directory, "big.jpg");
        using (var bitmap = Photo(300, 300)) bitmap.Save(path, ImageFormat.Jpeg);
        Assert.IsType<ImageEncodeResult.TooLarge>(_encoder.Encode(new ImageCandidate.File(path, "image/jpeg", false), Defaults with { MaxImageBytes = 100 }));
    }

    [Fact]
    public void File_whose_content_does_not_match_its_extension_is_unreadable()
    {
        var path = Path.Combine(_directory, "fake.jpg");
        File.WriteAllText(path, "this is not a jpeg");
        Assert.IsType<ImageEncodeResult.Unreadable>(_encoder.Encode(new ImageCandidate.File(path, "image/jpeg", false), Defaults));
    }

    [Fact]
    public void Missing_file_is_unreadable() =>
        Assert.IsType<ImageEncodeResult.Unreadable>(_encoder.Encode(new ImageCandidate.File(Path.Combine(_directory, "gone.png"), "image/png", false), Defaults));

    // --- Header parser against real GDI+ output ---

    [Theory]
    [InlineData("image/jpeg")]
    [InlineData("image/gif")]
    [InlineData("image/png")]
    public void ImageHeader_reads_real_encoder_output(string mime)
    {
        using var bitmap = Solid(123, 45, Color.Coral);
        var format = mime switch { "image/jpeg" => ImageFormat.Jpeg, "image/gif" => ImageFormat.Gif, _ => ImageFormat.Png };
        Assert.True(ImageHeader.TryReadSize(Encode(bitmap, format), mime, out var w, out var h));
        Assert.Equal((123, 45), (w, h));
    }
}
