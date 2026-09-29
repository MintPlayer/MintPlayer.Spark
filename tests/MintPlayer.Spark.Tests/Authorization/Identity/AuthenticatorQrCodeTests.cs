using System.Globalization;
using System.Xml.Linq;
using MintPlayer.Spark.Authorization.Identity;
using Xunit.Abstractions;
using ZXing;
using ZXing.Common;
using ZXing.QrCode;

namespace MintPlayer.Spark.Tests.Authorization.Identity;

/// <summary>
/// Spike SP-D: the server-rendered SVG QR code decodes — rasterised from the SVG's own geometry and
/// read by ZXing's QR reader, the way a scanner reads a screen.
/// </summary>
public class AuthenticatorQrCodeTests(ITestOutputHelper output)
{
    private const string Uri = "otpauth://totp/Spark%20Tests:jane%40example.com?secret=JBSWY3DPEHPK3PXPJBSWY3DPEHPK3PXP&issuer=Spark%20Tests&digits=6";

    [Fact]
    public void SP_D_the_svg_decodes_back_to_the_otpauth_uri()
    {
        var svg = new SparkQrCodeRenderer().RenderSvg(Uri);
        output.WriteLine(svg.Length > 600 ? svg[..600] : svg);

        var (pixels, width, height) = Rasterize(svg, scale: 2);
        var source = new RGBLuminanceSource(pixels, width, height, RGBLuminanceSource.BitmapFormat.Gray8);
        var result = new QRCodeReader().decode(new BinaryBitmap(new HybridBinarizer(source)));

        result.Should().NotBeNull("ZXing must find and decode the code in the rendered image");
        result!.Text.Should().Be(Uri);
    }

    [Fact]
    public void SP_D_the_svg_draws_exactly_the_encoded_modules()
    {
        var renderer = new SparkQrCodeRenderer();
        var modules = renderer.RenderModules(Uri);
        var (pixels, width, _) = Rasterize(renderer.RenderSvg(Uri), scale: 1);
        var moduleSize = width / modules.GetLength(0);
        output.WriteLine($"modules={modules.GetLength(0)} svgWidth={width} moduleSize={moduleSize}");

        for (var y = 0; y < modules.GetLength(0); y++)
            for (var x = 0; x < modules.GetLength(1); x++)
            {
                var dark = pixels[(y * moduleSize + moduleSize / 2) * width + x * moduleSize + moduleSize / 2] < 128;
                dark.Should().Be(modules[y, x], $"module ({x},{y})");
            }
    }

    /// <summary>
    /// Paints the SVG's filled shapes (rect elements, and path elements made of M/H/V/h/v/Z commands)
    /// onto a white Gray8 canvas. Deliberately minimal — it only has to understand what the renderer emits,
    /// and fails loudly on anything else.
    /// </summary>
    private static (byte[] Pixels, int Width, int Height) Rasterize(string svg, int scale)
    {
        var document = XDocument.Parse(svg);
        var root = document.Root!;
        var viewBox = ((string?)root.Attribute("viewBox"))?.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries).Select(double.Parse).ToArray();
        var width = (int)Math.Round((viewBox?[2] ?? double.Parse(((string)root.Attribute("width")!).Replace("px", ""), CultureInfo.InvariantCulture)) * scale);
        var height = (int)Math.Round((viewBox?[3] ?? double.Parse(((string)root.Attribute("height")!).Replace("px", ""), CultureInfo.InvariantCulture)) * scale);
        var pixels = Enumerable.Repeat((byte)255, width * height).ToArray();

        void Fill(double x, double y, double w, double h, byte value)
        {
            for (var py = (int)Math.Round(y * scale); py < (int)Math.Round((y + h) * scale); py++)
                for (var px = (int)Math.Round(x * scale); px < (int)Math.Round((x + w) * scale); px++)
                    if (px >= 0 && py >= 0 && px < width && py < height)
                        pixels[py * width + px] = value;
        }

        static bool IsLight(string? fill) => fill is null
            ? false
            : fill.Equals("#FFFFFF", StringComparison.OrdinalIgnoreCase) || fill.Equals("white", StringComparison.OrdinalIgnoreCase)
              || fill.Equals("#FFF", StringComparison.OrdinalIgnoreCase);

        foreach (var element in root.Descendants())
        {
            var fill = (string?)element.Attribute("fill");
            var value = IsLight(fill) ? (byte)255 : (byte)0;

            switch (element.Name.LocalName)
            {
                case "rect":
                    Fill(D(element, "x"), D(element, "y"), D(element, "width"), D(element, "height"), value);
                    break;
                case "path":
                    // QRCoder emits one path of horizontal runs: "M{x} {y}h{w}v{h}h-{w}z" per run.
                    var d = (string)element.Attribute("d")!;
                    var runs = System.Text.RegularExpressions.Regex.Matches(d, @"M(\d+) (\d+)h(\d+)v(\d+)h-\d+z");
                    string.Concat(runs.Select(m => m.Value)).Length.Should().Be(d.Length, "the path holds only runs this rasteriser understands");
                    foreach (System.Text.RegularExpressions.Match run in runs)
                        Fill(double.Parse(run.Groups[1].Value), double.Parse(run.Groups[2].Value),
                            double.Parse(run.Groups[3].Value), double.Parse(run.Groups[4].Value), value);
                    break;
                case "svg":
                    break;
                default:
                    throw new NotSupportedException("unexpected SVG element: " + element.Name.LocalName);
            }
        }

        return (pixels, width, height);
    }

    private static double D(XElement element, string name)
        => double.Parse(((string?)element.Attribute(name) ?? "0").Replace("px", ""), CultureInfo.InvariantCulture);
}
