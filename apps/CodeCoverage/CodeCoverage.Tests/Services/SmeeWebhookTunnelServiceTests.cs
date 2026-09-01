using Xunit;
using CodeCoverage.Services;

namespace CodeCoverage.Tests.Services;

public class SmeeWebhookTunnelServiceTests
{
    // The bytes GitHub signed: minified, fractional-second timestamp (the
    // installation-event format), a trailing-zero decimal, escapes and
    // whitespace inside strings.
    private const string Signed =
        """{"installation":{"id":153131096,"created_at":"2026-08-12T08:45:12.000+02:00","note":"a \"quoted\"  string with {braces} and \\n"},"count":1.50}""";

    [Fact]
    public void Minify_reconstructs_the_signed_bytes_from_pretty_printed_json()
    {
        // What smee relays: same tokens, whitespace added (JS pretty-print).
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

        SmeeWebhookTunnelService.LexicalMinify(prettyPrinted).Should().Be(Signed);
    }

    [Fact]
    public void Minify_is_a_noop_on_already_minified_json()
    {
        SmeeWebhookTunnelService.LexicalMinify(Signed).Should().Be(Signed);
    }

    [Fact]
    public void Minify_preserves_whitespace_and_escaped_quotes_inside_strings()
    {
        SmeeWebhookTunnelService.LexicalMinify("""{ "a" : "x  \" y \\" }""")
            .Should().Be("""{"a":"x  \" y \\"}""");
    }
}
