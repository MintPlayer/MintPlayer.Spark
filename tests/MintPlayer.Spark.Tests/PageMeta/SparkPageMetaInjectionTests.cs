using System.IO.Compression;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using MintPlayer.Spark.PageMeta;

namespace MintPlayer.Spark.Tests.PageMeta;

/// <summary>
/// Server-side meta tags for SPA routes (commerce PRD X6, spike S19). The SPA is served the two ways the apps serve it:
/// production (the static-file middleware sends <c>index.html</c> through <c>SendFileAsync</c>, after a fallback that
/// rewrites the path, as <c>UseSpaImproved</c> does) and Development (a proxy to the Angular dev server, as
/// <c>UseAngularCliServer</c> does). The middleware arrives through its <see cref="IStartupFilter"/>, as in an app.
/// The same pages are run through SeoTester's own checks in <c>apps/SeoTester/SeoTester.Tests/Checks/SparkPageMetaSeoTests.cs</c>
/// (SeoTester.Library is not referenced from here: it is an app's entity library, and its types would join this
/// suite's assembly scans).
/// </summary>
public sealed class SparkPageMetaInjectionTests : IDisposable
{
    internal const string Template = """
        <!doctype html>
        <html lang="en">
        <head>
          <meta charset="utf-8">
          <title>ClientApp</title>
          <base href="/">
          <meta name="description" content="The template's description">
          <meta property="og:title" content="Template card">
          <meta name="viewport" content="width=device-width, initial-scale=1">
          <link rel="canonical" href="https://template.example/">
        </head>
        <body><app-root></app-root></body>
        </html>
        """;

    private readonly string _root = Directory.CreateTempSubdirectory("spark-page-meta-").FullName;

    public SparkPageMetaInjectionTests() => File.WriteAllText(Path.Combine(_root, "index.html"), Template);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    /// <summary>The public event pages of a shop. An unknown slug has no page: <see langword="null"/>.</summary>
    internal sealed class EventPageMeta : ISparkPageMetaProvider
    {
        public IEnumerable<string> RoutePatterns => ["/events/{slug}"];

        public Task<SparkPageMeta?> GetAsync(SparkPageMetaContext context, CancellationToken cancellationToken)
        {
            var slug = (string)context.RouteValues["slug"]!;
            return Task.FromResult<SparkPageMeta?>(slug == "unknown"
                ? null
                : new SparkPageMeta(
                    Title: $"{slug} – summer festival tickets",
                    Description: $"Tickets for {slug}: three days of live music, camping and food trucks. Seated and standing tickets, sold by the organiser.",
                    Image: new Uri("/media/events/poster.png", UriKind.Relative)));
        }
    }

    private async Task<IHost> StaticHostAsync()
        => await new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(Services)
                .Configure(app =>
                {
                    var files = new StaticFileOptions { FileProvider = new PhysicalFileProvider(_root) };
                    app.UseStaticFiles(files);
                    app.Map("/spark", spark => spark.Run(context => context.Response.WriteAsJsonAsync(new { ok = true })));
                    // UseSpaImproved: every other request is answered with the default page.
                    app.Use((context, next) =>
                    {
                        context.Request.Path = "/index.html";
                        return next(context);
                    });
                    app.UseStaticFiles(files);
                }))
            .StartAsync();

    private static void Services(IServiceCollection services)
    {
        services.AddSparkPageMeta();
        services.AddScoped<ISparkPageMetaProvider, EventPageMeta>();
        services.Configure<SparkPageMetaOptions>(o => o.PublicOrigin = "https://shop.example");
    }

    private static HttpRequestMessage Navigation(string path, HttpMethod? method = null)
    {
        var request = new HttpRequestMessage(method ?? HttpMethod.Get, path);
        request.Headers.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        return request;
    }

    [Fact]
    public async Task A_public_page_gets_its_own_title_description_card_and_canonical()
    {
        using var host = await StaticHostAsync();

        var response = await host.GetTestClient().SendAsync(Navigation("/events/rock-werchter"));
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body.Should().Contain("<title data-spark-meta>rock-werchter – summer festival tickets</title>");
        body.Should().Contain("<meta property=\"og:image\" content=\"https://shop.example/media/events/poster.png\" data-spark-meta>");
        body.Should().Contain("<meta property=\"og:url\" content=\"https://shop.example/events/rock-werchter\" data-spark-meta>");
        body.Should().Contain("<link rel=\"canonical\" href=\"https://shop.example/events/rock-werchter\" data-spark-meta>");
        body.Should().Contain("<meta name=\"twitter:card\" content=\"summary_large_image\" data-spark-meta>");
        body.Should().Contain("<meta property=\"og:type\" content=\"website\" data-spark-meta>");

        // The template's own tags are gone, so a crawler has exactly one of each.
        body.Should().NotContain("ClientApp");
        body.Should().NotContain("The template's description");
        body.Should().NotContain("Template card");
        body.Should().NotContain("template.example");
        body.Should().Contain("<base href=\"/\">");
        body.Should().Contain("<app-root></app-root>");
    }

    [Fact]
    public async Task The_rewritten_page_has_no_template_validators_and_is_revalidated()
    {
        using var host = await StaticHostAsync();

        var response = await host.GetTestClient().SendAsync(Navigation("/events/rock-werchter"));
        var bytes = await response.Content.ReadAsByteArrayAsync();

        response.Headers.ETag.Should().BeNull();
        response.Content.Headers.LastModified.HasValue.Should().BeFalse();
        response.Headers.CacheControl!.NoCache.Should().BeTrue();
        response.Content.Headers.ContentLength.Should().Be(bytes.Length);
    }

    /// <summary>A browser holding the template's ETag must not get a 304 that keeps it on a copy without the tags.</summary>
    [Fact]
    public async Task A_conditional_navigation_still_gets_the_rewritten_page()
    {
        using var host = await StaticHostAsync();
        var client = host.GetTestClient();
        var plain = await client.SendAsync(Navigation("/events/unknown"));
        plain.Headers.ETag.Should().NotBeNull("the static-file middleware validates the untouched template");

        var request = Navigation("/events/rock-werchter");
        request.Headers.IfNoneMatch.Add(plain.Headers.ETag!);
        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Contain("rock-werchter – summer festival tickets");
    }

    [Fact]
    public async Task Seller_text_and_the_path_are_html_encoded()
    {
        using var host = await StaticHostAsync();

        // slug = "><script>alert(1)<\script><x  (a backslash: %2F would stay encoded in the path)
        var response = await host.GetTestClient().SendAsync(Navigation("/events/%22%3E%3Cscript%3Ealert(1)%3C%5Cscript%3E%3Cx%20"));
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body.Should().NotContain("<script>");
        body.Should().NotContain("\"><");
        body.Should().Contain("&quot;&gt;&lt;script&gt;alert(1)");
    }

    /// <summary>The canonical and og:url come from configuration: a forged Host must not end up in a cached card.</summary>
    [Fact]
    public async Task The_origin_is_the_configured_one_never_the_host_header()
    {
        using var host = await StaticHostAsync();

        var request = Navigation("/events/rock-werchter");
        request.Headers.Host = "evil.example";
        var body = await (await host.GetTestClient().SendAsync(request)).Content.ReadAsStringAsync();

        body.Should().NotContain("evil.example");
        body.Should().Contain("href=\"https://shop.example/events/rock-werchter\"");
    }

    [Theory]
    [InlineData("/events/unknown")]          // the provider has no page: null
    [InlineData("/about")]                   // no pattern matches
    [InlineData("/events/a/b")]              // no pattern matches
    public async Task Other_pages_are_served_untouched(string path)
    {
        using var host = await StaticHostAsync();

        var response = await host.GetTestClient().SendAsync(Navigation(path));

        (await response.Content.ReadAsStringAsync()).Should().Be(Template);
        response.Headers.ETag.Should().NotBeNull();
    }

    [Fact]
    public async Task A_request_that_is_not_a_navigation_is_untouched()
    {
        using var host = await StaticHostAsync();

        var request = new HttpRequestMessage(HttpMethod.Get, "/events/rock-werchter");
        request.Headers.Accept.ParseAdd("application/json");
        var response = await host.GetTestClient().SendAsync(request);

        (await response.Content.ReadAsStringAsync()).Should().Be(Template);
    }

    [Fact]
    public async Task Spark_api_paths_are_never_matched()
    {
        using var host = await StaticHostAsync();

        var response = await host.GetTestClient().SendAsync(Navigation("/spark/events/rock-werchter"));

        (await response.Content.ReadAsStringAsync()).Should().Contain("\"ok\":true");
    }

    [Fact]
    public async Task A_head_request_gets_the_headers_and_no_body()
    {
        using var host = await StaticHostAsync();

        var response = await host.GetTestClient().SendAsync(Navigation("/events/rock-werchter", HttpMethod.Head));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.ETag.Should().BeNull();
        response.Headers.CacheControl!.NoCache.Should().BeTrue();
        (await response.Content.ReadAsByteArrayAsync()).Should().BeEmpty();
    }

    /// <summary>
    /// Development: <c>UseAngularCliServer</c> proxies to the dev server, which compresses when asked. The middleware
    /// strips <c>Accept-Encoding</c> on a matched route, so the body arrives as text it can rewrite.
    /// </summary>
    [Fact]
    public async Task The_dev_server_proxy_is_rewritten_too()
    {
        string? upstreamAcceptEncoding = null;
        using var upstream = await new HostBuilder()
            .ConfigureWebHost(web => web.UseTestServer().Configure(app => app.Run(async context =>
            {
                upstreamAcceptEncoding = context.Request.Headers.AcceptEncoding.ToString();
                context.Response.ContentType = "text/html; charset=utf-8";
                var bytes = Encoding.UTF8.GetBytes(Template);
                if (upstreamAcceptEncoding.Contains("gzip"))
                {
                    context.Response.Headers.ContentEncoding = "gzip";
                    await using var gzip = new GZipStream(context.Response.Body, CompressionLevel.Fastest, leaveOpen: true);
                    await gzip.WriteAsync(bytes);
                    return;
                }
                await context.Response.Body.WriteAsync(bytes);
            })))
            .StartAsync();
        var upstreamClient = upstream.GetTestClient();

        using var host = await new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(Services)
                .Configure(app => app.Run(async context =>
                {
                    // What SpaProxy.PerformProxyRequest does: copy the request headers, then the response's headers and body.
                    var forwarded = new HttpRequestMessage(new HttpMethod(context.Request.Method), context.Request.Path.ToUriComponent());
                    foreach (var header in context.Request.Headers.Where(h => !h.Key.Equals("Host", StringComparison.OrdinalIgnoreCase)))
                        forwarded.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
                    using var answer = await upstreamClient.SendAsync(forwarded, HttpCompletionOption.ResponseHeadersRead);
                    context.Response.StatusCode = (int)answer.StatusCode;
                    foreach (var header in answer.Headers.Concat(answer.Content.Headers))
                        context.Response.Headers[header.Key] = header.Value.ToArray();
                    context.Response.Headers.Remove("transfer-encoding");
                    await answer.Content.CopyToAsync(context.Response.Body);
                })))
            .StartAsync();

        var request = Navigation("/events/rock-werchter");
        request.Headers.AcceptEncoding.ParseAdd("gzip, deflate, br");
        var response = await host.GetTestClient().SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        upstreamAcceptEncoding.Should().BeEmpty();
        response.Content.Headers.ContentEncoding.Should().BeEmpty();
        body.Should().Contain("<title data-spark-meta>rock-werchter – summer festival tickets</title>");
        body.Should().Contain("https://shop.example/events/rock-werchter");
    }

    [Fact]
    public void AddSpark_registers_the_filter()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "libs", "spark", "MintPlayer.Spark", "SparkMiddleware.cs"));

        source.Should().Contain("PageMeta.SparkPageMetaInjection.AddSparkPageMeta(services);");
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "nx.json")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root (nx.json) not found.");
    }
}
