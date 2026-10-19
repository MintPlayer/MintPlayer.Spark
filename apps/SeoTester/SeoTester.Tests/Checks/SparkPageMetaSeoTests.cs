using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using MintPlayer.Spark;
using MintPlayer.Spark.PageMeta;
using SeoTester.Checks;
using SeoTester.Html;
using SeoTester.Scoring;

namespace SeoTester.Tests.Checks;

/// <summary>
/// Spark's server-side meta tags (commerce PRD X6, spike S19) judged by SeoTester's own page checks: a public SPA route
/// served through <c>AddSpark</c>'s middleware passes the title, description, OpenGraph, Twitter card and canonical
/// checks a crawler would run. The middleware's own behaviour (dev proxy, HEAD, validators, pass-through) is tested
/// in <c>MintPlayer.Spark.Tests/PageMeta/SparkPageMetaInjectionTests.cs</c>.
/// </summary>
public sealed class SparkPageMetaSeoTests : IDisposable
{
    private const string Template = """
        <!doctype html>
        <html lang="en">
        <head>
          <meta charset="utf-8">
          <title>ClientApp</title>
          <base href="/">
          <meta name="viewport" content="width=device-width, initial-scale=1">
        </head>
        <body><app-root></app-root></body>
        </html>
        """;

    private readonly string _root = Directory.CreateTempSubdirectory("seo-page-meta-").FullName;

    public SparkPageMetaSeoTests() => File.WriteAllText(Path.Combine(_root, "index.html"), Template);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private sealed class EventPageMeta : ISparkPageMetaProvider
    {
        public IEnumerable<string> RoutePatterns => ["/events/{slug}"];

        public Task<SparkPageMeta?> GetAsync(SparkPageMetaContext context, CancellationToken cancellationToken)
            => Task.FromResult<SparkPageMeta?>(new SparkPageMeta(
                Title: $"{context.RouteValues["slug"]} – summer festival tickets",
                Description: "Three days of live music, camping and food trucks. Seated and standing tickets, sold by the organiser.",
                Image: new Uri("/media/events/poster.png", UriKind.Relative)));
    }

    /// <summary>A production SPA host: static files, then the default page for every other path (<c>UseSpaImproved</c>).</summary>
    private async Task<IHost> HostAsync()
        => await new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddSpark(_ => { });
                    services.AddScoped<ISparkPageMetaProvider, EventPageMeta>();
                    services.Configure<SparkPageMetaOptions>(o => o.PublicOrigin = "https://tickets.example");
                })
                .Configure(app =>
                {
                    var files = new StaticFileOptions { FileProvider = new PhysicalFileProvider(_root) };
                    app.Use((context, next) =>
                    {
                        context.Request.Path = "/index.html";
                        return next(context);
                    });
                    app.UseStaticFiles(files);
                }))
            .StartAsync();

    private static async Task<PageInput> FetchAsync(IHost host, string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Accept.ParseAdd("text/html");
        var response = await host.GetTestClient().SendAsync(request);
        var html = await response.Content.ReadAsStringAsync();
        var headers = response.Headers.Concat(response.Content.Headers)
            .ToDictionary(h => h.Key.ToLowerInvariant(), h => string.Join(", ", h.Value));
        return new PageInput
        {
            Url = new Uri("https://tickets.example" + path),
            StatusCode = (int)response.StatusCode,
            Headers = headers,
            BodyBytes = html.Length,
            Prefix = html.Length > 1024 ? html[..1024] : html,
            Html = HtmlPage.Parse(html),
        };
    }

    [Fact]
    public async Task A_public_page_passes_the_title_description_social_and_canonical_checks()
    {
        using var host = await HostAsync();

        var statuses = PageChecks.Evaluate(await FetchAsync(host, "/events/rock-werchter")).ToDictionary(f => f.CheckId, f => f.Status);

        foreach (var id in new[] { "seo.title-present", "seo.title-length", "seo.meta-description", "seo.canonical", "social.open-graph", "social.twitter-card" })
            statuses[id].Should().Be(CheckStatus.Pass, id);
    }

    [Fact]
    public async Task The_template_alone_fails_the_social_card()
    {
        using var host = await HostAsync();

        var statuses = PageChecks.Evaluate(await FetchAsync(host, "/about")).ToDictionary(f => f.CheckId, f => f.Status);

        statuses["social.open-graph"].Should().Be(CheckStatus.Fail, "the control: the unmatched route keeps the bare template");
    }
}
