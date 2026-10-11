using System.IO.Compression;
using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Raven.Client.Documents.Smuggler;
using SeoTester.Entities;
using SeoTester.LookupReferences;
using SeoTester.PageSpeed;
using SeoTester.Settings;
using SeoTester.Tests._Infrastructure;
using static SeoTester.Tests._Infrastructure.SeoApi;

namespace SeoTester.Tests.Rendering;

/// <summary>
/// SeoTester keeps facts about a page, never the page (<c>docs/seo_analyzer_PRD.md</c> §12, "Untrusted content: proof
/// tests"): after a whole audit (crawl, render in Chromium, PageSpeed, aggregate) of a site whose pages carry a unique
/// marker only in their bodies (text, a script, a comment, an attribute, and text a script writes), a full export of
/// the database (documents, revisions, attachments, counters, compare-exchange, expired and artificial documents
/// included) holds the marker nowhere. Red when the crawl keeps the body text on the <see cref="PageResult"/>.
/// </summary>
[Collection(RenderHostCollection.Name)]
public class CrawledContentTests(RenderHostFixture host)
{
    [Fact]
    public async Task No_body_content_of_a_crawled_and_rendered_page_is_stored()
    {
        var marker = "body-marker-" + Guid.NewGuid().ToString("N");
        var h = FakeSite.NewHost();
        var o = "https://" + h;
        string Body(string title, params string[] links) => FakeSite.Page(title, links).Replace("<body>", $$"""
            <body data-note="{{marker}}"><!-- {{marker}} --><p>Body text {{marker}} for the crawler only.</p>
            <script>var hidden = '{{marker}}-script'; document.addEventListener('DOMContentLoaded', () => { const p = document.createElement('p'); p.textContent = 'Rendered {{marker}}'; document.body.appendChild(p); });</script>
            """);
        host.Web.Page(o + "/robots.txt", "User-agent: *\nAllow: /\n");
        host.Web.Html(o + "/", Body("Home of the marker shop", "/about", "/contact"));
        host.Web.Html(o + "/about", Body("About the marker shop", "/"));
        host.Web.Html(o + "/contact", Body("Contact the marker shop", "/"));
        FakeSite.Registries(host.Web);
        var options = host.Factory.Services.GetRequiredService<IOptions<PageSpeedOptions>>().Value;
        host.Web.Pages[PageSpeedClient.RequestUrl(options, new Uri(o + "/")).AbsoluteUri] = new FakeResponse(HttpStatusCode.OK,
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Data", "Fixtures", "psi_mintplayer_mobile_perf.json")), ContentType: "application/json");
        var site = await SeedSiteAsync(host, Acme, hostName: h);
        await SetSettingAsync(host, SeoQuotas.Audits.Limit.Key, "-1");
        var (status, body, raw) = await ActionAsync(host, SiteTypeId, "Site", "StartAudit", site.Id!, Max);
        status.Should().Be(HttpStatusCode.OK, raw);
        var runId = body.GetProperty("result").GetProperty("runId").GetString()!;

        var run = await FakeSite.FinishedAsync(host.Database, runId, TimeSpan.FromMinutes(3));

        run.Status.Should().Be(AuditRunStatus.Completed, run.Error);
        var home = (await LoadDocumentAsync<PageResult>(host, PageResult.IdFor(runId, o + "/")))!;
        home.Title.Should().Be("Home of the marker shop", "the page was crawled, its extracted fields kept (the control)");
        host.Web.RequestsTo(h).Should().Contain(r => r.UserAgent != null && r.UserAgent.Contains("Chrome/"), "the pages were rendered too");

        var export = await ExportAsync();
        Encoding.UTF8.GetString(export).Should().Contain(runId, "the export holds this run (the control)");
        var at = export.AsSpan().IndexOf(Encoding.UTF8.GetBytes(marker));
        at.Should().Be(-1, at < 0 ? "" : "found in: " + Encoding.UTF8.GetString(export, Math.Max(0, at - 600), Math.Min(800, export.Length - Math.Max(0, at - 600))));
    }

    /// <summary>The whole database as the smuggler exports it, decompressed.</summary>
    private async Task<byte[]> ExportAsync()
    {
        var file = Path.Combine(Path.GetTempPath(), $"seo-export-{Guid.NewGuid():N}.ravendbdump");
        try
        {
            var operation = await host.Database.Smuggler.ExportAsync(new DatabaseSmugglerExportOptions
            {
                CompressionAlgorithm = ExportCompressionAlgorithm.Gzip,
                IncludeExpired = true,
                IncludeArtificial = true,
                IncludeArchived = true,
            }, file);
            await operation.WaitForCompletionAsync(TimeSpan.FromMinutes(2));
            await using var gzip = new GZipStream(File.OpenRead(file), CompressionMode.Decompress);
            using var bytes = new MemoryStream();
            await gzip.CopyToAsync(bytes);
            return bytes.ToArray();
        }
        finally
        {
            File.Delete(file);
        }
    }
}
