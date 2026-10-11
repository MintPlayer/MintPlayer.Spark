using SeoTester.Checks;
using SeoTester.Html;
using SeoTester.Scoring;

namespace SeoTester.Tests.Checks;

/// <summary>The raw-HTML and header checks of one page (catalogue layers H and N; research §1).</summary>
public class PageChecksTests
{
    private const string GoodHead = """
        <!doctype html>
        <html lang="en-GB">
        <head>
          <meta charset="utf-8">
          <meta name="viewport" content="width=device-width, initial-scale=1">
          <title>Trail running shoes for every terrain – Example Shop</title>
          <meta name="description" content="Light, grippy trail running shoes for mud, rock and gravel. Free returns within 30 days and advice from runners who test every pair.">
          <link rel="canonical" href="https://example.com/shoes">
          <link rel="alternate" hreflang="en-GB" href="https://example.com/shoes">
          <link rel="alternate" hreflang="nl-BE" href="https://example.com/nl/schoenen">
          <link rel="alternate" hreflang="x-default" href="https://example.com/shoes">
          <meta name="theme-color" content="#4338ca">
          <meta property="og:title" content="Trail shoes"><meta property="og:description" content="Shoes">
          <meta property="og:image" content="https://example.com/og.png"><meta property="og:url" content="https://example.com/shoes"><meta property="og:type" content="website">
          <meta name="twitter:card" content="summary_large_image">
          <script type="application/ld+json">{"@context":"https://schema.org","@type":"Product","name":"Trail shoe","offers":{"@type":"Offer","price":"99"}}</script>
        </head>
        """;

    private static readonly Dictionary<string, string> GoodHeaders = new()
    {
        ["content-type"] = "text/html; charset=utf-8",
        ["strict-transport-security"] = "max-age=63072000; includeSubDomains",
        ["content-security-policy"] = "default-src 'self'; object-src 'none'; frame-ancestors 'self'",
        ["x-content-type-options"] = "nosniff",
        ["permissions-policy"] = "camera=()",
        ["referrer-policy"] = "strict-origin-when-cross-origin",
        ["etag"] = "\"abc\"",
        ["server"] = "nginx",
    };

    private static PageInput Page(string html, Dictionary<string, string>? headers = null, string url = "https://example.com/shoes",
        string? encoding = "br", bool inSitemap = false, bool linked = true, IReadOnlyList<string>? cookies = null)
        => new()
        {
            Url = new Uri(url),
            StatusCode = 200,
            Headers = headers ?? GoodHeaders,
            SetCookies = cookies ?? [],
            WireEncoding = encoding,
            BodyBytes = html.Length,
            Prefix = html.Length > 1024 ? html[..1024] : html,
            Html = HtmlPage.Parse(html),
            InSitemap = inSitemap,
            LinkedInternally = linked,
        };

    private static string Body(int words = 260, string extra = "")
        => "<body><h1>Trail running shoes</h1><main><p>" + string.Join(' ', Enumerable.Range(0, words).Select(i => "word" + i)) + "</p>"
           + "<a href=\"/socks\">Running socks for long trails</a>" + extra + "</main></body></html>";

    private static Dictionary<string, CheckStatus> Statuses(PageInput page) => PageChecks.Evaluate(page).ToDictionary(f => f.CheckId, f => f.Status);

    [Fact]
    public void A_well_made_page_passes_every_scored_check_it_can_measure()
    {
        var statuses = Statuses(Page(GoodHead + Body()));

        statuses.Where(s => s.Value is CheckStatus.Fail).Select(s => s.Key).Should().BeEmpty();
        foreach (var id in new[] { "seo.title-present", "seo.title-length", "seo.meta-description", "seo.single-h1", "seo.canonical", "seo.hreflang", "seo.html-lang",
                     "seo.structured-data", "seo.thin-content", "social.open-graph", "social.twitter-card", "performance.compression", "performance.validators",
                     "security.hsts", "security.csp", "security.nosniff", "security.clickjacking", "security.server-signature", "mobile.viewport",
                     "mobile.zoom-disabled", "mobile.charset", "mobile.theme-color", "seo.link-text" })
            statuses[id].Should().Be(CheckStatus.Pass, id);
        statuses.Keys.Should().BeSubsetOf(PageChecks.Ids);
    }

    [Fact]
    public void A_neglected_page_fails_the_checks_that_describe_it()
    {
        var html = """
            <html><head><meta name="viewport" content="width=device-width, user-scalable=no"><meta name="robots" content="noindex">
            <meta name="keywords" content="shoes"><meta name="apple-mobile-web-app-capable" content="yes"></head>
            <body><h1>A</h1><h1>B</h1><img src="/a.png"><form method="post"><input name="q"></form>
            <a href="/x">click here</a><a href="/y"></a><script src="http://cdn.example.org/lib.js"></script>
            <a href="https://www.facebook.com/sharer/sharer.php?u=x">Share</a></body></html>
            """;
        var headers = new Dictionary<string, string>
        {
            ["content-type"] = "text/html",
            ["server"] = "Apache/2.4.1",
            ["x-powered-by"] = "PHP/8.1",
            ["x-xss-protection"] = "1; mode=block",
            ["expect-ct"] = "max-age=0",
            ["content-security-policy"] = "script-src 'self' 'unsafe-inline' 'unsafe-eval'",
        };

        var statuses = Statuses(Page(html, headers, url: "https://example.com/Old_Page?sessionid=1", encoding: null, inSitemap: true,
            cookies: ["session=abc; Path=/"]));

        statuses["seo.title-present"].Should().Be(CheckStatus.Fail);
        statuses["seo.meta-description"].Should().Be(CheckStatus.Fail);
        statuses["seo.single-h1"].Should().Be(CheckStatus.Warn);
        statuses["seo.html-lang"].Should().Be(CheckStatus.Fail);
        statuses["seo.noindex-conflict"].Should().Be(CheckStatus.Fail, "noindex but listed in the sitemap");
        statuses["seo.link-text"].Should().Be(CheckStatus.Warn);
        statuses["seo.url-hygiene"].Should().Be(CheckStatus.Warn);
        statuses["seo.meta-keywords"].Should().Be(CheckStatus.Warn);
        statuses["social.open-graph"].Should().Be(CheckStatus.Fail);
        statuses["social.share-buttons"].Should().Be(CheckStatus.Warn);
        statuses["security.hsts"].Should().Be(CheckStatus.Fail);
        statuses["security.mixed-content"].Should().Be(CheckStatus.Fail, "an http:// script on an https page is active mixed content");
        statuses["security.csp"].Should().Be(CheckStatus.Warn);
        statuses["security.nosniff"].Should().Be(CheckStatus.Fail);
        statuses["security.clickjacking"].Should().Be(CheckStatus.Fail);
        statuses["security.server-signature"].Should().Be(CheckStatus.Warn);
        statuses["security.x-xss-protection"].Should().Be(CheckStatus.Warn);
        statuses["security.obsolete-headers"].Should().Be(CheckStatus.Warn);
        statuses["security.sri"].Should().Be(CheckStatus.Warn);
        statuses["security.csrf-heuristic"].Should().Be(CheckStatus.Warn);
        statuses["security.cookie-flags"].Should().Be(CheckStatus.Warn);
        statuses["accessibility.img-alt"].Should().Be(CheckStatus.Fail);
        statuses["accessibility.form-labels"].Should().Be(CheckStatus.Fail);
        statuses["mobile.zoom-disabled"].Should().Be(CheckStatus.Fail);
        statuses["mobile.charset"].Should().Be(CheckStatus.Fail);
        statuses["mobile.apple-web-app"].Should().Be(CheckStatus.Warn);
        statuses["performance.validators"].Should().Be(CheckStatus.Warn);
    }

    [Fact]
    public void A_long_title_is_measured_in_pixels_as_well_as_characters()
    {
        // 55 characters, but capital W and M are wide: Arial 20 px puts it past Google's ~600 px.
        var wide = new string('W', 30) + " " + new string('M', 24);
        TitleWidth.Pixels(wide).Should().BeGreaterThan(TitleWidth.TruncationPx);
        TitleWidth.Pixels("Trail running shoes for every terrain – Example Shop").Should().BeLessThan(TitleWidth.TruncationPx);

        var statuses = Statuses(Page(GoodHead.Replace("Trail running shoes for every terrain – Example Shop", wide) + Body()));
        statuses["seo.title-length"].Should().Be(CheckStatus.Warn);
    }

    [Fact]
    public void Structured_data_reports_invalid_json_and_missing_required_properties()
    {
        var invalid = GoodHead.Replace("\"offers\":{\"@type\":\"Offer\",\"price\":\"99\"}}", "\"offers\":}");
        var missing = GoodHead.Replace(",\"offers\":{\"@type\":\"Offer\",\"price\":\"99\"}", "");

        Statuses(Page(invalid + Body()))["seo.structured-data"].Should().Be(CheckStatus.Fail);
        var finding = PageChecks.Evaluate(Page(missing + Body())).Single(f => f.CheckId == "seo.structured-data");
        finding.Status.Should().Be(CheckStatus.Warn);
        finding.Detail!["value"].Should().Contain("Product is missing offers");
    }

    [Fact]
    public void Hreflang_needs_valid_codes_and_a_self_reference()
    {
        var noSelf = GoodHead.Replace("<link rel=\"alternate\" hreflang=\"en-GB\" href=\"https://example.com/shoes\">", "")
            .Replace("hreflang=\"x-default\" href=\"https://example.com/shoes\"", "hreflang=\"x-default\" href=\"https://example.com/\"");
        var badCode = GoodHead.Replace("hreflang=\"nl-BE\"", "hreflang=\"dutch\"");

        Statuses(Page(noSelf + Body()))["seo.hreflang"].Should().Be(CheckStatus.Fail);
        Statuses(Page(badCode + Body()))["seo.hreflang"].Should().Be(CheckStatus.Fail);
        PageChecks.IsLanguageTag("zh-Hant-TW").Should().BeTrue();
        PageChecks.IsLanguageTag("english").Should().BeFalse();
    }

    [Fact]
    public void A_page_that_is_not_a_200_html_page_gets_no_page_findings()
    {
        var page = Page(GoodHead + Body());
        PageChecks.Evaluate(new PageInput { Url = page.Url, StatusCode = 404, Headers = page.Headers, Html = page.Html }).Should().BeEmpty();
        PageChecks.Evaluate(new PageInput { Url = page.Url, StatusCode = 200, Headers = new Dictionary<string, string> { ["content-type"] = "application/pdf" } }).Should().BeEmpty();
    }

    [Fact]
    public void The_csp_evaluation_flags_what_csp_evaluator_flags()
    {
        PageChecks.CspWeakness("default-src 'self'; object-src 'none'").Should().BeNull();
        PageChecks.CspWeakness("script-src 'self' 'unsafe-inline' 'nonce-abc'; object-src 'none'").Should().BeNull("a nonce makes 'unsafe-inline' ignored by browsers");
        PageChecks.CspWeakness("script-src *; object-src 'none'").Should().Contain("any host");
        PageChecks.CspWeakness("img-src 'self'").Should().Contain("no script-src");
        PageChecks.CspWeakness("default-src 'self'").Should().Contain("object-src");
    }

    [Fact]
    public void Untrusted_markup_is_parsed_without_running_scripts_and_huge_pages_are_cut()
    {
        var html = "<html><head><title>x</title><script>throw new Error('ran')</script></head><body>" + new string('a', HtmlPage.MaxCharacters + 10) + "</body></html>";

        var page = HtmlPage.Parse(html);

        page.Truncated.Should().BeTrue();
        page.Title.Should().Be("x");
    }

    [Fact]
    public void A_root_relative_og_image_or_canonical_is_not_absolute_on_any_os()
    {
        // On Linux, Uri.TryCreate(UriKind.Absolute) accepts "/x" as file:///x, so both used to pass there.
        var head = GoodHead
            .Replace("<link rel=\"canonical\" href=\"https://example.com/shoes\">", "<link rel=\"canonical\" href=\"/shoes\">")
            .Replace("content=\"https://example.com/og.png\"", "content=\"/og.png\"");
        head.Should().Contain("href=\"/shoes\"").And.Contain("content=\"/og.png\"");

        var findings = PageChecks.Evaluate(Page(head + Body())).ToDictionary(f => f.CheckId);

        findings["seo.canonical"].Status.Should().Be(CheckStatus.Warn);
        findings["social.open-graph"].Status.Should().NotBe(CheckStatus.Pass);
    }
}
