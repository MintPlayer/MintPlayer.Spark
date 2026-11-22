using System.Diagnostics;
using MintPlayer.Spark.Files.Images;
using MintPlayer.Spark.Files.Images.NetVips;
using MintPlayer.Spark.Files.Images.SkiaSharp;
using SkiaSharp;

namespace MintPlayer.Spark.Tests.Files.Images;

/// <summary>
/// The contract every <see cref="IImageProcessor"/> keeps (#700 PRD §3.6, SP5), run against each shipped processor: the pixel
/// cap read from the header before decoding, the eight EXIF orientations, a re-encode with no metadata and nothing appended,
/// the first frame of an animation, variants never upscaled, and refusal of what cannot be decoded.
/// </summary>
public abstract class ImageProcessorConformanceTests
{
    protected abstract IImageProcessor Processor { get; }

    private static readonly IReadOnlyDictionary<string, int> NoVariants = new Dictionary<string, int>();

    private async Task<(ProcessedImage Image, byte[] Bytes)> ProcessAsync(byte[] input, string contentType, long maxPixels = 40_000_000, IReadOnlyDictionary<string, int>? variants = null)
    {
        using var source = new MemoryStream(input);
        var image = await Processor.ProcessAsync(source, new ImageProcessingRequest(contentType, maxPixels, variants ?? NoVariants), CancellationToken.None);
        using var buffer = new MemoryStream();
        await image.Content.CopyToAsync(buffer);
        return (image, buffer.ToArray());
    }

    private static async Task<byte[]> BytesOf(Stream stream)
    {
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);
        return buffer.ToArray();
    }

    private async Task<ImageRejectedException> RejectedAsync(byte[] input, string contentType, long maxPixels = 40_000_000)
    {
        try
        {
            var (image, _) = await ProcessAsync(input, contentType, maxPixels);
            await image.DisposeAsync();
        }
        catch (ImageRejectedException rejected)
        {
            return rejected;
        }
        throw new Xunit.Sdk.XunitException("The processor accepted an image it must refuse.");
    }

    [Fact]
    public async Task A_bomb_is_refused_from_its_header_before_anything_is_decoded()
    {
        var bomb = ImageFixtures.Bomb;
        var watch = Stopwatch.StartNew();

        var rejected = await RejectedAsync(bomb, "image/png");

        watch.Stop();
        rejected.Message.Should().Contain("30000×30000");
        // Decoding 900 Mpx would take seconds and gigabytes; reading a header takes milliseconds (SP5: 7–70 ms).
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
    }

    [Theory]
    [InlineData(400, false)]
    [InlineData(399, true)]
    public async Task The_pixel_cap_counts_width_times_height(long maxPixels, bool refused)
    {
        var png = ImageFixtures.Png(20, 20);

        if (refused)
            (await RejectedAsync(png, "image/png", maxPixels)).Message.Should().Contain("20×20");
        else
            await using ((await ProcessAsync(png, "image/png", maxPixels)).Image) { }
    }

    public static TheoryData<ushort, int, int, SKColor[]> Orientations => new()
    {
        // Displayed quadrants (top-left, top-right, bottom-left, bottom-right) of the stored R G / B W image, per EXIF 1–8.
        { 1, 64, 32, [ImageFixtures.Red, ImageFixtures.Green, ImageFixtures.Blue, ImageFixtures.White] },
        { 2, 64, 32, [ImageFixtures.Green, ImageFixtures.Red, ImageFixtures.White, ImageFixtures.Blue] },
        { 3, 64, 32, [ImageFixtures.White, ImageFixtures.Blue, ImageFixtures.Green, ImageFixtures.Red] },
        { 4, 64, 32, [ImageFixtures.Blue, ImageFixtures.White, ImageFixtures.Red, ImageFixtures.Green] },
        { 5, 32, 64, [ImageFixtures.Red, ImageFixtures.Blue, ImageFixtures.Green, ImageFixtures.White] },
        { 6, 32, 64, [ImageFixtures.Blue, ImageFixtures.Red, ImageFixtures.White, ImageFixtures.Green] },
        { 7, 32, 64, [ImageFixtures.White, ImageFixtures.Green, ImageFixtures.Blue, ImageFixtures.Red] },
        { 8, 32, 64, [ImageFixtures.Green, ImageFixtures.White, ImageFixtures.Red, ImageFixtures.Blue] },
    };

    [Theory]
    [MemberData(nameof(Orientations))]
    public async Task Every_exif_orientation_is_applied(ushort orientation, int width, int height, SKColor[] quadrants)
    {
        var jpeg = ImageFixtures.WithExif(ImageFixtures.Jpeg(64, 32), orientation);

        var (image, bytes) = await ProcessAsync(jpeg, "image/jpeg");
        await using var _ = image;

        image.ContentType.Should().Be("image/jpeg");
        (image.Width, image.Height).Should().Be((width, height));
        using var decoded = SKBitmap.Decode(bytes);
        (decoded.Width, decoded.Height).Should().Be((width, height), "the stored image is upright, with no orientation left to apply");
        var points = new[] { (width / 4, height / 4), (3 * width / 4, height / 4), (width / 4, 3 * height / 4), (3 * width / 4, 3 * height / 4) };
        for (var i = 0; i < 4; i++)
            Close(decoded.GetPixel(points[i].Item1, points[i].Item2), quadrants[i]).Should().BeTrue(
                $"orientation {orientation}, quadrant {i}: expected {quadrants[i]}, got {decoded.GetPixel(points[i].Item1, points[i].Item2)}");
    }

    [Fact]
    public async Task The_re_encode_drops_exif_gps_and_the_artist()
    {
        var jpeg = ImageFixtures.WithExif(ImageFixtures.Jpeg(64, 32), 1, artist: "SPARK-ARTIST-MARKER", gps: true);
        ImageFixtures.Contains(jpeg, "SPARK-ARTIST-MARKER").Should().BeTrue("the fixture carries the marker");

        var (image, bytes) = await ProcessAsync(jpeg, "image/jpeg", variants: new Dictionary<string, int> { ["thumb"] = 16 });
        await using var _ = image;

        bytes.Should().NotEqual(jpeg, "an image is never stored as uploaded");
        ImageFixtures.Contains(bytes, "SPARK-ARTIST-MARKER").Should().BeFalse();
        ImageFixtures.Contains(bytes, "Exif").Should().BeFalse("no EXIF block (and so no GPS IFD) survives");
        var variant = await BytesOf(image.Variants.Single().Content);
        ImageFixtures.Contains(variant, "SPARK-ARTIST-MARKER").Should().BeFalse();
        ImageFixtures.Contains(variant, "Exif").Should().BeFalse();
    }

    [Fact]
    public async Task A_payload_appended_to_a_gif_is_gone_and_the_gif_becomes_a_png()
    {
        var polyglot = FileSnifferTests.Fixture("polyglot.gif");
        ImageFixtures.Contains(polyglot, "<script").Should().BeTrue("the fixture carries the payload");

        var (image, bytes) = await ProcessAsync(polyglot, "image/gif");
        await using var _ = image;

        image.ContentType.Should().Be("image/png");
        bytes[..4].Should().Equal(0x89, 0x50, 0x4E, 0x47);
        ImageFixtures.Contains(bytes, "<script").Should().BeFalse();
    }

    [Fact]
    public async Task An_animated_gif_keeps_only_its_first_frame()
    {
        var (image, bytes) = await ProcessAsync(ImageFixtures.AnimatedGif(), "image/gif");
        await using var _ = image;

        image.ContentType.Should().Be("image/png");
        (image.Width, image.Height).Should().Be((2, 2), "one frame, not the frames stacked");
        using var decoded = SKBitmap.Decode(bytes);
        (decoded.Width, decoded.Height).Should().Be((2, 2));
        Close(decoded.GetPixel(1, 1), new SKColor(220, 20, 20)).Should().BeTrue($"frame 0 is red, got {decoded.GetPixel(1, 1)}");
    }

    [Fact]
    public async Task Variants_are_webp_fitted_to_their_edge_and_never_upscaled()
    {
        var png = ImageFixtures.Png(1000, 500);

        var (image, _) = await ProcessAsync(png, "image/png", variants: new Dictionary<string, int> { ["thumb"] = 160, ["huge"] = 4000 });
        await using var __ = image;

        image.ContentType.Should().Be("image/png");
        (image.Width, image.Height).Should().Be((1000, 500));
        var thumb = image.Variants.Single(v => v.Name == "thumb");
        var huge = image.Variants.Single(v => v.Name == "huge");
        (thumb.ContentType, thumb.Width, thumb.Height).Should().Be(("image/webp", 160, 80));
        (huge.ContentType, huge.Width, huge.Height).Should().Be(("image/webp", 1000, 500), "a variant is never larger than the original");
        foreach (var variant in new[] { thumb, huge })
        {
            var bytes = await BytesOf(variant.Content);
            System.Text.Encoding.ASCII.GetString(bytes, 8, 4).Should().Be("WEBP");
            using var decoded = SKBitmap.Decode(bytes);
            (decoded.Width, decoded.Height).Should().Be((variant.Width, variant.Height));
            Close(decoded.GetPixel(variant.Width / 4, variant.Height / 4), ImageFixtures.Red).Should().BeTrue("the variant is the same picture");
        }
    }

    [Fact]
    public async Task Jpeg_and_webp_keep_their_type()
    {
        var (jpeg, _) = await ProcessAsync(ImageFixtures.Jpeg(32, 32), "image/jpeg");
        await using (jpeg)
            jpeg.ContentType.Should().Be("image/jpeg");

        using var bitmap = ImageFixtures.Quadrants(32, 32);
        var (webp, bytes) = await ProcessAsync(ImageFixtures.Encode(bitmap, SKEncodedImageFormat.Webp, 90), "image/webp");
        await using (webp)
        {
            webp.ContentType.Should().Be("image/webp");
            System.Text.Encoding.ASCII.GetString(bytes, 8, 4).Should().Be("WEBP");
        }
    }

    [Fact]
    public async Task Bytes_that_do_not_decode_are_refused()
    {
        var garbage = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.Concat(Enumerable.Repeat((byte)0x5A, 200)).ToArray();

        (await RejectedAsync(garbage, "image/png")).Message.Should().Be("The image could not be read.");
    }

    [Fact]
    public async Task A_truncated_image_is_refused_not_padded()
    {
        var jpeg = ImageFixtures.Jpeg(256, 256);

        await RejectedAsync(jpeg[..(jpeg.Length / 2)], "image/jpeg");
    }

    [Fact]
    public async Task Bytes_of_another_format_than_the_sniffed_one_are_refused()
    {
        await RejectedAsync(ImageFixtures.Jpeg(32, 32), "image/png");
    }

    private static bool Close(SKColor actual, SKColor expected, int tolerance = 48)
        => Math.Abs(actual.Red - expected.Red) <= tolerance
        && Math.Abs(actual.Green - expected.Green) <= tolerance
        && Math.Abs(actual.Blue - expected.Blue) <= tolerance;
}

public class SkiaSharpImageProcessorTests : ImageProcessorConformanceTests
{
    protected override IImageProcessor Processor { get; } = new SkiaSharpImageProcessor();
}

public class NetVipsImageProcessorTests : ImageProcessorConformanceTests
{
    protected override IImageProcessor Processor { get; } = new NetVipsImageProcessor();
}
