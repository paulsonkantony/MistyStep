using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using MistyStep.Core.Capture;
using MistyStep.Core.Imaging;

namespace MistyStep.Windows.Imaging;

/// <summary>
/// Encodes image candidates with System.Drawing (GDI+). Thread-safe; called on the thread pool.
/// <list type="bullet">
/// <item>A clipboard PNG within the size and edge limits is served unchanged.</item>
/// <item>Otherwise: decode, downscale so the long edge fits, encode PNG; if still too big and opaque, JPEG at quality 90.</item>
/// <item>A dropped .png/.jpg/.gif/.webp file within the size limit is served unchanged; a .bmp is converted.</item>
/// </list>
/// </summary>
public sealed class GdiImageEncoder : IImageEncoder
{
    public const long JpegQuality = 90;

    public ImageEncodeResult Encode(ImageCandidate candidate, CaptureLimits limits)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(limits);
        try
        {
            return candidate switch
            {
                ImageCandidate.PngBytes png => EncodePng(png, limits),
                ImageCandidate.Dib dib => EncodeDib(dib.Bytes, limits),
                ImageCandidate.File file => EncodeFile(file, limits),
                _ => ImageEncodeResult.Unreadable.Instance,
            };
        }
        catch (Exception ex) when (ex is ArgumentException or ExternalException or OutOfMemoryException or IOException or UnauthorizedAccessException)
        {
            // GDI+ reports bad image data as ArgumentException or OutOfMemoryException.
            return ImageEncodeResult.Unreadable.Instance;
        }
    }

    private static ImageEncodeResult EncodePng(ImageCandidate.PngBytes png, CaptureLimits limits)
    {
        if (png.Bytes.LongLength <= limits.MaxImageBytes && Math.Max(png.Width, png.Height) <= limits.MaxImageEdge)
            return new ImageEncodeResult.Ok(png.Bytes, "image/png", png.Width, png.Height);

        using var stream = new MemoryStream(png.Bytes, writable: false);
        using var bitmap = new Bitmap(stream);
        return Process(bitmap, limits, knownAlpha: null);
    }

    private static ImageEncodeResult EncodeDib(byte[] dib, CaptureLimits limits)
    {
        switch (DibDecoder.Decode(dib))
        {
            case DibDecodeResult.Pixels pixels:
                using (var bitmap = FromBgra(pixels.Width, pixels.Height, pixels.Bgra))
                    return Process(bitmap, limits, knownAlpha: pixels.HasAlpha);

            case DibDecodeResult.BmpFile bmp:
                using (var stream = new MemoryStream(bmp.Bytes, writable: false))
                using (var bitmap = new Bitmap(stream))
                    return Process(bitmap, limits, knownAlpha: null);

            case DibDecodeResult.TooLarge:
                return ImageEncodeResult.TooLarge.Instance;

            default:
                return ImageEncodeResult.Unreadable.Instance;
        }
    }

    private static ImageEncodeResult EncodeFile(ImageCandidate.File file, CaptureLimits limits)
    {
        var info = new FileInfo(file.Path);
        if (!info.Exists) return ImageEncodeResult.Unreadable.Instance;

        if (file.ConvertToPng)
        {
            // Read into memory first so the file is not kept locked while GDI+ decodes it.
            var bmpBytes = File.ReadAllBytes(file.Path);
            using var stream = new MemoryStream(bmpBytes, writable: false);
            using var bitmap = new Bitmap(stream);
            return Process(bitmap, limits, knownAlpha: null);
        }

        if (info.Length > limits.MaxImageBytes) return ImageEncodeResult.TooLarge.Instance;
        var bytes = File.ReadAllBytes(file.Path);
        if (bytes.LongLength > limits.MaxImageBytes) return ImageEncodeResult.TooLarge.Instance;

        // Served unchanged; the header must match the extension's type.
        return ImageHeader.TryReadSize(bytes, file.Mime, out var width, out var height)
            ? new ImageEncodeResult.Ok(bytes, file.Mime, width, height)
            : ImageEncodeResult.Unreadable.Instance;
    }

    /// <summary>Downscale, encode PNG, and fall back to JPEG for opaque images that are still too big.</summary>
    private static ImageEncodeResult Process(Bitmap source, CaptureLimits limits, bool? knownAlpha)
    {
        using var scaled = Downscale(source, limits.MaxImageEdge);
        var image = scaled ?? source;

        var png = Save(image, ImageFormat.Png, null);
        if (png.LongLength <= limits.MaxImageBytes)
            return new ImageEncodeResult.Ok(png, "image/png", image.Width, image.Height);

        var hasAlpha = knownAlpha ?? HasTransparency(image);
        if (hasAlpha) return ImageEncodeResult.TooLarge.Instance;

        var jpeg = SaveJpeg(image);
        return jpeg.LongLength <= limits.MaxImageBytes
            ? new ImageEncodeResult.Ok(jpeg, "image/jpeg", image.Width, image.Height)
            : ImageEncodeResult.TooLarge.Instance;
    }

    /// <summary>Returns a scaled copy if the long edge exceeds <paramref name="maxEdge"/>, else null.</summary>
    internal static Bitmap? Downscale(Bitmap source, int maxEdge)
    {
        var longEdge = Math.Max(source.Width, source.Height);
        if (longEdge <= maxEdge) return null;

        var scale = (double)maxEdge / longEdge;
        var width = Math.Max(1, (int)Math.Round(source.Width * scale));
        var height = Math.Max(1, (int)Math.Round(source.Height * scale));

        var target = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        target.SetResolution(source.HorizontalResolution, source.VerticalResolution);
        using var graphics = Graphics.FromImage(target);
        graphics.CompositingMode = CompositingMode.SourceCopy;
        graphics.CompositingQuality = CompositingQuality.HighQuality;
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.SmoothingMode = SmoothingMode.HighQuality;
        using var attributes = new ImageAttributes();
        attributes.SetWrapMode(WrapMode.TileFlipXY); // avoids dark fringes at the edges
        graphics.DrawImage(source, new Rectangle(0, 0, width, height), 0, 0, source.Width, source.Height, GraphicsUnit.Pixel, attributes);
        return target;
    }

    /// <summary>True if any pixel is not fully opaque. Formats without alpha are opaque by definition.</summary>
    internal static bool HasTransparency(Bitmap image)
    {
        if (!Image.IsAlphaPixelFormat(image.PixelFormat)) return false;

        var rect = new Rectangle(0, 0, image.Width, image.Height);
        var data = image.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var row = new byte[image.Width * 4];
            for (var y = 0; y < image.Height; y++)
            {
                Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, row.Length);
                for (var x = 3; x < row.Length; x += 4)
                {
                    if (row[x] != 0xFF) return true;
                }
            }

            return false;
        }
        finally
        {
            image.UnlockBits(data);
        }
    }

    internal static Bitmap FromBgra(int width, int height, byte[] bgra)
    {
        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        var data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            var rowBytes = width * 4;
            for (var y = 0; y < height; y++)
                Marshal.Copy(bgra, y * rowBytes, data.Scan0 + y * data.Stride, rowBytes);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        return bitmap;
    }

    private static byte[] SaveJpeg(Image image)
    {
        var codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(Encoder.Quality, JpegQuality);

        // JPEG has no alpha: flatten onto white so any (fully opaque) image encodes predictably.
        using var opaque = new Bitmap(image.Width, image.Height, PixelFormat.Format24bppRgb);
        opaque.SetResolution(image.HorizontalResolution, image.VerticalResolution);
        using (var graphics = Graphics.FromImage(opaque))
        {
            graphics.Clear(Color.White);
            graphics.DrawImage(image, 0, 0, image.Width, image.Height);
        }

        return Save(opaque, codec, parameters);
    }

    private static byte[] Save(Image image, ImageFormat format, EncoderParameters? parameters)
    {
        var codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == format.Guid);
        return Save(image, codec, parameters);
    }

    private static byte[] Save(Image image, ImageCodecInfo codec, EncoderParameters? parameters)
    {
        using var output = new MemoryStream();
        image.Save(output, codec, parameters);
        return output.ToArray();
    }
}
