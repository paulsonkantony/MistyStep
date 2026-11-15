using System.Drawing;
using QRCoder;

namespace MistyStep.App.UI;

/// <summary>Renders the pairing URL as a QR code bitmap.</summary>
internal static class QrImage
{
    public static Bitmap Create(string text, int pixelsPerModule = 8)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(data).GetGraphic(pixelsPerModule);
        using var stream = new MemoryStream(png);
        using var decoded = new Bitmap(stream);
        return new Bitmap(decoded); // detached from the stream
    }
}
