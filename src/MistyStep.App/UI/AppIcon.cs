using System.Buffers.Binary;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace MistyStep.App.UI;

/// <summary>
/// The app icon: "Wingfoot" by Lorc (game-icons.net, CC BY 3.0), white on the app's blue rounded square.
/// Embedded as Assets/MistyStep.ico (built by tools/make-icon.ps1). The tray's "needs attention" variant
/// adds an orange dot, packed as an in-memory .ico (PNG payload) so the Icon owns its handle and no
/// P/Invoke is needed here. (M6 adds light/dark and the paused variant.)
/// </summary>
internal static class AppIcon
{
    private const string ResourceName = "MistyStep.ico";

    /// <summary>The full multi-size icon, for window title bars and Alt+Tab.</summary>
    public static Icon ForWindow() => Load(null);

    /// <summary>A tray-sized icon (the system small-icon size for the current DPI), optionally with the attention dot.</summary>
    public static Icon ForTray(bool attention = false)
    {
        var size = SystemInformation.SmallIconSize.Width;
        using var source = Load(new Size(size, size));
        if (!attention) return (Icon)source.Clone();

        using var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            g.DrawIcon(source, new Rectangle(0, 0, size, size));

            var dot = size * 0.5f;
            var dotRect = new RectangleF(size - dot - 0.5f, size - dot - 0.5f, dot, dot);
            using var ring = new SolidBrush(Color.White);
            g.FillEllipse(ring, dotRect);
            dotRect.Inflate(-size * 0.07f, -size * 0.07f);
            using var orange = new SolidBrush(Color.FromArgb(0xF5, 0x8A, 0x07));
            g.FillEllipse(orange, dotRect);
        }

        return Pack(bitmap);
    }

    private static Icon Load(Size? size)
    {
        using var stream = typeof(AppIcon).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("The app icon resource is missing.");
        return size is { } s ? new Icon(stream, s) : new Icon(stream);
    }

    private static Icon Pack(Bitmap bitmap)
    {
        byte[] png;
        using (var pngStream = new MemoryStream())
        {
            bitmap.Save(pngStream, ImageFormat.Png);
            png = pngStream.ToArray();
        }

        // ICONDIR (6 bytes) + one ICONDIRENTRY (16 bytes) + PNG data.
        var size = bitmap.Width;
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
