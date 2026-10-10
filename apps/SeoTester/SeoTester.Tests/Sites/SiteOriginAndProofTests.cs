using SeoTester.Sites;

namespace SeoTester.Tests.Sites;

/// <summary>
/// What a user types becomes one canonical origin (F2), and the ownership evidence read from DNS and the site (F3) is
/// matched exactly: everything there is untrusted.
/// </summary>
public class SiteOriginAndProofTests
{
    private const string Token = "0123456789abcdef0123456789abcdef";

    [Theory]
    [InlineData("example.com", "https://example.com", "example.com")]
    [InlineData("  HTTPS://Example.COM./some/path?q=1#x ", "https://example.com", "example.com")]
    [InlineData("http://www.example.org:80/", "http://www.example.org", "www.example.org")]
    [InlineData("https://example.org:8443", "https://example.org:8443", "example.org")]
    [InlineData("https://bücher.example", "https://xn--bcher-kva.example", "xn--bcher-kva.example")]
    public void An_address_becomes_its_origin(string input, string origin, string host)
    {
        SiteOrigin.TryNormalize(input, out var actualOrigin, out var actualHost, out var problem).Should().BeTrue(problem);
        actualOrigin.Should().Be(origin);
        actualHost.Should().Be(host);
    }

    [Theory]
    [InlineData("")]
    [InlineData("ftp://example.com")]
    [InlineData("https://user:secret@example.com")]
    [InlineData("https://127.0.0.1")]
    [InlineData("https://[::1]/")]
    [InlineData("http://localhost")]
    [InlineData("https://exa_mple.com")]
    [InlineData("javascript:alert(1)")]
    public void What_is_not_a_public_website_is_refused(string input)
    {
        SiteOrigin.TryNormalize(input, out _, out _, out var problem).Should().BeFalse();
        problem.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void A_TXT_record_proves_only_with_the_exact_value()
    {
        OwnershipProof.MatchesTxt(["v=spf1 -all", " seotester-site-verification=" + Token + " "], Token).Should().BeTrue();
        OwnershipProof.MatchesTxt(["seotester-site-verification=" + Token.ToUpperInvariant()], Token).Should().BeFalse();
        OwnershipProof.MatchesTxt(["seotester-site-verification=" + Token + "x"], Token).Should().BeFalse();
        OwnershipProof.MatchesTxt(["seotester-site-verification="], "").Should().BeFalse("an empty token proves nothing");
        OwnershipProof.RecordName("www.example.com").Should().Be("_seotester.www.example.com");
    }

    [Fact]
    public void The_well_known_file_needs_the_line()
    {
        OwnershipProof.MatchesWellKnown("# SeoTester\r\nseotester-site-verification=" + Token + "\r\n", Token).Should().BeTrue();
        OwnershipProof.MatchesWellKnown("You asked for /.well-known/seotester-verification.txt?" + Token, Token).Should().BeFalse("a page echoing the request proves nothing");
        OwnershipProof.MatchesWellKnown(null, Token).Should().BeFalse();
    }

    [Fact]
    public void The_meta_tag_counts_only_in_the_head_and_not_in_a_comment()
    {
        OwnershipProof.MatchesMeta($"<html><head><title>x</title><META content='{Token}' NAME=\"SeoTester-Site-Verification\"></head><body></body></html>", Token).Should().BeTrue();
        OwnershipProof.MatchesMeta($"<html><head><meta name=seotester-site-verification content={Token} /></head></html>", Token).Should().BeTrue();
        OwnershipProof.MatchesMeta($"<html><head></head><body><meta name=\"seotester-site-verification\" content=\"{Token}\"></body></html>", Token)
            .Should().BeFalse("a tag in the body may be a visitor's comment");
        OwnershipProof.MatchesMeta($"<html><head><!-- <meta name=\"seotester-site-verification\" content=\"{Token}\"> --></head></html>", Token).Should().BeFalse();
        OwnershipProof.MatchesMeta($"<html><head><meta name=\"description\" content=\"{Token}\"></head></html>", Token).Should().BeFalse();
        OwnershipProof.MatchesMeta("<head>" + new string('<', 300_000), Token).Should().BeFalse("a hostile page neither matches nor stalls");
    }
}
