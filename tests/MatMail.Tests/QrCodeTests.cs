using MatMail.Services;

namespace MatMail.Tests;

public class QrCodeTests
{
    private const string Uri = "otpauth://totp/MatMail:alice?secret=JBSWY3DPEHPK3PXP&issuer=MatMail&algorithm=SHA1&digits=6&period=30";

    [Fact]
    public void The_code_is_an_svg_that_scales_with_its_container()
    {
        string svg = QrCodes.ToSvg(Uri);

        Assert.StartsWith("<svg", svg);
        string root = svg[..(svg.IndexOf('>') + 1)];
        Assert.Contains("viewBox=\"0 0 ", root);
        Assert.DoesNotContain("width=", root);
        Assert.DoesNotContain("height=", root);
        Assert.Contains("<path", svg);
    }

    [Fact]
    public void It_is_black_on_white_so_it_reads_in_dark_mode_too()
    {
        string svg = QrCodes.ToSvg(Uri);

        Assert.Contains("#000000", svg);
        Assert.Contains("#ffffff", svg);
    }

    [Fact]
    public void The_text_is_not_part_of_the_markup_and_the_same_text_gives_the_same_picture()
    {
        string svg = QrCodes.ToSvg(Uri);

        Assert.DoesNotContain("JBSWY3DPEHPK3PXP", svg);
        Assert.DoesNotContain("<script", svg, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(svg, QrCodes.ToSvg(Uri));
        Assert.NotEqual(svg, QrCodes.ToSvg(Uri.Replace("alice", "bob")));
    }
}
