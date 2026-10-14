using System.Buffers.Binary;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace MatMail.Tests;

/// <summary>The app icon (the M envelope, <c>scripts/build-app-icons.mjs</c>): the pictures have the sizes that the manifest and the head of the page promise, the masters are made so that the script can work with them, every icon that a page names exists.</summary>
public class AppIconTests
{
    private static readonly string Root = FindRoot();
    private static readonly string Web = Path.Combine(Root, "src", "MatMail", "wwwroot");

    private static string FindRoot()
    {
        string root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "MatMail.slnx")))
        {
            root = Path.GetDirectoryName(root) ?? throw new InvalidOperationException("The repository root was not found.");
        }

        return root;
    }

    [Theory]
    [InlineData("icon-512.png", 512)]
    [InlineData("icon-maskable-512.png", 512)]
    [InlineData("icon-192.png", 192)]
    [InlineData("apple-touch-icon.png", 180)]
    [InlineData("badge-96.png", 96)]
    public void A_picture_of_the_icon_is_as_big_as_its_name_and_the_manifest_say(string file, int size)
    {
        (int width, int height) = PngSize(File.ReadAllBytes(Path.Combine(Web, "icons", file)));

        Assert.Equal((size, size), (width, height));
    }

    [Fact]
    public void The_favicon_holds_three_pictures_of_16_32_and_48_pixels_that_are_png()
    {
        byte[] ico = File.ReadAllBytes(Path.Combine(Web, "favicon.ico"));

        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(ico.AsSpan(0)));    // reserved
        Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(ico.AsSpan(2)));    // an icon
        int count = BinaryPrimitives.ReadUInt16LittleEndian(ico.AsSpan(4));
        Assert.Equal(3, count);
        var sizes = new List<int>();
        for (int i = 0; i < count; i++)
        {
            int entry = 6 + (16 * i);
            int length = (int)BinaryPrimitives.ReadUInt32LittleEndian(ico.AsSpan(entry + 8));
            int offset = (int)BinaryPrimitives.ReadUInt32LittleEndian(ico.AsSpan(entry + 12));
            (int width, int height) = PngSize(ico.AsSpan(offset, length).ToArray());
            Assert.Equal(width, height);
            Assert.Equal(ico[entry], (byte)width);
            sizes.Add(width);
        }

        Assert.Equal([16, 32, 48], sizes);
    }

    [Theory]
    [InlineData("logo.svg")]
    [InlineData("mark.svg")]
    [InlineData("favicon.svg")]
    public void A_master_is_well_formed_and_marks_its_background_and_its_picture(string file)
    {
        XDocument svg = XDocument.Load(Path.Combine(Web, "icons", file));
        XNamespace ns = "http://www.w3.org/2000/svg";

        Assert.Equal("0 0 64 64", svg.Root!.Attribute("viewBox")?.Value);
        Assert.Contains(svg.Descendants(ns + "rect"), r => r.Attribute("class")?.Value == "bg" && r.Attribute("rx")?.Value == "14");   // the script makes the full-bleed pictures from them
        Assert.Single(svg.Descendants(ns + "g"), g => g.Attribute("id")?.Value == "art");                                            // ... and the picture inside the safe zone
    }

    [Fact]
    public void Every_icon_that_a_page_the_manifest_or_the_service_worker_names_exists()
    {
        string[] sources =
        [
            Path.Combine(Web, "..", "Pages", "Shared", "_Head.cshtml"),
            Path.Combine(Web, "..", "Services", "PwaManifest.cs"),
            Path.Combine(Web, "sw.js"),
        ];
        var named = sources
            .SelectMany(file => Regex.Matches(File.ReadAllText(file), "(?:~|\")(/(?:icons/[A-Za-z0-9._-]+|favicon\\.ico))").Select(m => m.Groups[1].Value))
            .Distinct()
            .ToList();

        Assert.NotEmpty(named);
        Assert.All(named, path => Assert.True(File.Exists(Path.Combine(Web, path.TrimStart('/').Replace('/', Path.DirectorySeparatorChar))), $"{path} is named but missing"));
    }

    private static (int Width, int Height) PngSize(byte[] png)
    {
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, png[..4]);
        return ((int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(16)), (int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(20)));
    }
}
