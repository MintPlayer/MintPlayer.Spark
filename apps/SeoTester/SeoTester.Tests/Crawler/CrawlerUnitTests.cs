using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MintPlayer.Spark.Http;
using SeoTester.Checks;
using SeoTester.Crawler;
using SeoTester.Tests._Infrastructure;

namespace SeoTester.Tests.Crawler;

/// <summary>The pieces of the crawler below the pipeline: the registry parsers on recorded answers, politeness, and the SSRF-safe clients.</summary>
public class CrawlerUnitTests
{
    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Data", "Fixtures", name));

    [Fact]
    public void Rdap_goes_through_the_iana_bootstrap_and_reads_the_expiration_event()
    {
        var bootstrap = Fixture("iana_dns.json");

        SiteParsers.RdapBaseFor(bootstrap, "example.com").Should().Be("https://rdap.verisign.com/com/v1/");
        SiteParsers.RdapBaseFor(bootstrap, "example.be").Should().BeNull("spike S4: .be has no RDAP");
        SiteParsers.RdapExpiry(Fixture("rdap_com.json")).Should().Be(new DateTime(2028, 9, 14, 4, 0, 0, DateTimeKind.Utc));
        SiteParsers.RdapExpiry(Fixture("rdap_nl.json")).HasValue.Should().BeFalse("spike S4: .nl publishes no expiry");
        SiteParsers.RdapExpiry("not json").HasValue.Should().BeFalse();
    }

    [Fact]
    public void Hstspreload_status_is_read_from_the_recorded_answers()
    {
        SiteParsers.HstsPreloadStatus(Fixture("hsts_status_example.com.json")).Should().Be("unknown");
        SiteParsers.HstsPreloadStatus(Fixture("hsts_status_github.com.json")).Should().Be("preloaded");
        SiteParsers.HstsPreloadStatus("[]").Should().BeNull();
    }

    [Fact]
    public void The_manifest_and_css_readers_find_what_the_checks_ask()
    {
        SiteParsers.ManifestProblems("""{"name":"App","start_url":"/","display":"standalone","theme_color":"#000","icons":[{"src":"a.png","sizes":"192x192"},{"src":"b.png","sizes":"512x512"}]}""")
            .Should().BeEmpty();
        SiteParsers.ManifestProblems("""{"short_name":"App","display":"browser"}""").Should().Contain("no start_url").And.Contain("no 512×512 icon");
        SiteParsers.ManifestProblems("{").Should().Equal("invalid JSON");

        var css = SiteParsers.ScanCss(["@font-face{font-family:A;src:url(a.woff2)} @font-face{font-family:B;font-display:swap} @media (prefers-color-scheme: dark){body{}}"],
            ["https://fonts.googleapis.com/css2?family=Inter"], colorSchemeMeta: false);
        css.Should().Be(new CssFacts(3, 2, true, false));

        SiteParsers.JudgeMinified(string.Concat(Enumerable.Repeat("body {\n    color: red;\n    /* a comment */\n}\n", 200))).Minified.Should().BeFalse();
        SiteParsers.JudgeMinified(string.Concat(Enumerable.Repeat("body{color:red}", 400))).Minified.Should().BeTrue();
        SiteParsers.JudgeMinified("tiny").Minified.HasValue.Should().BeFalse();
    }

    [Fact]
    public async Task The_pacer_keeps_one_connection_and_the_interval_and_honours_a_retry_after()
    {
        using var pacer = new HostPacer(1, TimeSpan.FromMilliseconds(150), null, TimeProvider.System);
        var starts = new List<long>();
        var watch = Stopwatch.StartNew();
        var inFlight = 0;
        var maxInFlight = 0;

        await Task.WhenAll(Enumerable.Range(0, 4).Select(async _ =>
        {
            using (await pacer.WaitAsync(CancellationToken.None))
            {
                lock (starts)
                    starts.Add(watch.ElapsedMilliseconds);
                maxInFlight = Math.Max(maxInFlight, Interlocked.Increment(ref inFlight));
                await Task.Delay(20);
                Interlocked.Decrement(ref inFlight);
            }
        }));

        maxInFlight.Should().Be(1);
        starts.Order().Zip(starts.Order().Skip(1), (a, b) => b - a).Should().OnlyContain(gap => gap >= 140);

        pacer.Defer(TimeSpan.FromMilliseconds(400));
        var before = watch.ElapsedMilliseconds;
        using (await pacer.WaitAsync(CancellationToken.None)) { }
        (watch.ElapsedMilliseconds - before).Should().BeGreaterThanOrEqualTo(380);
    }

    [Fact]
    public async Task The_pacer_carries_the_last_request_over_from_the_previous_batch()
    {
        using var pacer = new HostPacer(4, TimeSpan.FromMilliseconds(300), DateTime.UtcNow, TimeProvider.System);
        var watch = Stopwatch.StartNew();

        using (await pacer.WaitAsync(CancellationToken.None)) { }

        watch.ElapsedMilliseconds.Should().BeGreaterThanOrEqualTo(250);
    }

    [Fact]
    public async Task The_crawlers_clients_refuse_private_addresses()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSeoCrawler(new ConfigurationBuilder().Build());
        var provider = services.BuildServiceProvider();
        var fetcher = new PageFetcher(provider.GetRequiredService<IHttpClientFactory>(), NullLogger<PageFetcher>.Instance);

        var page = await fetcher.FetchAsync(new Uri("http://127.0.0.1/"), 1024, CancellationToken.None);
        var metadata = await fetcher.FetchAsync(new Uri("http://169.254.169.254/latest/meta-data/"), 1024, CancellationToken.None, client: SeoHttp.Lookups);

        page.StatusCode.Should().Be(0);
        page.Error.Should().Be("refused: Address");
        metadata.Error.Should().Be("refused: Address");
        await ((Func<Task>)(async () => await provider.GetRequiredService<ISafeHttpSockets>().ConnectAsync(SeoHttp.Tls, "10.0.0.1", 443, CancellationToken.None)))
            .Should().ThrowAsync<SafeHttpBlockedException>();
    }

    [Fact]
    public async Task The_fetcher_sends_the_bot_user_agent_traces_redirects_and_reads_the_charset()
    {
        var web = new FakeWeb();
        web.Redirect("https://example.com/old", "https://example.com/new", System.Net.HttpStatusCode.MovedPermanently);
        web.Pages["https://example.com/new"] = new FakeResponse(System.Net.HttpStatusCode.OK, "<html><head><meta charset=\"iso-8859-1\"></head><body>café</body></html>",
            ContentType: "text/html");
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSeoCrawler(new ConfigurationBuilder().Build());
        services.AddHttpClient(SeoHttp.Crawler).ConfigurePrimaryHttpMessageHandler(web.CreateHandler);
        var fetcher = new PageFetcher(services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>(), NullLogger<PageFetcher>.Instance);

        var result = await fetcher.FetchAsync(new Uri("https://example.com/old"), PageFetcher.MaxPageBytes, CancellationToken.None);

        result.StatusCode.Should().Be(200);
        result.FinalUrl!.AbsoluteUri.Should().Be("https://example.com/new");
        result.Hops.Should().Equal(("https://example.com/old", 301));
        web.Requests.Should().OnlyContain(r => r.UserAgent == SeoTesterBot.UserAgent);
        result.Text().Should().Contain("caf");
    }
}
