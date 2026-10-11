using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SeoTester.Crawler;
using SeoTester.Dns;
using SeoTester.Fetching;
using SeoTester.Tests._Infrastructure;

namespace SeoTester.Tests.Fetcher;

/// <summary>
/// A site cannot make SeoTester carry its cookies (<c>docs/seo_analyzer_PRD.md</c> §12, "Untrusted content: proof
/// tests"): a <c>Set-Cookie</c> is reported (the cookie checks read it) and never sent back, not on the next request to
/// the site, not on a redirect hop, not to another site, not in a later run. Real sockets on two loopback addresses
/// standing in for two public sites, the SSRF-safe clients as they are wired, so the wire shows every header sent. The
/// in-process fetcher and the seo-fetcher's API (D54) are both covered; the SSRF-safe client alone is covered in
/// <c>SafeHttpClientTests.A_cookie_a_site_sets_is_never_sent_back_by_the_client</c>.
/// Red with <c>UseCookies = true</c> on the SSRF-safe primary handler: the next request, the redirect hop and the later
/// request each carry the cookies.
/// </summary>
public class FetcherCookieTests
{
    [Fact]
    public async Task The_in_process_fetcher_never_sends_a_cookie_back()
    {
        using var sites = new CookieSites();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ISeoDns>(new FakeDns());
        services.AddSingleton<ITlsProber>(new FakeTls());
        services.AddSeoFetching(new ConfigurationBuilder().AddInMemoryCollection(sites.Settings).Build());
        await using var provider = services.BuildServiceProvider();

        await sites.ProveAsync(provider.GetRequiredService<ISeoFetcher>());
    }

    [Fact]
    public async Task The_fetcher_service_never_sends_a_cookie_back()
    {
        using var sites = new CookieSites();
        await using var fetcher = await FetcherTestHost.StartAsync(settings: sites.Settings);

        await sites.ProveAsync(fetcher.Remote());
    }

    /// <summary>Site A (127.0.0.2) sets a cookie on every answer; site B (127.0.0.3) is where A redirects to.</summary>
    private sealed class CookieSites : IDisposable
    {
        private readonly LoopbackHttpServer b = new(IPAddress.Parse("127.0.0.3"), _ => LoopbackHttpServer.Ok("b", "b=evil; Path=/"));
        private readonly LoopbackHttpServer a;

        public CookieSites()
        {
            a = new LoopbackHttpServer(IPAddress.Parse("127.0.0.2"), path => path switch
            {
                "/hop" => LoopbackHttpServer.Redirect("/after", "r=evil; Path=/"),
                "/away" => LoopbackHttpServer.Redirect($"http://127.0.0.3:{b.Port}/b", "x=evil; Domain=127.0.0.3; Path=/"),
                _ => LoopbackHttpServer.Ok(path, "s=evil; Path=/; Max-Age=3600"),
            });
        }

        public IReadOnlyDictionary<string, string?> Settings => new Dictionary<string, string?>
        {
            ["Spark:SafeHttp:AllowedPorts:0"] = a.Port.ToString(),
            ["Spark:SafeHttp:AllowedPorts:1"] = b.Port.ToString(),
            ["Spark:SafeHttp:AllowedNetworks:0"] = "127.0.0.2/32",
            ["Spark:SafeHttp:AllowedNetworks:1"] = "127.0.0.3/32",
        };

        private string A(string path) => $"http://127.0.0.2:{a.Port}{path}";

        public async Task ProveAsync(ISeoFetcher fetcher)
        {
            var first = await fetcher.FetchAsync(new Uri(A("/set")), 1024, CancellationToken.None);
            first.StatusCode.Should().Be(200, first.Error);
            first.SetCookies.Should().Contain(c => c.StartsWith("s=evil"), "the cookie reached the fetcher (and the checks still see it)");

            (await fetcher.FetchAsync(new Uri(A("/next")), 1024, CancellationToken.None)).StatusCode.Should().Be(200);
            var hop = await fetcher.FetchAsync(new Uri(A("/hop")), 1024, CancellationToken.None);
            hop.StatusCode.Should().Be(200, hop.Error);
            hop.Hops.Should().ContainSingle();
            var away = await fetcher.FetchAsync(new Uri(A("/away")), 1024, CancellationToken.None);
            away.StatusCode.Should().Be(200, away.Error);
            (await fetcher.FetchAsync(new Uri($"http://127.0.0.3:{b.Port}/b"), 1024, CancellationToken.None)).StatusCode.Should().Be(200);
            // The browser's client, one hop at a time (as Chromium asks through the router).
            var browser = await fetcher.FetchAsync(new SeoFetchRequest(new Uri(A("/browser")), 1024) { Client = SeoHttp.Browser }, CancellationToken.None);
            browser.StatusCode.Should().Be(200, browser.Error);
            // A later run: the same clients, long after the cookies were handed out.
            (await fetcher.FetchAsync(new Uri(A("/later")), 1024, CancellationToken.None)).StatusCode.Should().Be(200);

            var heads = a.Heads.Concat(b.Heads).ToList();
            var lines = heads.Select(h => h.Split("\r\n")[0]).ToList();
            foreach (var line in new[] { "GET /next HTTP/1.1", "GET /after HTTP/1.1", "GET /b HTTP/1.1", "GET /later HTTP/1.1" })
                lines.Should().Contain(line);
            heads.Should().HaveCount(9);
            heads.Where(h => h.Contains("\r\nCookie:", StringComparison.OrdinalIgnoreCase)).Should().BeEmpty("no request carries a cookie a site set");
        }

        public void Dispose()
        {
            a.Dispose();
            b.Dispose();
        }
    }
}
