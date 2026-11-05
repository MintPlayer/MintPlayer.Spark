using System.IO.Compression;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Minification;
using MintPlayer.Spark.Services;
using WebMarkupMin.Core;

namespace MintPlayer.Spark.Tests.Minification;

/// <summary>
/// <c>spark.AddMinification()</c> (<c>docs/html_minification_PRD.md</c> M1). The host here is the minification stage and
/// a stand-in for the server-side renderer, which reads the template through its own <c>next()</c> into a buffer and then
/// writes a different page to the response (SpaServices' <c>UseSpaPrerendering</c>, PRD §2.2). The legacy site's
/// placement minified only that template; these tests pin that the stage minifies the page the renderer writes.
/// </summary>
public class SparkMinificationTests
{
    private const string Template = "<!DOCTYPE html><html><head><title>template</title></head><body><app-root></app-root></body></html>";

    /// <summary>A server-rendered page in the shape Angular writes it: hydration comments, the transfer state, JSON-LD, inline styles.</summary>
    private const string RenderedPage = """
        <!DOCTYPE html>
        <html lang="en">
          <head>
            <title>A song - MintPlayer</title>
            <link rel="canonical" href="https://mintplayer.com/po/song/1">
            <style>
              .song-title  {  color : red ;  }
            </style>
            <script type="application/ld+json">{"@type": "MusicRecording",  "name": "A song"}</script>
          </head>
          <body>
            <app-root ngh="0">
              <div class="row">
                <span>A song</span>
                <!--container-->
              </div>
              <input type="text" value="">
            </app-root>
            <script id="ng-state" type="application/json">{"key": {"b": "A  song"}}</script>
          </body>
        </html>
        """;

    [Fact]
    public async Task The_rendered_page_is_minified_not_the_template()
    {
        using var host = await StartHost();

        var (status, body, _) = await Get(host, "/po/song/1");

        status.Should().Be(HttpStatusCode.OK);
        body.Should().Contain("<title>A song - MintPlayer</title>", "the renderer's page is what reaches the browser");
        body.Should().NotContain("\n", "even Safe whitespace collapses each run of indentation into one space");
        body.Length.Should().BeLessThan(RenderedPage.Length);
    }

    [Fact]
    public async Task What_Angular_and_a_crawler_read_is_left_as_it_was()
    {
        using var host = await StartHost();

        var (_, body, response) = await Get(host, "/po/song/1");

        body.Should().Contain("<!--container-->", "hydration markers are comments, and RemoveComments defaults to false (NG0500)");
        body.Should().Contain("""<script id="ng-state" type="application/json">{"key": {"b": "A  song"}}</script>""",
            "the transfer state is embedded JSON, which is never minified");
        body.Should().Contain("""{"@type": "MusicRecording",  "name": "A song"}""", "nor is JSON-LD");
        body.Should().Contain("""rel="canonical" href="https://mintplayer.com/po/song/1""", "attribute quotes and the scheme stay");
        body.Should().Contain("""<input type="text" value="">""", "redundant and empty attributes stay: CSS may select on them");
        body.Should().Contain(".song-title{color:red}", "the inline styles are minified: that is where the saving is (SP1)");
        response.Headers.Contains("X-HTML-Minification-Powered-By").Should().BeFalse();
    }

    [Fact]
    public async Task RemoveComments_removes_the_hydration_markers_too()
    {
        using var host = await StartHost(o => o.RemoveComments = true);

        var (_, body, _) = await Get(host, "/po/song/1");

        body.Should().NotContain("<!--container-->");
    }

    [Fact]
    public async Task Configuration_switches_it_off_whatever_the_code_says()
    {
        using var host = await StartHost(o => o.Enabled = true, new() { ["Spark:Minification:Enabled"] = "false" });

        var (_, body, _) = await Get(host, "/po/song/1");

        body.Should().Be(RenderedPage);
    }

    [Theory]
    [InlineData("/legal", false)]
    [InlineData("/legal/terms", false)]
    [InlineData("/LEGAL/terms", false)]
    [InlineData("/legalese", true)]
    [InlineData("/index.csr.html", false)]
    [InlineData("/@vite/client", false)]
    public async Task Excluded_paths_are_served_as_rendered(string path, bool minified)
    {
        using var host = await StartHost(o => o.ExcludedPaths.Add("/legal"));

        var (_, body, _) = await Get(host, path);

        (body != RenderedPage).Should().Be(minified);
    }

    [Theory]
    [InlineData(404, true)]
    [InlineData(401, true)]
    [InlineData(500, false)]
    public async Task A_rendered_error_page_is_minified_too(int status, bool minified)
    {
        using var host = await StartHost();

        var (_, body, _) = await Get(host, $"/po/song/1?status={status}");

        (body != RenderedPage).Should().Be(minified);
    }

    [Fact]
    public async Task Response_compression_outside_compresses_the_minified_page()
    {
        // WebMarkupMin throws on a response that already has a Content-Encoding. The compression the app registers at
        // the top of its pipeline wraps the stage, so it only ever sees the minified page.
        using var host = await StartHost(compress: true);
        var client = host.GetTestClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/po/song/1");
        request.Headers.AcceptEncoding.ParseAdd("br");

        using var response = await client.SendAsync(request);
        await using var brotli = new BrotliStream(await response.Content.ReadAsStreamAsync(), CompressionMode.Decompress);
        using var reader = new StreamReader(brotli, Encoding.UTF8);
        var body = await reader.ReadToEndAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentEncoding.Should().Equal("br");
        body.Should().Contain("<title>A song - MintPlayer</title>");
        body.Should().NotContain("\n");
    }

    [Fact]
    public async Task It_runs_in_Development()
    {
        using var host = await StartHost(environment: Environments.Development);

        var (_, body, _) = await Get(host, "/po/song/1");

        body.Should().NotBe(RenderedPage, "D4: a page that does not survive minification must break in Development first");
    }

    [Fact]
    public async Task A_post_is_never_minified()
    {
        using var host = await StartHost();
        var client = host.GetTestClient();

        using var response = await client.PostAsync("/po/song/1", new StringContent(""));

        (await response.Content.ReadAsStringAsync()).Should().Be(RenderedPage);
    }

    [Fact]
    public async Task Without_UseSparkPrerendering_the_host_refuses_to_start()
    {
        // G3: the stage exists only in a server-rendered app. AddMinification() in any other app is a mistake, and the
        // host says so rather than serving every page unminified.
        var act = async () => (await StartHost(applyStage: false)).Dispose();

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*BeforePrerendering*UseSparkPrerendering()*");
    }

    [Fact]
    public void AddMinification_registers_into_the_BeforePrerendering_stage_only()
    {
        var spark = new SparkBuilder(new ServiceCollection());

        spark.AddMinification();
        spark.AddMinification(o => o.RemoveComments = true);

        spark.Registry.PendingStages.Should().Equal(SparkMiddlewareStage.BeforePrerendering);
        spark.Services.Count(d => d.ServiceType == typeof(SparkHtmlMinification))
            .Should().Be(1, "a second AddMinification() adds options, not a second middleware");
        using var services = Services(spark);
        services.GetRequiredService<Microsoft.Extensions.Options.IOptions<SparkMinificationOptions>>().Value.RemoveComments
            .Should().BeTrue("the second call's options apply");

        spark.Registry.ApplyMiddleware(new ApplicationBuilder(services), SparkMiddlewareStage.BeforePrerendering);
        spark.Registry.PendingStages.Should().BeEmpty();
    }

    [Theory]
    [InlineData(SparkWhitespaceMinification.Safe, WhitespaceMinificationMode.Safe)]
    [InlineData(SparkWhitespaceMinification.Medium, WhitespaceMinificationMode.Medium)]
    [InlineData(SparkWhitespaceMinification.Aggressive, WhitespaceMinificationMode.Aggressive)]
    public void Whitespace_maps_onto_WebMarkupMin_s_mode_of_the_same_name(SparkWhitespaceMinification spark, WhitespaceMinificationMode expected)
        => SparkHtmlMinification.Settings(new SparkMinificationOptions { Whitespace = spark }).WhitespaceMinificationMode.Should().Be(expected);

    [Fact]
    public void The_defaults_are_the_owner_s()
    {
        var options = new SparkMinificationOptions();

        options.Enabled.Should().BeTrue();
        options.Whitespace.Should().Be(SparkWhitespaceMinification.Safe, "Aggressive breaks hydration on every page (PRD §6 SP2); owner: careful with the HTML");
        options.RemoveComments.Should().BeFalse("NG0500");
        options.ExcludedPaths.Should().BeEmpty();
    }

    private static ServiceProvider Services(SparkBuilder spark)
    {
        spark.Services.AddLogging();
        return spark.Services.BuildServiceProvider();
    }

    private static async Task<(HttpStatusCode Status, string Body, HttpResponseMessage Response)> Get(IHost host, string path)
    {
        var response = await host.GetTestClient().GetAsync(path);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(), response);
    }

    private static async Task<IHost> StartHost(
        Action<SparkMinificationOptions>? configure = null,
        Dictionary<string, string?>? configuration = null,
        bool applyStage = true,
        bool compress = false,
        string environment = "Production")
    {
        return await new HostBuilder()
            .UseEnvironment(environment)
            .ConfigureAppConfiguration(c => c.AddInMemoryCollection(configuration ?? []))
            .ConfigureWebHost(webHost => webHost
                .UseTestServer()
                .ConfigureServices((context, services) =>
                {
                    var spark = new SparkBuilder(services, context.Configuration);
                    spark.AddMinification(configure);
                    services.AddSingleton(spark.Registry);
                    services.AddSparkPendingStagesCheck();
                    if (compress)
                        services.AddResponseCompression(o => { o.EnableForHttps = true; o.Providers.Add<BrotliCompressionProvider>(); });
                })
                .Configure(app =>
                {
                    if (compress)
                        app.UseResponseCompression();
                    // Where UseSparkPrerendering applies it: just outside the renderer.
                    if (applyStage)
                        app.ApplicationServices.GetRequiredService<SparkModuleRegistry>().ApplyMiddleware(app, SparkMiddlewareStage.BeforePrerendering);
                    app.Use(RendererStandIn);
                    app.Run(context =>
                    {
                        context.Response.ContentType = "text/html; charset=utf-8";
                        return context.Response.WriteAsync(Template);
                    });
                }))
            .StartAsync();
    }

    /// <summary>
    /// What <c>UseSpaPrerendering</c> does with a request: reads the template through <c>next()</c> into a buffer, puts the
    /// response body back, and writes the rendered page to it, with the status the page has.
    /// </summary>
    private static async Task RendererStandIn(HttpContext context, Func<Task> next)
    {
        var original = context.Response.Body;
        using var template = new MemoryStream();
        context.Response.Body = template;
        await next();
        context.Response.Body = original;

        template.Length.Should().BeGreaterThan(0);
        if (int.TryParse(context.Request.Query["status"], out var status))
            context.Response.StatusCode = status;
        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.WriteAsync(RenderedPage);
    }
}
