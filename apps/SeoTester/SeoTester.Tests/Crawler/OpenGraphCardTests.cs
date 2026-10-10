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
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, 0x49, 0x48, 0x44, 0x52, 0, 0, 4, 0xB0, 0, 0, 2, 0x76, 8, 6, 0, 0, 0];

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
        OgImageRules.RasterType([0xFF, 0xD8, 0xFF, 0xE0]).Should().Be("image/jpeg");
        OgImageRules.RasterType("GIF89a...."u8).Should().Be("image/gif");
        OgImageRules.RasterType("RIFF\0\0\0\0WEBPVP8 "u8).Should().Be("image/webp");
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
