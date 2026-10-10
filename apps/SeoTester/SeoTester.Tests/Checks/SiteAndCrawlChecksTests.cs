using SeoTester.Catalogue;
using SeoTester.Checks;
using SeoTester.Crawling;
using SeoTester.Scoring;
using SeoTester.Tls;

namespace SeoTester.Tests.Checks;

/// <summary>The probe's site-level checks and the aggregate's crawl-level checks, on plain facts (no I/O).</summary>
public class SiteAndCrawlChecksTests
{
    private static readonly DateTime Now = new(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc);

    private static SiteFacts Healthy(bool intrusive = false) => new()
    {
        Home = new Uri("https://example.com/"),
        NowUtc = Now,
        Intrusive = intrusive,
        Robots = new RobotsFacts(RobotsAvailability.Available, 200, RobotsTxt.Parse("User-agent: *\nDisallow: /admin\nSitemap: https://example.com/sitemap.xml", SeoTesterBot.RobotsToken), 60),
        Sitemap = new SitemapFacts("https://example.com/sitemap.xml", 200, null, 12, false, 0, true),
        Tls = new TlsFacts(true, null, "Tls13", "h2", Now.AddDays(70), null, "R11 Let's Encrypt", LegacyTlsAnswer.Refused, LegacyTlsAnswer.Refused, true),
        HttpToHttps = new RedirectFacts("http://example.com/", [("http://example.com/", 301)], "https://example.com/", 200, null),
        WwwVariant = new RedirectFacts("https://www.example.com/", [("https://www.example.com/", 301)], "https://example.com/", 200, null),
        HomeAltSvc = "h3=\":443\"; ma=86400",
        SecurityTxt = new FetchFacts("https://example.com/.well-known/security.txt", 200, "text/plain; charset=utf-8", "Contact: mailto:security@example.com\nExpires: 2027-01-01T00:00:00Z\n", null, null),
        Favicon = new FetchFacts("https://example.com/favicon.ico", 200, "image/x-icon", null, null, null),
        NotFoundProbe = new FetchFacts("https://example.com/x", 404, "text/html", null, null, null),
        Manifest = new ManifestFacts("https://example.com/manifest.webmanifest", 200, []),
        HasIconLink = true,
        HasAppleTouchIcon = true,
        Css = new CssFacts(2, 0, true, true),
        Assets = [new AssetFacts("https://example.com/app.css", "css", 200, "br", "public, max-age=31536000, immutable", 40_000, true, 0)],
        Dns = new DnsFacts("example.com", ["0 issue \"letsencrypt.org\""], true, ["v=spf1 include:_spf.example.com -all"], ["v=DMARC1; p=reject"],
            ["v=STSv1; id=1"], true, ["2001:db8::1"], ["ns1.example.net", "ns2.example.org"], ["192.0.2.1", "198.51.100.1"]),
        Rdap = new RdapFacts("found", Now.AddDays(400), "rdap.verisign.com"),
        HstsPreloadStatus = "preloaded",
    };

    private static Dictionary<string, CheckStatus> Statuses(SiteFacts facts) => SiteChecks.Evaluate(facts).ToDictionary(f => f.CheckId, f => f.Status);

    [Fact]
    public void A_healthy_site_passes_every_scored_site_check()
    {
        var statuses = Statuses(Healthy());

        statuses.Where(s => s.Value is CheckStatus.Fail or CheckStatus.Error).Select(s => s.Key).Should().BeEmpty();
        statuses["security.https"].Should().Be(CheckStatus.Pass);
        statuses["security.http-redirect"].Should().Be(CheckStatus.Pass);
        statuses["domain.caa"].Should().Be(CheckStatus.Pass, "letsencrypt.org allows the Let's Encrypt certificate");
        statuses["domain.www-apex"].Should().Be(CheckStatus.Pass);
        statuses["security.exposed-files"].Should().Be(CheckStatus.NotApplicable, "intrusive probes were not granted");
        statuses.Keys.Should().BeSubsetOf(SiteChecks.Ids);
    }

    [Fact]
    public void Each_site_problem_turns_its_check_red()
    {
        var facts = Healthy(intrusive: true);
        facts.Robots = new RobotsFacts(RobotsAvailability.Available, 200, RobotsTxt.Parse("User-agent: *\nDisallow: /", SeoTesterBot.RobotsToken), 20);
        facts.Tls = facts.Tls! with { CertificateNotAfter = Now.AddDays(5), Tls10 = LegacyTlsAnswer.Accepted, Alpn = "http/1.1", Protocol = "Tls12", Tls13 = false };
        facts.HttpToHttps = new RedirectFacts("http://example.com/", [("http://example.com/", 302), ("http://example.com/home", 301)], "https://example.com/home", 200, null);
        facts.SecurityTxt = facts.SecurityTxt! with { Body = "Contact: mailto:x@example.com\nExpires: 2025-01-01T00:00:00Z" };
        facts.NotFoundProbe = facts.NotFoundProbe! with { Status = 200 };
        facts.ExposedFiles = [new FetchFacts("https://example.com/.git/HEAD", 200, "text/plain", "ref: refs/heads/main\n", null, null)];
        facts.Directories = [new FetchFacts("https://example.com/images/", 200, "text/html", "<html><head><title>Index of /images</title></head></html>", null, null)];
        facts.Dns = facts.Dns! with { Caa = ["0 issue \"digicert.com\""], ApexTxt = ["v=spf1 +all"], DmarcTxt = [] };
        facts.Rdap = new RdapFacts("found", Now.AddDays(10), "rdap.example");
        facts.WwwVariant = new RedirectFacts("https://www.example.com/", [], null, null, "does not resolve");

        var statuses = Statuses(facts);

        statuses["technical.robots-blocks-all"].Should().Be(CheckStatus.Fail);
        statuses["security.certificate-expiry"].Should().Be(CheckStatus.Fail);
        statuses["security.tls-legacy"].Should().Be(CheckStatus.Fail);
        statuses["security.tls13"].Should().Be(CheckStatus.Warn);
        statuses["performance.http2"].Should().Be(CheckStatus.Fail);
        statuses["security.http-redirect"].Should().Be(CheckStatus.Fail, "302 first, and two hops");
        statuses["security.security-txt"].Should().Be(CheckStatus.Warn);
        statuses["technical.soft-404"].Should().Be(CheckStatus.Fail);
        statuses["security.exposed-files"].Should().Be(CheckStatus.Fail);
        statuses["security.directory-listing"].Should().Be(CheckStatus.Fail);
        statuses["domain.caa"].Should().Be(CheckStatus.Fail, "CAA allows only DigiCert but Let's Encrypt issued the certificate");
        statuses["domain.spf"].Should().Be(CheckStatus.Fail);
        statuses["domain.dmarc"].Should().Be(CheckStatus.Fail);
        statuses["domain.expiry"].Should().Be(CheckStatus.Fail);
        statuses["domain.www-apex"].Should().Be(CheckStatus.Fail);
    }

    [Fact]
    public void Unmeasured_things_are_errors_or_not_applicable_never_failures()
    {
        var facts = new SiteFacts { Home = new Uri("https://example.be/"), NowUtc = Now, Rdap = new RdapFacts("no-rdap", null, null) };

        var findings = SiteChecks.Evaluate(facts);

        findings.Single(f => f.CheckId == "domain.expiry").Status.Should().Be(CheckStatus.NotApplicable, ".be publishes no expiry (S4)");
        findings.Where(f => f.CheckId.StartsWith("domain.", StringComparison.Ordinal) && f.CheckId != "domain.expiry")
            .Should().OnlyContain(f => f.Status == CheckStatus.Error);
        findings.Single(f => f.CheckId == "security.https").Status.Should().Be(CheckStatus.Error);
    }

    private static CrawledPage P(string path, int status = 200, int depth = 1, string? title = null, string? description = null, int hops = 0, int? firstHop = null,
        string? canonical = null, string[]? alternates = null, bool inSitemap = false, bool linked = true, ulong? print = null)
        => new("https://example.com" + path, status, depth, title ?? "Title " + path, description ?? "Description " + path, hops, firstHop, null,
            canonical, alternates ?? [], "https://example.com/", inSitemap, linked, false, print ?? (ulong)path.GetHashCode() * 0x9E3779B97F4A7C15UL);

    [Fact]
    public void The_crawl_level_checks_judge_each_url_concerned()
    {
        var pages = new[]
        {
            P("/", depth: 0, linked: false, inSitemap: true),
            P("/a", title: "Same", description: "Same"),
            P("/b", title: "Same", description: "Same"),
            P("/broken", status: 404),
            P("/moved", hops: 2, firstHop: 301),
            P("/temp", hops: 1, firstHop: 302),
            P("/deep", depth: 5),
            P("/en", alternates: ["https://example.com/en", "https://example.com/nl"]),
            P("/nl", alternates: ["https://example.com/nl"]),
            P("/canon", canonical: "https://example.com/broken"),
            P("/copy1", print: 0xF0F0F0F0F0F0F0F0UL),
            P("/copy2", print: 0xF0F0F0F0F0F0F0F1UL),
        };

        var findings = CrawlChecks.Evaluate(pages, ["https://example.com/", "https://example.com/orphan"], null)
            .GroupBy(f => f.Finding.CheckId).ToDictionary(g => g.Key, g => g.Where(f => f.Finding.Status != CheckStatus.Pass).Select(f => f.Url).ToList());

        findings["seo.duplicate-titles"].Should().BeEquivalentTo(["https://example.com/a", "https://example.com/b"]);
        findings["seo.duplicate-descriptions"].Should().HaveCount(2);
        findings["technical.broken-internal-links"].Should().Equal("https://example.com/broken");
        findings["technical.redirect-chains"].Should().BeEquivalentTo(["https://example.com/moved", "https://example.com/temp"]);
        findings["technical.crawl-depth"].Should().Equal("https://example.com/deep");
        findings["seo.hreflang"].Should().Equal("https://example.com/en"); // /nl does not link back to /en
        findings["seo.canonical"].Should().Equal("https://example.com/canon");
        findings["technical.orphan-pages"].Should().BeEquivalentTo(["https://example.com/", "https://example.com/orphan"]);
        findings["technical.duplicate-content"].Should().BeEquivalentTo(["https://example.com/copy1", "https://example.com/copy2"]);
        findings["technical.broken-external-links"].Should().Equal((string?)null); // not checked: N/A for the site
    }

    [Fact]
    public void Near_identical_text_gets_a_near_identical_fingerprint()
    {
        var text = string.Join(' ', Enumerable.Range(0, 300).Select(i => "word" + (i % 97)));
        var other = string.Join(' ', Enumerable.Range(0, 300).Select(i => "other" + i));

        System.Numerics.BitOperations.PopCount(CrawlChecks.Fingerprint(text) ^ CrawlChecks.Fingerprint(text)).Should().Be(0);
        System.Numerics.BitOperations.PopCount(CrawlChecks.Fingerprint(text) ^ CrawlChecks.Fingerprint(other)).Should().BeGreaterThan(10);
        CrawlChecks.Fingerprint("too short").Should().Be(0UL);
    }

    [Fact]
    public void The_tally_keeps_the_worst_vote_per_url_and_samples_the_failures()
    {
        var tally = new RunTally();
        tally.Add("https://example.com/a", Finding.Pass("seo.canonical"));
        tally.Add("https://example.com/a", Finding.Fail("seo.canonical", ("target", "x")));
        tally.Add("https://example.com/b", Finding.Pass("seo.canonical"));
        tally.Add(null, Finding.Warn("security.tls13"));
        tally.Add("https://example.com/a", Finding.Fail("not.a-check"));

        var tallies = tally.Tallies();

        tallies["seo.canonical"].Outcome.Should().Be(new CheckOutcome("seo.canonical", 1, 0, 1));
        tallies["seo.canonical"].SampleUrls.Should().Equal("https://example.com/a");
        tallies["seo.canonical"].Detail!["target"].Should().Be("x");
        tallies["security.tls13"].Outcome.Warn.Should().Be(1);
        tallies.Should().NotContainKey("not.a-check", "an implementation without a catalogue entry is not run");
    }

    [Fact]
    public void Every_catalogue_check_is_implemented_now_or_waits_for_the_renderer_and_psi()
    {
        var catalogue = ShippedCatalogue.All.Select(c => c.Id).ToHashSet();

        CheckImplementations.Implemented.Should().BeSubsetOf(catalogue);
        CheckImplementations.AwaitingRenderOrPsi.Should().BeSubsetOf(catalogue);
        CheckImplementations.Implemented.Intersect(CheckImplementations.AwaitingRenderOrPsi).Should().BeEmpty();
        catalogue.Except(CheckImplementations.Implemented).Except(CheckImplementations.AwaitingRenderOrPsi).Should().BeEmpty();
        CheckImplementations.Implemented.Count.Should().Be(92);
        CheckImplementations.AwaitingRenderOrPsi.Count.Should().Be(18);
    }
}
