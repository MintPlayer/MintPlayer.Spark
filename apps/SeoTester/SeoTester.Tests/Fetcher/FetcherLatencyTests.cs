using System.Diagnostics;
using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SeoTester.Crawler;
using SeoTester.Fetching;
using SeoTester.Tests._Infrastructure;
using Xunit.Abstractions;

namespace SeoTester.Tests.Fetcher;

/// <summary>
/// What the fetcher hop adds to one routed browser request (<c>docs/seo_analyzer_PRD.md</c> D54): the request
/// <see cref="Rendering.RenderNetwork"/> makes for every request Chromium sends, in process and through the fetcher's
/// API over real loopback sockets (Kestrel), against the same fake web, so the difference is the hop alone: one HTTP
/// exchange on the internal network, the JSON request and the framed answer. Prints the measurement; the bound only
/// catches a hop gone badly wrong.
/// </summary>
public class FetcherLatencyTests(ITestOutputHelper output)
{
    [Fact]
    public async Task The_fetcher_hop_adds_little_to_a_routed_request()
    {
        var web = new FakeWeb();
        var sizes = new (string Path, int Bytes, string Type)[]
        {
            ("/page", 20 * 1024, "text/html"),
            ("/app.js", 150 * 1024, "application/javascript"),
            ("/hero.png", 1024 * 1024, "image/png"),
        };
        foreach (var (path, bytes, type) in sizes)
            web.Pages["https://latency.test" + path] = new FakeResponse(HttpStatusCode.OK, "", ContentType: type, Bytes: new byte[bytes]);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSeoFetching(new ConfigurationBuilder().Build());
        foreach (var client in SeoHttp.MaxBodyBytes.Keys)
            services.AddHttpClient(client).ConfigurePrimaryHttpMessageHandler(web.CreateHandler);
        await using var provider = services.BuildServiceProvider();
        var local = provider.GetRequiredService<ISeoFetcher>();
        await using var fetcher = await FetcherTestHost.StartKestrelAsync(web);
        var remote = fetcher.Remote();

        var lines = new List<string> { "size | in process p50 / p95 ms | through the fetcher p50 / p95 ms | added p50 ms" };
        var worst = 0.0;
        foreach (var (path, bytes, _) in sizes)
        {
            var request = new SeoFetchRequest(new Uri("https://latency.test" + path), SeoHttp.MaxBodyBytes[SeoHttp.Browser])
            {
                Client = SeoHttp.Browser,
                Headers = [new("accept", "*/*"), new("user-agent", "Mozilla/5.0 " + SeoTesterBot.UserAgent)],
            };
            var a = await MeasureAsync(local, request);
            var b = await MeasureAsync(remote, request);
            var added = b.P50 - a.P50;
            worst = Math.Max(worst, added);
            lines.Add($"{bytes / 1024} KB | {a.P50:F2} / {a.P95:F2} | {b.P50:F2} / {b.P95:F2} | {added:F2}");
        }
        foreach (var line in lines)
            output.WriteLine(line);

        worst.Should().BeLessThan(100, "one loopback exchange per routed request");
    }

    private static async Task<(double P50, double P95)> MeasureAsync(ISeoFetcher fetcher, SeoFetchRequest request)
    {
        for (var i = 0; i < 20; i++)
            (await fetcher.FetchAsync(request, CancellationToken.None)).StatusCode.Should().Be(200);
        var samples = new List<double>();
        for (var i = 0; i < 200; i++)
        {
            var watch = Stopwatch.StartNew();
            var result = await fetcher.FetchAsync(request, CancellationToken.None);
            samples.Add(watch.Elapsed.TotalMilliseconds);
            result.StatusCode.Should().Be(200);
        }
        samples.Sort();
        return (samples[samples.Count / 2], samples[(int)(samples.Count * 0.95)]);
    }
}
