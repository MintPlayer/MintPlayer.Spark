using MintPlayer.Spark.Webhooks.GitHub.DevTunnel.Services;
using Xunit;

namespace MintPlayer.Spark.Tests.Webhooks.DevTunnel;

/// <summary>
/// The regression suite for the defect that made the smee tunnel unusable: the body reaching the
/// processor must be the bytes GitHub signed, character for character. See
/// <c>docs/prd/PRD-SlingBot-Archival-Followups.md</c> §1.
/// </summary>
public class SmeeBodyReaderTests
{
    // The bytes GitHub signed: minified, fractional-second timestamp (the installation-event
    // format), a trailing-zero decimal, escapes and whitespace inside strings.
    private const string Signed =
        """{"installation":{"id":153131096,"created_at":"2026-08-12T08:45:12.000+02:00","note":"a \"quoted\"  string with {braces} and \\n"},"count":1.50}""";

    private static string Frame(string body)
        => $$"""{"host":"https://smee.io","x-github-event":"installation","x-hub-signature-256":"sha256=abc","body":{{body}},"timestamp":1700000000}""";

    [Fact]
    public void Reads_the_body_back_byte_for_byte()
    {
        SmeeBodyReader.TryReadDelivery(Frame(Signed), out _, out var body).Should().BeTrue();

        // Not "equivalent JSON" — identical text. A parse/re-serialize would pass an equivalence
        // check and still break the HMAC.
        body.Should().Be(Signed);
    }

    [Theory]
    // Each of these round-trips wrong through a JSON object model. They are the actual failure
    // modes measured against Smee.IO.Client, not hypotheticals.
    [InlineData("""{"created_at":"2024-01-01T12:00:12.000+02:00"}""")] // offset rebased to local TZ
    [InlineData("""{"amount":1.50}""")]                                // trailing zero dropped
    [InlineData("""{"big":12345678901234567890}""")]                   // beyond long.MaxValue
    [InlineData("""{"esc":"a\/b"}""")]                                 // escape normalized away
    [InlineData("""{"unicode":"café"}""")]                        // \u escape decoded
    public void Preserves_scalars_that_a_parse_and_reserialize_would_rewrite(string body)
    {
        SmeeBodyReader.TryReadDelivery(Frame(body), out _, out var read).Should().BeTrue();

        read.Should().Be(body);
    }

    [Fact]
    public void Extracts_every_non_envelope_string_property_as_a_header()
    {
        SmeeBodyReader.TryReadDelivery(Frame(Signed), out var headers, out _).Should().BeTrue();

        headers!["x-github-event"].ToString().Should().Be("installation");
        headers["x-hub-signature-256"].ToString().Should().Be("sha256=abc");
        headers["host"].ToString().Should().Be("https://smee.io");
        headers.ContainsKey("body").Should().BeFalse();
        headers.ContainsKey("timestamp").Should().BeFalse();
    }

    [Fact]
    public void Header_lookup_is_case_insensitive()
    {
        SmeeBodyReader.TryReadDelivery(Frame(Signed), out var headers, out _).Should().BeTrue();

        // Octokit reads "X-GitHub-Event"; smee sends it lowercase.
        headers!["X-GitHub-Event"].ToString().Should().Be("installation");
    }

    [Theory]
    [InlineData("""{"ok":true}""")]                                              // no body, no event
    [InlineData("""{"body":{"a":1},"timestamp":1}""")]                           // body, no event header
    [InlineData("""{"x-github-event":"push","timestamp":1}""")]                  // event header, no body
    [InlineData("[1,2,3]")]                                                      // not an object
    [InlineData("not json at all")]                                              // smee ready/ping
    public void Rejects_frames_that_are_not_webhook_deliveries(string frame)
    {
        SmeeBodyReader.TryReadDelivery(frame, out var headers, out var body).Should().BeFalse();

        headers.Should().BeNull();
        body.Should().BeNull();
    }

    // --- LexicalMinify: ported from the CodeCoverage tunnel this replaced ------------------

    [Fact]
    public void Minify_reconstructs_the_signed_bytes_from_pretty_printed_json()
    {
        var prettyPrinted = """
            {
              "installation": {
                "id": 153131096,
                "created_at": "2026-08-12T08:45:12.000+02:00",
                "note": "a \"quoted\"  string with {braces} and \\n"
              },
              "count": 1.50
            }
            """;

        SmeeBodyReader.LexicalMinify(prettyPrinted).Should().Be(Signed);
    }

    [Fact]
    public void Minify_is_a_noop_on_already_minified_json()
    {
        SmeeBodyReader.LexicalMinify(Signed).Should().Be(Signed);
    }

    [Fact]
    public void Minify_preserves_whitespace_and_escaped_quotes_inside_strings()
    {
        SmeeBodyReader.LexicalMinify("""{ "a" : "x  \" y \\" }""")
            .Should().Be("""{"a":"x  \" y \\"}""");
    }
}
