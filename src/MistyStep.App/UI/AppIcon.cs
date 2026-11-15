using System.Buffers.Binary;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;

namespace MistyStep.App.UI;

/// <summary>
/// Draws the tray icon at runtime: a rounded blue square with a white "M". Packed as an in-memory
/// .ico (PNG payload) so the Icon owns its handle and no P/Invoke is needed here.
/// (M6 adds light/dark and state variants.)
/// </summary>
internal static class AppIcon
{
    public static Icon Create(int size = 32)
    {
        using var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);

            var radius = size / 4f;
            using var path = new GraphicsPath();
            var r = new RectangleF(0.5f, 0.5f, size - 1f, size - 1f);
            path.AddArc(r.X, r.Y, radius * 2, radius * 2, 180, 90);
            path.AddArc(r.Right - radius * 2, r.Y, radius * 2, radius * 2, 270, 90);
            path.AddArc(r.Right - radius * 2, r.Bottom - radius * 2, radius * 2, radius * 2, 0, 90);
            path.AddArc(r.X, r.Bottom - radius * 2, radius * 2, radius * 2, 90, 90);
            path.CloseFigure();
            using var fill = new SolidBrush(Color.FromArgb(0x24, 0x57, 0xD6));
            g.FillPath(fill, path);

            using var font = new Font("Segoe UI", size * 0.5f, FontStyle.Bold, GraphicsUnit.Pixel);
            using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString("M", font, Brushes.White, new RectangleF(0, 0, size, size + 1), format);
        }

        byte[] png;
        using (var pngStream = new MemoryStream())
        {
            bitmap.Save(pngStream, ImageFormat.Png);
            png = pngStream.ToArray();
        }

        // ICONDIR (6 bytes) + one ICONDIRENTRY (16 bytes) + PNG data.
        var ico = new byte[22 + png.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(ico.AsSpan(2), 1); // type: icon
        BinaryPrimitives.WriteUInt16LittleEndian(ico.AsSpan(4), 1); // one image
        ico[6] = (byte)(size >= 256 ? 0 : size);
        ico[7] = (byte)(size >= 256 ? 0 : size);
        BinaryPrimitives.WriteUInt16LittleEndian(ico.AsSpan(10), 1);  // planes
        BinaryPrimitives.WriteUInt16LittleEndian(ico.AsSpan(12), 32); // bits per pixel
        BinaryPrimitives.WriteInt32LittleEndian(ico.AsSpan(14), png.Length);
        BinaryPrimitives.WriteInt32LittleEndian(ico.AsSpan(18), 22);
        png.CopyTo(ico, 22);

        using var icoStream = new MemoryStream(ico);
        return new Icon(icoStream);
    }
}
