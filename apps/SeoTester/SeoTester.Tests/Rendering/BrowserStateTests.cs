using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;
using SeoTester.Crawler;
using SeoTester.Crawling;
using SeoTester.Dns;
using SeoTester.Fetching;
using SeoTester.Rendering;
using SeoTester.Tests._Infrastructure;
using Xunit.Abstractions;

namespace SeoTester.Tests.Rendering;

/// <summary>
/// A site cannot leave anything in SeoTester's browser for a later render, and cannot make it save a file
/// (<c>docs/seo_analyzer_PRD.md</c> §12, "Untrusted content: proof tests"). The real <see cref="PageRenderer"/> over one
/// launched Chromium, every request routed through the fake web, which records the header names each request carried.
/// Needs Playwright's Chromium (<c>playwright.ps1 install chromium</c>).
/// </summary>
[Collection(BrowserCollection.Name)]
public sealed class BrowserStateTests(ITestOutputHelper output) : IAsyncLifetime
{
    private readonly FakeWeb web = new();
    private ServiceProvider services = null!;
    private ChromiumBrowser chromium = null!;

    public Task InitializeAsync()
    {
        var collection = new ServiceCollection();
        collection.AddLogging();
        collection.AddSingleton<ISeoDns>(new FakeDns());
        collection.AddSingleton<ITlsProber>(new FakeTls());
        collection.AddSeoFetching(new ConfigurationBuilder().Build());
        foreach (var client in SeoHttp.MaxBodyBytes.Keys)
            collection.AddHttpClient(client).ConfigurePrimaryHttpMessageHandler(web.CreateHandler);
        services = collection.BuildServiceProvider();
        chromium = new ChromiumBrowser(Options.Create(new BrowserOptions { Mode = BrowserMode.Launch, SettleTimeout = TimeSpan.FromSeconds(2) }),
            NullLogger<ChromiumBrowser>.Instance);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await chromium.DisposeAsync();
        await services.DisposeAsync();
    }

    private async Task<PageRender> RenderAsync(Uri url)
    {
        using var pacer = new HostPacer(4, TimeSpan.Zero, null, TimeProvider.System);
        using var network = new RenderNetwork(services.GetRequiredService<ISeoFetcher>(), new Uri(url.GetLeftPart(UriPartial.Authority)), RobotsTxt.AllowAll, pacer, NullLogger.Instance);
        return await new PageRenderer(chromium, NullLogger<PageRenderer>.Instance).RenderAsync(network, url, probeRoute: false, CancellationToken.None);
    }

    /// <summary>Reports what the page can see of earlier visits: cookies, both storages, IndexedDB and the Cache API.</summary>
    private const string Report = """
        <script>
          window.report = async () => {
            const r = { phase: document.documentElement.dataset.phase };
            r.cookie = document.cookie;
            try { r.ls = Object.keys(localStorage).join(','); } catch (e) { r.ls = 'error'; }
            try { r.ss = Object.keys(sessionStorage).join(','); } catch (e) { r.ss = 'error'; }
            try { r.idb = (await indexedDB.databases()).map(d => d.name).join(','); } catch (e) { r.idb = 'error'; }
            try { r.caches = (await caches.keys()).join(','); } catch (e) { r.caches = 'error'; }
            await fetch('/report?' + new URLSearchParams(r));
          };
        </script>
        """;

    /// <summary>
    /// Render 1 of site A gets a cookie from the server and one from script, and writes localStorage, sessionStorage,
    /// IndexedDB and the Cache API (and proves they took: its own report, and its next request carrying the cookies).
    /// Render 2, of the same site and of another site on the same browser, sees none of it, and no request of render 2
    /// carries a <c>Cookie</c> header, including a third-party request from site B back to site A. Red when the
    /// renderer reuses one browser context across renders (render 2 reports the cookies and storage, and its requests
    /// carry <c>Cookie</c>).
    /// </summary>
    [Fact]
    public async Task Nothing_a_site_stores_in_one_render_reaches_the_next()
    {
        var a = "https://" + FakeSite.NewHost();
        var b = "https://" + FakeSite.NewHost();
        web.Html(a + "/", $$"""
            <!doctype html><html lang="en" data-phase="set"><head><title>Sets state</title></head><body><h1>Sets state</h1>{{Report}}
            <script>
              (async () => {
                document.cookie = 'js=evil; path=/; max-age=3600; secure; samesite=none';
                localStorage.setItem('ls-evil', '1');
                sessionStorage.setItem('ss-evil', '1');
                await new Promise((resolve) => {
                  const open = indexedDB.open('idb-evil', 1);
                  open.onupgradeneeded = () => open.result.createObjectStore('s');
                  open.onsuccess = () => { const tx = open.result.transaction('s', 'readwrite'); tx.objectStore('s').put('evil', 'k'); tx.oncomplete = resolve; tx.onerror = resolve; };
                  open.onerror = resolve;
                });
                try { const c = await caches.open('cache-evil'); await c.put('/cached', new Response('evil')); } catch (e) { }
                await fetch('/after-set');
                await report();
              })();
            </script></body></html>
            """, new Dictionary<string, string> { ["Set-Cookie"] = "srv=evil; Path=/; Max-Age=3600; Secure; SameSite=None" });
        web.Html(a + "/again", $"""<!doctype html><html lang="en" data-phase="again"><head><title>Again</title></head><body><h1>Again</h1>{Report}<script>report();</script></body></html>""");
        web.Html(b + "/", $"""
            <!doctype html><html lang="en" data-phase="other"><head><title>Other site</title></head><body><h1>Other site</h1>
            <img src="{a}/pixel.gif" alt="">{Report}<script>report();</script></body></html>
            """);

        var first = await RenderAsync(new Uri(a + "/"));
        first.Facts.Error.Should().BeNull();
        var set = Reports("set");
        set.Should().HaveCount(2, "the phone and the desktop render each report");
        set.Should().OnlyContain(r => r["cookie"].Contains("srv=evil") && r["cookie"].Contains("js=evil") && r["ls"] == "ls-evil" && r["ss"] == "ss-evil"
            && r["idb"] == "idb-evil" && r["caches"] == "cache-evil", "render 1 really stored everything (the control)");
        web.Requests.Where(r => r.Url == a + "/after-set").Should().OnlyContain(r => r.HeaderNames!.Contains("Cookie", StringComparer.OrdinalIgnoreCase),
            "within one render the browser sends its cookies back (the control)");
        var before = web.Requests.Count;

        (await RenderAsync(new Uri(a + "/again"))).Facts.Error.Should().BeNull();
        (await RenderAsync(new Uri(b + "/"))).Facts.Error.Should().BeNull();

        var later = Reports("again").Concat(Reports("other")).ToList();
        foreach (var r in later)
            output.WriteLine(string.Join(" ", r.Select(p => $"{p.Key}={p.Value}")));
        later.Should().HaveCount(4);
        later.Should().OnlyContain(r => r["cookie"] == "" && r["ls"] == "" && r["ss"] == "" && r["idb"] == "" && r["caches"] == "");
        var requests = web.Requests.Skip(before).ToList();
        requests.Should().Contain(r => r.Url == a + "/pixel.gif", "site B's page asked site A for an image");
        requests.Where(r => r.HeaderNames!.Contains("Cookie", StringComparer.OrdinalIgnoreCase)).Select(r => r.Url).Should().BeEmpty(
            "no request of a later render carries a cookie");
    }

    private List<Dictionary<string, string>> Reports(string phase)
        => web.Requests.Select(r => new Uri(r.Url)).Where(u => u.AbsolutePath == "/report")
            .Select(u => u.Query.TrimStart('?').Split('&').Select(p => p.Split('=', 2))
                .ToDictionary(p => Uri.UnescapeDataString(p[0]), p => Uri.UnescapeDataString(p.Length > 1 ? p[1].Replace('+', ' ') : "")))
            .Where(r => r.GetValueOrDefault("phase") == phase)
            .ToList();

    /// <summary>
    /// A page that tries three ways to save a file: a frame answered with <c>Content-Disposition: attachment</c>, a
    /// scripted click on <c>&lt;a download&gt;</c> (a served file and a blob), and a navigation to an
    /// <c>application/octet-stream</c>. No download completes and no file holding the marker appears in the
    /// browser's temp folders or the user's Downloads folder; the render finishes normally and the next one works. Red
    /// with <c>AcceptDownloads = true</c>: the files land in Playwright's artifacts folder.
    /// </summary>
    [Fact]
    public async Task A_page_cannot_make_the_browser_save_a_file()
    {
        var marker = "download-marker-" + Guid.NewGuid().ToString("N");
        var site = "https://" + FakeSite.NewHost();
        var started = DateTime.UtcNow.AddSeconds(-2);
        web.Html(site + "/", $$"""
            <!doctype html><html lang="en"><head><title>Downloads</title></head><body><h1>Downloads</h1>
            <iframe src="/attachment"></iframe>
            <script>
              const click = (href, name) => { const a = document.createElement('a'); a.href = href; a.download = name; document.body.appendChild(a); a.click(); };
              click('/file.bin', '{{marker}}-click.bin');
              click(URL.createObjectURL(new Blob(['{{marker}} blob'], { type: 'application/octet-stream' })), '{{marker}}-blob.bin');
              setTimeout(() => { location.href = '/octet'; }, 300);
            </script></body></html>
            """);
        var body = Encoding.UTF8.GetBytes(marker + " body");
        web.Pages[site + "/attachment"] = new FakeResponse(HttpStatusCode.OK, "", ContentType: "text/plain", Bytes: body,
            Headers: new Dictionary<string, string> { ["Content-Disposition"] = $"attachment; filename=\"{marker}-attachment.exe\"" });
        web.Pages[site + "/file.bin"] = new FakeResponse(HttpStatusCode.OK, "", ContentType: "application/octet-stream", Bytes: body);
        web.Pages[site + "/octet"] = new FakeResponse(HttpStatusCode.OK, "", ContentType: "application/octet-stream", Bytes: body);
        web.Html(site + "/next", FakeSite.Page("After the downloads"));

        var browser = await chromium.GetAsync(CancellationToken.None);
        var downloads = new ConcurrentQueue<string>();
        using var stop = new CancellationTokenSource();
        var watcher = WatchDownloadsAsync(browser, downloads, stop.Token);
        // Playwright deletes a context's downloads when the context closes, so the folders are scanned all along.
        var files = new ConcurrentDictionary<string, byte>();
        var scanner = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                foreach (var file in FilesHolding(marker, started, tempTopLevel: false))
                    files.TryAdd(file, 0);
                await Task.Delay(50);
            }
        });

        var render = await RenderAsync(new Uri(site + "/"));
        await Task.Delay(1500);
        foreach (var file in FilesHolding(marker, started, tempTopLevel: true))
            files.TryAdd(file, 0);
        stop.Cancel();
        await watcher;
        await scanner;

        foreach (var d in downloads)
            output.WriteLine("download event: " + d);
        render.Facts.Error.Should().BeNull("the render goes on as if nothing happened");
        render.Facts.Mobile.Should().NotBeNull();
        web.Requests.Should().Contain(r => r.Url == site + "/attachment");
        files.Keys.Should().BeEmpty("no file holding what the site served was written");
        downloads.Should().NotContain(d => d.EndsWith(" completed"), "Chromium announces some downloads, Playwright cancels them all");
        (await RenderAsync(new Uri(site + "/next"))).Facts.Error.Should().BeNull("the next render works");
    }

    /// <summary>Records every download the browser announces on any page of any context, with how it ended.</summary>
    private static async Task WatchDownloadsAsync(IBrowser browser, ConcurrentQueue<string> downloads, CancellationToken stop)
    {
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        void Watch(IPage page)
        {
            if (!seen.Add(page))
                return;
            page.Download += async (_, download) =>
            {
                string? failure;
                try
                {
                    failure = await download.FailureAsync();
                }
                catch (PlaywrightException ex)
                {
                    failure = ex.Message;
                }
                downloads.Enqueue($"{download.SuggestedFilename} {(failure is null ? "completed" : "failed: " + failure)}");
            };
        }
        while (!stop.IsCancellationRequested)
        {
            foreach (var context in browser.Contexts)
            {
                if (seen.Add(context))
                    context.Page += (_, page) => Watch(page);
                foreach (var page in context.Pages)
                    Watch(page);
            }
            try
            {
                await Task.Delay(10, stop);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    /// <summary>
    /// Files written since <paramref name="since"/> whose name or bytes hold <paramref name="marker"/>, in every folder a
    /// browser could save to: Playwright's artifacts folders (its download folder) and the rest of the temp folder's
    /// top level, and the user's Downloads folder.
    /// </summary>
    private static List<string> FilesHolding(string marker, DateTime since, bool tempTopLevel)
    {
        var bytes = Encoding.UTF8.GetBytes(marker);
        var temp = Path.GetTempPath();
        var places = new List<(string Dir, SearchOption Depth)>();
        if (tempTopLevel)
            places.Add((temp, SearchOption.TopDirectoryOnly));
        places.AddRange(Directory.GetDirectories(temp, "playwright-artifacts-*").Select(d => (d, SearchOption.AllDirectories)));
        var userDownloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        if (Directory.Exists(userDownloads))
            places.Add((userDownloads, SearchOption.TopDirectoryOnly));

        var found = new List<string>();
        foreach (var (dir, depth) in places)
        {
            IEnumerable<string> candidates;
            try
            {
                candidates = Directory.EnumerateFiles(dir, "*", new EnumerationOptions { RecurseSubdirectories = depth == SearchOption.AllDirectories, IgnoreInaccessible = true });
            }
            catch (IOException)
            {
                continue;
            }
            foreach (var file in candidates)
            {
                try
                {
                    var info = new FileInfo(file);
                    if (info.LastWriteTimeUtc < since && info.CreationTimeUtc < since)
                        continue;
                    if (info.Name.Contains(marker, StringComparison.Ordinal)
                        || (info.Length < 1024 * 1024 && File.ReadAllBytes(file).AsSpan().IndexOf(bytes) >= 0))
                        found.Add(file);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // In use by another process, or gone already: not ours.
                }
            }
        }
        return found;
    }
}
