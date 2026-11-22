using MintPlayer.Spark.Files;

namespace MintPlayer.Spark.Tests.Files;

/// <summary>#700 §3.3: the display name is cleaned; it is never a path or a key.</summary>
public class FileNameSanitizerTests
{
    [Theory]
    [InlineData(@"..\..\windows\win.ini", "win.ini")]
    [InlineData("/etc/passwd", "passwd")]
    [InlineData("folder/sub/report.pdf", "report.pdf")]
    [InlineData("a\u0000b\u0007c.txt", "abc.txt")]
    [InlineData("evil‮fdp.exe", "evilfdp.exe")]
    [InlineData("zero​width.png", "zerowidth.png")]
    [InlineData("what?<>|.pdf", "what____.pdf")]
    [InlineData("CON.txt", "_CON.txt")]
    [InlineData("lpt1", "_lpt1")]
    [InlineData("  .hidden.  ", "hidden")]
    [InlineData("", "file")]
    [InlineData("   ", "file")]
    [InlineData("...", "file")]
    [InlineData(null, "file")]
    public void Names_are_cleaned(string? input, string expected)
        => FileNameSanitizer.Sanitize(input).Should().Be(expected);

    [Theory]
    [InlineData("wave.gif", "image/png", "wave.png")]
    [InlineData("photo.png", "image/png", "photo.png")]
    [InlineData("photo.JPEG", "image/jpeg", "photo.JPEG")]
    [InlineData("photo.jfif", "image/jpeg", "photo.jfif")]
    [InlineData("photo.bmp", "image/jpeg", "photo.jpg")]
    [InlineData("noext", "image/webp", "noext.webp")]
    [InlineData(".gif", "image/png", "file.png")]
    [InlineData("doc.pdf", "application/pdf", "doc.pdf")]
    public void A_re_encoded_image_takes_its_new_types_extension(string name, string contentType, string expected)
        => FileNameSanitizer.WithExtensionFor(name, contentType).Should().Be(expected);

    [Fact]
    public void Decomposed_unicode_is_normalised_to_NFC()
        => FileNameSanitizer.Sanitize("café.pdf").Should().Be("café.pdf");

    [Fact]
    public void A_long_name_is_shortened_keeping_its_extension()
    {
        var result = FileNameSanitizer.Sanitize(new string('a', 300) + ".pdf");

        result.Length.Should().Be(FileNameSanitizer.MaxLength);
        result.Should().EndWith(".pdf");
    }
}
