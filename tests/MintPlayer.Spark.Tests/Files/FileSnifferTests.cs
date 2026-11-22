using MintPlayer.Spark.Files.Sniffing;

namespace MintPlayer.Spark.Tests.Files;

/// <summary>
/// #700 D4: what a file is comes from its bytes, never from the client. Every fixture is a real file made by a real
/// encoder (<c>Files/Fixtures</c>).
/// </summary>
public class FileSnifferTests
{
    internal static byte[] Fixture(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Files", "Fixtures", name));

    private static readonly FileSniffer Sniffer = new();

    private static readonly string[] Everything =
    [
        "image/png", "image/jpeg", "image/gif", "image/webp", "application/pdf", ".docx", ".zip",
    ];

    private static FileSniffResult Sniff(string fixture, string? asName = null, IReadOnlyList<string>? accept = null)
    {
        var bytes = Fixture(fixture);
        return Sniffer.Sniff(bytes.AsSpan(0, Math.Min(bytes.Length, FileSniffer.HeaderLength)), asName ?? fixture, accept ?? Everything);
    }

    [Theory]
    [InlineData("photo.png", "image/png")]
    [InlineData("photo.jpg", "image/jpeg")]
    [InlineData("photo.gif", "image/gif")]
    [InlineData("photo.webp", "image/webp")]
    [InlineData("scan.pdf", "application/pdf")]
    [InlineData("letter.docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document")]
    [InlineData("archive.zip", "application/zip")]
    public void Each_allow_listed_format_is_recognised_from_a_real_file(string fixture, string contentType)
    {
        var result = Sniff(fixture);

        result.Error.Should().BeNull();
        result.ContentType.Should().Be(contentType);
    }

    [Theory]
    [InlineData("page.html", "page.png")]
    [InlineData("drawing.svg", "drawing.png")]
    [InlineData("drawing.svg", "drawing.svg")]
    [InlineData("polyglot.gif", "polyglot.gif")]
    public void Markup_is_refused_whatever_the_name(string fixture, string asName)
        => Sniff(fixture, asName).Error.Should().Contain("markup");

    [Fact]
    public void An_executable_is_not_a_recognised_format()
        => Sniff("tool.exe", "tool.pdf").Error.Should().Contain("not a recognised format");

    [Fact]
    public void A_png_named_pdf_is_an_extension_mismatch()
        => Sniff("photo.png", "invoice.pdf").Error.Should().Contain("PNG").And.Contain("'.pdf'");

    [Fact]
    public void An_empty_file_is_refused()
        => Sniffer.Sniff([], "empty.png", Everything).Error.Should().Contain("empty");

    [Fact]
    public void An_empty_accept_list_refuses_everything()
        => Sniff("scan.pdf", accept: []).Error.Should().Contain("accepts no files");

    [Fact]
    public void A_recognised_format_outside_the_accept_list_is_refused()
        => Sniff("scan.pdf", accept: ["image/png"]).Error.Should().Contain("PDF files are not accepted");

    [Fact]
    public void An_extension_entry_accepts_by_extension()
        => Sniff("scan.pdf", accept: [".pdf"]).ContentType.Should().Be("application/pdf");

    [Fact]
    public void An_Image_without_accept_takes_the_raster_default_and_never_svg()
    {
        var accept = FileSniffer.EffectiveAccept("Image", null);

        accept.Should().BeEquivalentTo(["image/png", "image/jpeg", "image/gif", "image/webp"]);
        FileSniffer.EffectiveAccept("BinaryFile", null).Should().BeEmpty();
    }

    [Fact]
    public void A_registered_signature_makes_its_format_acceptable()
    {
        var bmp = new MagicBytesSignature("BMP", new Dictionary<string, string> { [".bmp"] = "image/bmp" }, (0, "BM"u8.ToArray()));
        var sniffer = new FileSniffer([bmp]);

        sniffer.IsRecognised(".bmp").Should().BeTrue();
        new FileSniffer().IsRecognised(".bmp").Should().BeFalse();
        new FileSniffer().IsRecognised("image/png").Should().BeTrue();
    }
}
