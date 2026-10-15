using CodeCoverage.Git.MergeQueue.Resolution;

namespace CodeCoverage.Git.Tests;

/// <summary>
/// The content validators of docs/coverage_merge_queue_PRD.md §10.4 step 4 (R2). Each case is the smallest text that
/// tells the rule from a weaker one: a validator that only parsed loosely, or compared names before unescaping, or let a
/// DTD through, fails one of these.
/// </summary>
public class ContentValidatorsTests
{
    private static readonly JsonValidator Json = new();
    private static readonly JsonNoDuplicateKeysValidator NoDuplicates = new();
    private static readonly XmlValidator Xml = new();
    private static readonly Utf8Validator Utf8 = new();
    private static readonly NoConflictMarkersValidator NoMarkers = new();

    [Theory]
    [InlineData("{\"a\": 1}")]
    [InlineData("[1, 2, {\"b\": [true, null]}]")]
    [InlineData("\"just a string\"")]
    [InlineData("\uFEFF{\"bom\": true}\n")]
    [InlineData("{\r\n  \"crlf\": 1\r\n}\r\n")]
    public void Strict_json_is_valid(string text)
    {
        Json.Validate(text).Valid.Should().BeTrue();
        NoDuplicates.Validate(text).Valid.Should().BeTrue();
    }

    [Theory]
    [InlineData("{\"a\": 1,}")]                // trailing comma
    [InlineData("[1, 2,]")]
    [InlineData("{\"a\": 1} // comment")]      // comment
    [InlineData("/* c */ {\"a\": 1}")]
    [InlineData("{\"a\": 1}{\"b\": 2}")]       // two values
    [InlineData("{'a': 1}")]                   // single quotes
    [InlineData("")]
    [InlineData("{\"a\": 1")]
    public void Anything_looser_than_RFC_8259_is_invalid(string text)
    {
        Json.Validate(text).Valid.Should().BeFalse();
        NoDuplicates.Validate(text).Valid.Should().BeFalse();
    }

    [Fact]
    public void Nesting_is_capped_at_64_levels()
    {
        Json.Validate(new string('[', 64) + new string(']', 64)).Valid.Should().BeTrue();
        var deep = Json.Validate(new string('[', 65) + new string(']', 65));
        deep.Valid.Should().BeFalse();
        deep.Problem.Should().Contain("64");
    }

    [Fact]
    public void A_duplicate_name_is_found_at_any_depth_and_named_by_position_only()
    {
        Json.Validate("{\"a\": {\"x\": 1, \"x\": 2}}").Valid.Should().BeTrue("control: plain json allows duplicates");

        var verdict = NoDuplicates.Validate("{\n  \"a\": {\n    \"secret-x\": 1,\n    \"secret-x\": 2\n  }\n}");

        verdict.Valid.Should().BeFalse();
        verdict.Line.Should().Be(4);
        verdict.Column.Should().Be(5);
        verdict.Problem.Should().NotContain("secret");
        verdict.Position.Should().Be("line 4, column 5");
    }

    [Fact]
    public void Names_are_compared_unescaped()
    {
        NoDuplicates.Validate("{\"a\": 1, \"\\u0061\": 2}").Valid.Should().BeFalse();
        NoDuplicates.Validate("[{\"a\": 1}, {\"a\": 2}]").Valid.Should().BeTrue("one name in two objects is not a duplicate");
        NoDuplicates.Validate("{\"a\": 1, \"A\": 2}").Valid.Should().BeTrue("the comparison is ordinal");
    }

    [Fact]
    public void Xml_must_be_well_formed_and_a_DTD_is_refused()
    {
        Xml.Validate("<Project Sdk=\"x\">\r\n  <ItemGroup />\r\n</Project>\r\n").Valid.Should().BeTrue();
        Xml.Validate("\uFEFF<?xml version=\"1.0\" encoding=\"utf-8\"?><a/>").Valid.Should().BeTrue();
        Xml.Validate("<a><b></a>").Valid.Should().BeFalse();
        Xml.Validate("<a/><b/>").Valid.Should().BeFalse();

        var xxe = Xml.Validate("<?xml version=\"1.0\"?><!DOCTYPE a [<!ENTITY x SYSTEM \"file:///etc/passwd\">]><a>&x;</a>");
        xxe.Valid.Should().BeFalse();
        xxe.Problem.Should().Contain("DTD");
        Xml.Validate("<!DOCTYPE a [<!ENTITY l \"lol\"><!ENTITY l2 \"&l;&l;&l;\">]><a>&l2;</a>").Valid.Should().BeFalse();
    }

    [Fact]
    public void Utf8_refuses_a_NUL_and_a_lone_surrogate()
    {
        Utf8.Validate("plain\r\nü € 😀\n").Valid.Should().BeTrue();
        Utf8.Validate("a\nb\0").Line.Should().Be(2);
        Utf8.Validate("x\uD800y").Valid.Should().BeFalse();
    }

    [Fact]
    public void Strict_decoding_refuses_bytes_that_are_not_UTF_8_and_keeps_a_BOM()
    {
        ConflictRepository.DecodeStrict([0x7B, 0xC3, 0x28, 0x7D]).Should().BeNull();
        ConflictRepository.DecodeStrict([0x61, 0x00]).Should().BeNull();
        ConflictRepository.DecodeStrict([0xEF, 0xBB, 0xBF, 0x61]).Should().Be("\uFEFFa");
    }

    [Theory]
    [InlineData("a\n<<<<<<< ours\nb\n")]
    [InlineData("a\r\n=======\r\n")]
    [InlineData(">>>>>>> theirs")]
    [InlineData("|||||||")]
    public void A_marker_like_line_is_refused(string text)
        => NoMarkers.Validate(text).Valid.Should().BeFalse();

    [Fact]
    public void Marker_characters_inside_a_line_are_fine()
        => NoMarkers.Validate("a <<<<<<< b\n  =======\n").Valid.Should().BeTrue();
}
