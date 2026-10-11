using System.Net;
using SeoTester.Entities;
using SeoTester.Html;
using SeoTester.LookupReferences;
using SeoTester.Settings;
using SeoTester.Tests._Infrastructure;
using static SeoTester.Tests._Infrastructure.SeoApi;

namespace SeoTester.Tests.Crawler;

/// <summary>
/// The shared-link card (<c>docs/seo_analyzer_PRD.md</c> Q-M11d): the crawl keeps a page's <c>og:title</c>,
/// <c>og:description</c>, <c>og:image</c> and <c>og:url</c> on its <see cref="PageResult"/>, and the card's image is
/// served only by <c>/seotester/og-image</c>: fetched by the server over the SSRF-safe crawler client, to members who
/// may read the page, and only a raster image of at most 5 MB.
/// </summary>
[Collection(SeoPipelineHostCollection.Name)]
public class OpenGraphCardTests(SeoPipelineHostFixture host)
{
    /// <summary>A 1×1 transparent PNG: IHDR, IDAT, IEND.</summary>
    private static readonly byte[] Png = Convert.FromHexString(
        "89504E470D0A1A0A0000000D4948445200000001000000010806000000" + "1F15C489" + "0000000A49444154789C6300010000050001" + "0D0A2DB4" + "0000000049454E44AE426082");

    /// <summary>A 1×1 GIF ending in its trailer (0x3B).</summary>
    private static readonly byte[] Gif = Convert.FromHexString("47494638396101000100800000FFFFFF00000021F90401000000002C00000000010001000002024401003B");

    /// <summary>A JPEG's frame: SOI, a JFIF APP0 segment, EOI.</summary>
    private static readonly byte[] Jpeg = Convert.FromHexString("FFD8FFE000104A46494600010100000100010000FFD9");

    /// <summary>A WebP: the RIFF header with the exact size and one VP8L chunk.</summary>
    private static readonly byte[] Webp = [.. "RIFF"u8, 18, 0, 0, 0, .. "WEBP"u8, .. "VP8L"u8, 6, 0, 0, 0, 0x2F, 0, 0, 0, 0, 0];

    /// <summary>A PNG signature with an HTML document behind it (a polyglot: an image to a sniffer, a page to a browser).</summary>
    private static readonly byte[] Polyglot = [.. Png[..8], .. "<html><body><script>alert(document.domain)</script></body></html>"u8];

    /// <summary>A well-formed PNG whose text chunk carries a script.</summary>
    private static readonly byte[] PngWithScript = [.. Png[..^12], 0, 0, 0, 27, .. "tEXt"u8, .. "c\0<script>alert(1)</script>"u8, 0, 0, 0, 0, .. Png[^12..]];

    private static string OgPage(string title, string image, string? link = null)
        => FakeSite.Page(title, link is null ? [] : [link]).Replace("</head>",
            $"""<meta property="og:title" content=" {title} card "><meta property="og:description" content="Shared {title}"><meta property="og:image" content="{image}"><meta property="og:url" content="https://www.example.com/{title}"></head>""");

    [Fact]
    public async Task The_crawl_keeps_the_open_graph_values_and_the_image_is_served_through_the_server_only()
    {
        var h = FakeSite.NewHost();
        var o = "https://" + h;
        var images = "https://img" + Guid.NewGuid().ToString("N")[..8] + ".example.net";
        host.Web.Page(o + "/robots.txt", "User-agent: *\nAllow: /\n");
        host.Web.Html(o + "/", OgPage("home", images + "/card.png", "/svg"));
        host.Web.Html(o + "/svg", OgPage("svg", images + "/card.svg", "/huge"));
        host.Web.Html(o + "/huge", OgPage("huge", images + "/huge.png"));
        host.Web.Pages[images + "/card.png"] = new FakeResponse(HttpStatusCode.OK, "", ContentType: "image/png", Bytes: Png);
        host.Web.Pages[images + "/card.svg"] = new FakeResponse(HttpStatusCode.OK, "<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>", ContentType: "image/png");
        host.Web.Pages[images + "/huge.png"] = new FakeResponse(HttpStatusCode.OK, "", ContentType: "image/png",
            Bytes: [.. Png, .. new byte[OgImageRules.MaxBytes]]);
        FakeSite.Registries(host.Web);
        var site = await SeedSiteAsync(host, Acme, hostName: h);

        await SetSettingAsync(host, SeoQuotas.Audits.Limit.Key, "-1");
        var (status, body, raw) = await ActionAsync(host, SiteTypeId, "Site", "StartAudit", site.Id!, Max);
        status.Should().Be(HttpStatusCode.OK, raw);
        var runId = body.GetProperty("result").GetProperty("runId").GetString()!;
        (await FakeSite.FinishedAsync(host.Database, runId)).Status.Should().Be(AuditRunStatus.Completed);

        var home = (await LoadDocumentAsync<PageResult>(host, PageResult.IdFor(runId, o + "/")))!;
        home.OgTitle.Should().Be("home card");
        home.OgDescription.Should().Be("Shared home");
        home.OgImage.Should().Be(images + "/card.png");
        home.OgUrl.Should().Be("https://www.example.com/home");
        host.Web.RequestsTo(new Uri(images).Host).Should().BeEmpty("the crawl stores the address, it does not fetch the image");

        using var member = await GetAsync(PageResult.IdFor(runId, o + "/"), Max);
        member.StatusCode.Should().Be(HttpStatusCode.OK);
        member.Content.Headers.ContentType!.MediaType.Should().Be("image/png");
        member.Headers.GetValues("X-Content-Type-Options").Should().Equal("nosniff");
        member.Headers.GetValues("Content-Security-Policy").Single().Should().Contain("sandbox");
        (await member.Content.ReadAsByteArrayAsync()).Should().Equal(Png);
        host.Web.RequestsTo(new Uri(images).Host).Should().OnlyContain(r => r.UserAgent == SeoTesterBot.UserAgent);

        using var outsider = await GetAsync(PageResult.IdFor(runId, o + "/"), Bob);
        using var anonymous = await GetAsync(PageResult.IdFor(runId, o + "/"), null);
        using var svg = await GetAsync(PageResult.IdFor(runId, o + "/svg"), Max);
        using var huge = await GetAsync(PageResult.IdFor(runId, o + "/huge"), Max);
        outsider.StatusCode.Should().Be(HttpStatusCode.NotFound, "the page is Acme's (#453)");
        anonymous.StatusCode.Should().Be(HttpStatusCode.NotFound);
        svg.StatusCode.Should().Be(HttpStatusCode.NotFound, "only PNG, JPEG, GIF and WebP by their bytes, whatever Content-Type says");
        huge.StatusCode.Should().Be(HttpStatusCode.NotFound, "an image over 5 MB is refused, not cut");
    }

    /// <summary>
    /// The proxy serves only what it decided is a raster image (<c>docs/seo_analyzer_PRD.md</c> §12, "Untrusted content:
    /// proof tests"): HTML, SVG (as itself and as <c>image/png</c>), a PNG-signature polyglot, a PNG with a script in a
    /// text chunk and an oversized image are refused (the one 404); every image it serves carries <c>nosniff</c>, the
    /// sandboxing CSP and the <c>Content-Type</c> of its bytes, never the one the site sent.
    /// </summary>
    [Fact]
    public async Task The_image_proxy_serves_only_raster_images_typed_by_their_bytes()
    {
        var h = FakeSite.NewHost();
        var o = "https://" + h;
        var images = "https://img" + Guid.NewGuid().ToString("N")[..8] + ".example.net";
        var refused = new Dictionary<string, (byte[] Bytes, string ContentType)>
        {
            ["html"] = ("<!doctype html><html><body><script>alert(1)</script></body></html>"u8.ToArray(), "text/html"),
            ["svg"] = ("<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>"u8.ToArray(), "image/svg+xml"),
            ["svgaspng"] = ("<svg xmlns=\"http://www.w3.org/2000/svg\" onload=\"alert(1)\"/>"u8.ToArray(), "image/png"),
            ["polyglot"] = (Polyglot, "image/png"),
            ["scriptchunk"] = (PngWithScript, "image/png"),
            ["oversized"] = ([.. Png[..^12], .. new byte[OgImageRules.MaxBytes], .. Png[^12..]], "image/png"),
        };
        var accepted = new Dictionary<string, (byte[] Bytes, string ContentType, string Served)>
        {
            ["pngashtml"] = (Png, "text/html", "image/png"),
            ["gifaspng"] = (Gif, "image/png", "image/gif"),
            ["jpegassvg"] = (Jpeg, "image/svg+xml", "image/jpeg"),
            ["webpasoctet"] = (Webp, "application/octet-stream", "image/webp"),
        };
        var names = refused.Keys.Concat(accepted.Keys).ToList();
        host.Web.Page(o + "/robots.txt", "User-agent: *\nAllow: /\n");
        host.Web.Html(o + "/", FakeSite.Page("home", names.Select(n => "/" + n).ToArray()));
        foreach (var name in names)
        {
            var (bytes, type) = refused.TryGetValue(name, out var r) ? r : (accepted[name].Bytes, accepted[name].ContentType);
            host.Web.Html(o + "/" + name, OgPage(name, $"{images}/{name}", "/"));
            host.Web.Pages[$"{images}/{name}"] = new FakeResponse(HttpStatusCode.OK, "", ContentType: type, Bytes: bytes);
        }
        FakeSite.Registries(host.Web);
        var site = await SeedSiteAsync(host, Acme, hostName: h);
        await SetSettingAsync(host, SeoQuotas.Audits.Limit.Key, "-1");
        var (status, body, raw) = await ActionAsync(host, SiteTypeId, "Site", "StartAudit", site.Id!, Max);
        status.Should().Be(HttpStatusCode.OK, raw);
        var runId = body.GetProperty("result").GetProperty("runId").GetString()!;
        (await FakeSite.FinishedAsync(host.Database, runId)).Status.Should().Be(AuditRunStatus.Completed);

        foreach (var name in refused.Keys)
        {
            using var response = await GetAsync(PageResult.IdFor(runId, o + "/" + name), Max);
            response.StatusCode.Should().Be(HttpStatusCode.NotFound, name);
        }
        foreach (var (name, (bytes, _, served)) in accepted)
        {
            using var response = await GetAsync(PageResult.IdFor(runId, o + "/" + name), Max);
            response.StatusCode.Should().Be(HttpStatusCode.OK, name);
            response.Content.Headers.ContentType!.ToString().Should().Be(served, name);
            response.Headers.GetValues("X-Content-Type-Options").Should().Equal("nosniff");
            response.Headers.GetValues("Content-Security-Policy").Single().Should().Be("default-src 'none'; sandbox");
            (await response.Content.ReadAsByteArrayAsync()).Should().Equal(bytes);
        }
        host.Web.RequestsTo(new Uri(images).Host).Should().HaveCount(names.Count, "every image was really fetched (so a 404 is the proxy's refusal)");
    }

    [Fact]
    public void Only_well_formed_raster_images_without_markup_are_recognised()
    {
        OgImageRules.RasterType(Png).Should().Be("image/png");
        OgImageRules.RasterType(Gif).Should().Be("image/gif");
        OgImageRules.RasterType(Jpeg).Should().Be("image/jpeg");
        OgImageRules.RasterType(Webp).Should().Be("image/webp");
        OgImageRules.RasterType(Polyglot).Should().BeNull("a PNG signature with a page behind it");
        OgImageRules.RasterType(PngWithScript).Should().BeNull("a script in a text chunk");
        OgImageRules.RasterType([.. Png, .. "<html>"u8]).Should().BeNull("bytes after IEND");
        OgImageRules.RasterType(Png[..^12]).Should().BeNull("no IEND");
        OgImageRules.RasterType([.. Gif[..^1], .. "<script>alert(1)</script>;"u8]).Should().BeNull("markup in a GIF");
        OgImageRules.RasterType([.. Jpeg, .. "<svg onload=alert(1)>"u8]).Should().BeNull("bytes after a JPEG's end");
        OgImageRules.RasterType([.. Webp, .. "<html>"u8]).Should().BeNull("bytes past the RIFF size");
        OgImageRules.RasterType([0xFF, 0xD8, 0xFF, 0xE0]).Should().BeNull("a JPEG with no end");
    }

    [Theory]
    [InlineData("https://cdn.example.com/a.png", true)]
    [InlineData("http://cdn.example.com/a.png", true)]
    [InlineData("/a.png", false)]
    [InlineData("ftp://cdn.example.com/a.png", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("https://user:pass@cdn.example.com/a.png", false)]
    [InlineData("", false)]
    public void Only_an_absolute_http_address_without_user_info_is_fetched(string value, bool fetchable)
        => (OgImageRules.FetchableUrl(value) is not null).Should().Be(fetchable);

    [Fact]
    public void Images_are_recognised_by_their_bytes()
    {
        OgImageRules.RasterType(Png).Should().Be("image/png");
        OgImageRules.RasterType(Jpeg).Should().Be("image/jpeg");
        OgImageRules.RasterType(Gif).Should().Be("image/gif");
        OgImageRules.RasterType(Webp).Should().Be("image/webp");
        OgImageRules.RasterType("<svg xmlns=\"http://www.w3.org/2000/svg\"/>"u8).Should().BeNull();
        OgImageRules.RasterType("<html>"u8).Should().BeNull();
    }

    private async Task<HttpResponseMessage> GetAsync(string pageId, string? user)
    {
        using var client = host.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/seotester/og-image?page=" + Uri.EscapeDataString(pageId));
        SeoRequests.AddIdentity(request, user, []);
        var response = await client.SendAsync(request);
        await response.Content.LoadIntoBufferAsync();
        return response;
    }
}
