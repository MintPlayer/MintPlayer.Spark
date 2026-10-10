using System.Net;
using System.Net.Http.Json;
using System.Text;
using SeoTester.Crawler;
using SeoTester.Dns;
using SeoTester.Fetching;
using SeoTester.Tests._Infrastructure;

namespace SeoTester.Tests.Fetcher;

/// <summary>
/// The seo-fetcher's internal API and its trust boundary (<c>docs/seo_analyzer_PRD.md</c> D54): the shared secret,
/// the size and time bounds on both sides, the redirect and address rules still enforced inside the fetcher, and the
/// requests it refuses to make. In-memory hosts, except where the address rules need real sockets.
/// </summary>
public class FetcherApiTests
{
    private static readonly object FetchBody = new FetchRequestWire("https://example.test/", "GET", SeoHttp.Crawler, 1024, null, null);

    [Fact]
    public async Task Calls_without_the_key_or_with_a_wrong_one_are_refused_before_anything_is_fetched()
    {
        var web = new FakeWeb();
        web.Html("https://example.test/", "<html></html>");
        var dns = new FakeDns();
        await using var fetcher = await FetcherTestHost.StartAsync(web, dns, new FakeTls());
        using var client = fetcher.CreateClient();

        async Task<HttpStatusCode> PostAsync(string path, object body, string? key)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, path.TrimStart('/')) { Content = JsonContent.Create(body, options: SeoFetcherProtocol.Json) };
            if (key is not null)
                request.Headers.Add(SeoFetcherProtocol.KeyHeader, key);
            using var response = await client.SendAsync(request);
            return response.StatusCode;
        }

        (await PostAsync(SeoFetcherProtocol.FetchPath, FetchBody, key: null)).Should().Be(HttpStatusCode.Unauthorized);
        (await PostAsync(SeoFetcherProtocol.FetchPath, FetchBody, key: "")).Should().Be(HttpStatusCode.Unauthorized);
        (await PostAsync(SeoFetcherProtocol.FetchPath, FetchBody, key: FetcherTestHost.Key[..^1] + "X")).Should().Be(HttpStatusCode.Unauthorized);
        (await PostAsync(SeoFetcherProtocol.FetchPath, FetchBody, key: FetcherTestHost.Key + "X")).Should().Be(HttpStatusCode.Unauthorized);
        (await PostAsync(SeoFetcherProtocol.TlsPath, new TlsRequestWire("example.test", 443), key: "wrong")).Should().Be(HttpStatusCode.Unauthorized);
        (await PostAsync(SeoFetcherProtocol.DnsPath, new DnsRequestWire("example.test", SeoDnsType.Txt), key: null)).Should().Be(HttpStatusCode.Unauthorized);
        (await PostAsync("/anything", FetchBody, key: null)).Should().Be(HttpStatusCode.Unauthorized, "an unknown path tells nobody without the key that it is unknown");
        (await PostAsync("/anything", FetchBody, key: FetcherTestHost.Key)).Should().Be(HttpStatusCode.NotFound);
        web.Requests.Should().BeEmpty("nothing was fetched for a caller without the key");

        using var health = await client.GetAsync("health");
        health.StatusCode.Should().Be(HttpStatusCode.OK, "the compose healthcheck carries no key");

        (await PostAsync(SeoFetcherProtocol.FetchPath, FetchBody, FetcherTestHost.Key)).Should().Be(HttpStatusCode.OK);
        web.Requests.Should().ContainSingle();

        // The app's side: a wrong key is the fetcher being unusable, never a failed page.
        var wrong = fetcher.Remote(key: "a-wrong-key-that-is-long-enough-0123456789");
        await ((Func<Task>)(() => wrong.FetchAsync(new Uri("https://example.test/"), 1024, CancellationToken.None)))
            .Should().ThrowAsync<SeoFetcherUnavailableException>();
    }

    [Fact]
    public async Task A_body_is_cut_at_the_size_asked_for_and_never_beyond_the_clients_cap()
    {
        var web = new FakeWeb();
        web.Pages["https://example.test/big"] = new FakeResponse(HttpStatusCode.OK, new string('a', 3 * 1024 * 1024), ContentType: "text/plain");
        await using var fetcher = await FetcherTestHost.StartAsync(web);
        var remote = fetcher.Remote();

        var small = await remote.FetchAsync(new Uri("https://example.test/big"), 1000, CancellationToken.None);
        small.StatusCode.Should().Be(200);
        small.Truncated.Should().BeTrue();
        small.Body!.Length.Should().Be(1000);

        // Verification's cap is 1 MiB: asking for 3 MiB still gets 1 MiB, cut, from the fetcher.
        var capped = await remote.FetchAsync(new Uri("https://example.test/big"), 3 * 1024 * 1024, CancellationToken.None, client: SeoHttp.Verification);
        capped.Truncated.Should().BeTrue();
        capped.Body!.Length.Should().Be(SeoHttp.MaxBodyBytes[SeoHttp.Verification]);
    }

    [Fact]
    public async Task The_api_refuses_oversized_and_malformed_requests()
    {
        var web = new FakeWeb();
        await using var fetcher = await FetcherTestHost.StartAsync(web, new FakeDns(), new FakeTls());
        using var client = fetcher.CreateClient();

        async Task<HttpStatusCode> PostAsync(string path, HttpContent content)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, path.TrimStart('/')) { Content = content };
            request.Headers.Add(SeoFetcherProtocol.KeyHeader, FetcherTestHost.Key);
            using var response = await client.SendAsync(request);
            return response.StatusCode;
        }
        HttpContent Json(object body) => JsonContent.Create(body, options: SeoFetcherProtocol.Json);

        // Larger than the API reads at all (2 MiB), then a browser body over 1 MiB inside the limit.
        (await PostAsync(SeoFetcherProtocol.FetchPath, new ByteArrayContent(new byte[SeoFetchLimits.MaxApiRequestBytes + 1]))).Should().Be(HttpStatusCode.BadRequest);
        (await PostAsync(SeoFetcherProtocol.FetchPath, Json(new FetchRequestWire("https://example.test/", "POST", SeoHttp.Browser, 1024, null,
            new byte[SeoFetchLimits.MaxRequestBodyBytes + 1])))).Should().Be(HttpStatusCode.BadRequest);
        (await PostAsync(SeoFetcherProtocol.FetchPath, new StringContent("not json", Encoding.UTF8, "application/json"))).Should().Be(HttpStatusCode.BadRequest);
        // Only http(s), only the five clients, only their methods, no body outside the browser, no user info, sane headers.
        (await PostAsync(SeoFetcherProtocol.FetchPath, Json(new FetchRequestWire("file:///etc/passwd", "GET", SeoHttp.Crawler, 1024, null, null)))).Should().Be(HttpStatusCode.BadRequest);
        (await PostAsync(SeoFetcherProtocol.FetchPath, Json(new FetchRequestWire("gopher://example.test/", "GET", SeoHttp.Crawler, 1024, null, null)))).Should().Be(HttpStatusCode.BadRequest);
        (await PostAsync(SeoFetcherProtocol.FetchPath, Json(new FetchRequestWire("https://example.test/", "GET", "Spark.SafeHttp", 1024, null, null)))).Should().Be(HttpStatusCode.BadRequest);
        (await PostAsync(SeoFetcherProtocol.FetchPath, Json(new FetchRequestWire("https://example.test/", "POST", SeoHttp.Crawler, 1024, null, null)))).Should().Be(HttpStatusCode.BadRequest);
        (await PostAsync(SeoFetcherProtocol.FetchPath, Json(new FetchRequestWire("https://example.test/", "GET", SeoHttp.Crawler, 1024, null, [1, 2])))).Should().Be(HttpStatusCode.BadRequest);
        (await PostAsync(SeoFetcherProtocol.FetchPath, Json(new FetchRequestWire("https://user:pw@example.test/", "GET", SeoHttp.Crawler, 1024, null, null)))).Should().Be(HttpStatusCode.BadRequest);
        (await PostAsync(SeoFetcherProtocol.FetchPath, Json(new FetchRequestWire("https://example.test/", "GET", SeoHttp.Crawler, 1024,
            [new HeaderWire("X-Bad", "a\r\nInjected: yes")], null)))).Should().Be(HttpStatusCode.BadRequest);
        (await PostAsync(SeoFetcherProtocol.DnsPath, Json(new DnsRequestWire("bad name;", SeoDnsType.Txt)))).Should().Be(HttpStatusCode.BadRequest);
        (await PostAsync(SeoFetcherProtocol.TlsPath, Json(new TlsRequestWire("example.test", 70000)))).Should().Be(HttpStatusCode.BadRequest);
        web.Requests.Should().BeEmpty();

        // The app's client turns a refused request into a refused fetch, as the in-process client does.
        var refused = await fetcher.Remote().FetchAsync(new SeoFetchRequest(new Uri("https://example.test/"), 1024) { Method = "DELETE" }, CancellationToken.None);
        refused.StatusCode.Should().Be(0);
        refused.Refused.Should().BeTrue();
    }

    [Fact]
    public async Task A_slow_target_ends_at_the_fetchers_bound_and_a_slow_fetcher_at_the_apps()
    {
        var web = new FakeWeb();
        web.Html("https://slow.test/", "<html></html>", delay: TimeSpan.FromSeconds(20));
        await using var fetcher = await FetcherTestHost.StartAsync(web, settings: new Dictionary<string, string?>
        {
            [SeoTester.Fetcher.FetcherHost.OperationTimeoutSetting] = "00:00:01",
        });

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var result = await fetcher.Remote().FetchAsync(new Uri("https://slow.test/"), 1024, CancellationToken.None);
        watch.Stop();
        result.StatusCode.Should().Be(0);
        result.Error.Should().Be("timed out");
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10), "the fetcher's own bound (1 s here) ended the call, not the target");

        // The app's deadline: the call's bound plus the margin (here negative, leaving 0.5 s) ends a fetcher that hangs.
        await using var hanging = await FetcherTestHost.StartAsync(web);
        var impatient = hanging.Remote(margin: -(SeoFetchLimits.OperationTimeout - TimeSpan.FromMilliseconds(500)));
        watch.Restart();
        await ((Func<Task>)(() => impatient.FetchAsync(new Uri("https://slow.test/"), 1024, CancellationToken.None)))
            .Should().ThrowAsync<SeoFetcherUnavailableException>();
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Redirects_are_checked_again_inside_the_fetcher()
    {
        var web = new FakeWeb();
        web.Redirect("https://example.test/to-port", "https://example.test:8443/admin");
        web.Redirect("https://example.test/to-file", "file:///etc/passwd");
        web.Redirect("https://example.test/away", "https://elsewhere.test/proof");
        web.Html("https://elsewhere.test/proof", "<html></html>");
        for (var i = 0; i < 7; i++)
            web.Redirect($"https://example.test/loop{i}", $"https://example.test/loop{i + 1}");
        await using var fetcher = await FetcherTestHost.StartAsync(web);
        var remote = fetcher.Remote();

        (await remote.FetchAsync(new Uri("https://example.test/to-port"), 1024, CancellationToken.None)).Error.Should().Be("refused: Port");
        (await remote.FetchAsync(new Uri("https://example.test/to-file"), 1024, CancellationToken.None)).Error.Should().Be("refused: Scheme");
        (await remote.FetchAsync(new Uri("https://example.test/loop0"), 1024, CancellationToken.None)).Error.Should().Be("refused: TooManyRedirects");
        (await remote.FetchAsync(new Uri("https://example.test/away"), 1024, CancellationToken.None, client: SeoHttp.Verification)).Error
            .Should().Be("refused: CrossOriginRedirect");
        web.RequestsTo("elsewhere.test").Should().BeEmpty("a proof must come from the site itself");
        // The browser's client hands a redirect back as it is (Chromium asks for the target, which is checked then).
        var hop = await remote.FetchAsync(new SeoFetchRequest(new Uri("https://example.test/away"), 1024) { Client = SeoHttp.Browser }, CancellationToken.None);
        hop.StatusCode.Should().Be(302);
        hop.Header("location").Should().Be("https://elsewhere.test/proof");
    }

    [Fact]
    public async Task The_fetcher_cannot_be_pointed_at_private_addresses_even_through_a_redirect()
    {
        // Real sockets, no fake web: 127.0.0.2 stands in for a public site (the one network allowed), 127.0.0.1 for
        // what the fetcher must never reach (RavenDB, the relay, the app). Both listen on ports the rules allow.
        using var inside = new LoopbackHttpServer(IPAddress.Loopback, _ => LoopbackHttpServer.Ok("secret"));
        using var outside = new LoopbackHttpServer(IPAddress.Parse("127.0.0.2"), path => path == "/hop"
            ? LoopbackHttpServer.Redirect($"http://127.0.0.1:{inside.Port}/secret")
            : LoopbackHttpServer.Ok("public"));
        await using var fetcher = await FetcherTestHost.StartAsync(settings: new Dictionary<string, string?>
        {
            ["Spark:SafeHttp:AllowedPorts:0"] = inside.Port.ToString(),
            ["Spark:SafeHttp:AllowedPorts:1"] = outside.Port.ToString(),
            ["Spark:SafeHttp:AllowedNetworks:0"] = "127.0.0.2/32",
        });
        var remote = fetcher.Remote();

        var allowed = await remote.FetchAsync(new Uri($"http://127.0.0.2:{outside.Port}/"), 1024, CancellationToken.None);
        allowed.StatusCode.Should().Be(200, allowed.Error);
        Encoding.UTF8.GetString(allowed.Body!).Should().Be("public");

        foreach (var url in new[]
                 {
                     $"http://127.0.0.1:{inside.Port}/secret",
                     $"http://localhost:{inside.Port}/secret",
                     $"http://[::ffff:127.0.0.1]:{inside.Port}/secret",
                     $"http://169.254.169.254:{inside.Port}/latest/meta-data/",
                     $"http://10.0.0.1:{inside.Port}/",
                 })
            (await remote.FetchAsync(new Uri(url), 1024, CancellationToken.None)).Error.Should().Be("refused: Address", url);

        var redirected = await remote.FetchAsync(new Uri($"http://127.0.0.2:{outside.Port}/hop"), 1024, CancellationToken.None);
        redirected.Error.Should().Be("refused: Address", "the redirect's target is checked again");
        redirected.Hops.Should().ContainSingle();

        var tls = await remote.ProbeTlsAsync("127.0.0.1", inside.Port, CancellationToken.None);
        tls.Handshake.Should().BeFalse();
        tls.Error.Should().Be("refused: Address");

        inside.Connections.Should().Be(0, "no request, redirect or TLS probe reached the private address");
        outside.Connections.Should().Be(2);
    }

    [Fact]
    public async Task Tls_and_dns_answers_cross_the_api_intact()
    {
        var dns = new FakeDns();
        dns.Txt["_seotester.example.test"] = ["seotester-verification=abc"];
        var tls = new FakeTls();
        tls.Hosts["example.test"] = FakeTls.Good(new DateTime(2027, 1, 2, 3, 4, 5, DateTimeKind.Utc));
        await using var fetcher = await FetcherTestHost.StartAsync(new FakeWeb(), dns, tls);
        var remote = fetcher.Remote();

        (await remote.GetTxtRecordsAsync("_seotester.example.test", CancellationToken.None)).Should().Equal("seotester-verification=abc");
        (await remote.QueryDnsAsync("nothing.example.test", SeoDnsType.Caa, CancellationToken.None)).Should().BeEmpty();
        var facts = await remote.ProbeTlsAsync("example.test", 443, CancellationToken.None);
        facts.Should().Be(FakeTls.Good(new DateTime(2027, 1, 2, 3, 4, 5, DateTimeKind.Utc)));
    }
}
