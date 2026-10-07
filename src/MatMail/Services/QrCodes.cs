using QRCoder;

namespace MatMail.Services;

/// <summary>Draws QR codes on the server, as SVG: nothing about a secret ever goes to an outside service.</summary>
public static class QrCodes
{
    /// <summary>
    /// The QR code of a text as an inline SVG that scales to the width of its container (black on white with the quiet zone around
    /// it, so it reads in dark mode too). The text is not part of the markup, which therefore is safe to write into a page as it is.
    /// </summary>
    public static string ToSvg(string text)
    {
        using var generator = new QRCodeGenerator();
        using QRCodeData data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        var svg = new SvgQRCode(data);
        return svg.GetGraphic(
            pixelsPerModule: 4,
            darkColorHex: "#000000",
            lightColorHex: "#ffffff",
            drawQuietZones: true,
            sizingMode: SvgQRCode.SizingMode.ViewBoxAttribute);
    }
}
