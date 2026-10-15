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
/// launched Chromium with a temp folder of its own, every request routed through the fake web, which records the header
/// names each request carried. Needs Playwright's Chromium (<c>playwright.ps1 install chromium</c>).
/// </summary>
[Collection(BrowserCollection.Name)]
public sealed class BrowserStateTests(ITestOutputHelper output) : IAsyncLifetime
{
    /// <summary>Where Playwright's driver (node's <c>os.tmpdir()</c>) and Chromium take their temp folder from, on any OS.</summary>
    private static readonly string[] TempVariables = ["TMPDIR", "TMP", "TEMP"];

    /// <summary>A failure bound only: a scan takes milliseconds.</summary>
    private static readonly TimeSpan ScanBound = TimeSpan.FromMinutes(1);

    private readonly FakeWeb web = new();
    private ServiceProvider services = null!;
    private ChromiumBrowser chromium = null!;
    private DirectoryInfo browserTemp = null!;

    public async Task InitializeAsync()
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
        browserTemp = Directory.CreateTempSubdirectory("seo-browser-");
        await LaunchAsync();
    }

    public async Task DisposeAsync()
    {
        await chromium.DisposeAsync();
        await services.DisposeAsync();
        try
        {
            browserTemp.Delete(recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A browser process still on its way out holds a file: the folder stays behind.
        }
    }

    /// <summary>
    /// Launches the browser with <see cref="browserTemp"/> as its temp folder: its profile, Playwright's download folder
    /// and Chromium's temp files land there, and the download proof scans that folder instead of the one every process on
    /// the machine shares (<c>docs/seo_analyzer_PRD.md</c> §12, "CI stall"). The driver and Chromium take the variables
    /// from this process's environment as they start, so they are set for that moment only: no other test runs meanwhile
    /// (<c>AssemblyInfo.cs</c>).
    /// </summary>
    private async Task LaunchAsync()
    {
        var saved = TempVariables.Select(name => (Name: name, Value: Environment.GetEnvironmentVariable(name))).ToList();
        try
        {
            foreach (var name in TempVariables)
                Environment.SetEnvironmentVariable(name, browserTemp.FullName);
            await chromium.GetAsync(CancellationToken.None);
        }
        finally
        {
            foreach (var (name, value) in saved)
                Environment.SetEnvironmentVariable(name, value);
        }
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
    /// A page that tries three routed ways to save a file: a frame answered with <c>Content-Disposition: attachment</c>, a
    /// scripted click on <c>&lt;a download&gt;</c> of a served file, and a navigation to an <c>application/octet-stream</c>.
    /// Chromium announces no download of a routed response, and not one file is created in the browser's temp folder, not
    /// even for a moment (off Windows, Chromium's own temp files apart: <see cref="CreatedFiles"/>): the router never hands
    /// Chromium a response it would save (<c>docs/seo_analyzer_PRD.md</c> §12, "Downloads never reach Chromium"). None holds the marker,
    /// no download completes, the page stays as it was and the next render works. The <c>&lt;a download&gt;</c> is not routed
    /// at all (Chromium's download manager fetches it itself), and the dead proxy refuses it: the site never sees it. Red
    /// before the router stopped downloads: Chromium created a <c>&lt;guid&gt;.tmp</c> holding each body. Red with
    /// <c>AcceptDownloads = true</c>: the files land in Playwright's download folder.
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
              const a = document.createElement('a'); a.href = '/file.bin'; a.download = '{{marker}}-click.bin'; document.body.appendChild(a); a.click();
              setTimeout(() => { location.href = '/octet'; }, 300);
            </script></body></html>
            """);
        var body = Encoding.UTF8.GetBytes(marker + " body");
        web.Pages[site + "/attachment"] = new FakeResponse(HttpStatusCode.OK, "", ContentType: "text/plain", Bytes: body,
            Headers: new Dictionary<string, string> { ["Content-Disposition"] = $"attachment; filename=\"{marker}-attachment.exe\"" });
        web.Pages[site + "/file.bin"] = new FakeResponse(HttpStatusCode.OK, "", ContentType: "application/octet-stream", Bytes: body);
        web.Pages[site + "/octet"] = new FakeResponse(HttpStatusCode.OK, "", ContentType: "application/octet-stream", Bytes: body);
        web.Html(site + "/next", FakeSite.Page("After the downloads"));

        var left = await RenderWatchedAsync(new Uri(site + "/"), marker, started);

        left.Render.Facts.Error.Should().BeNull("the render goes on as if nothing happened");
        left.Render.Facts.Mobile!.Title.Should().Be("Downloads", "the page stays as it was");
        left.Created.Should().BeEmpty("no file is created in the browser's temp folder, not even for a moment");
        left.Downloads.Should().OnlyContain(d => d.StartsWith(marker + "-click.bin ", StringComparison.Ordinal),
            "Chromium starts no download of what the router answers: only the unrouted <a download>, which the dead proxy fails");
        left.HeldMarker.Should().BeEmpty("no file holding what the site served reached Playwright's download folder or is left anywhere");
        left.Downloads.Should().NotContain(d => d.EndsWith(" completed"), "no download completes");
        left.Render.Facts.Network.Should().Contain(n => n.Url == site + "/attachment" && n.Outcome == "download", "the frame's attachment never reached Chromium");
        left.Render.Facts.Network.Should().Contain(n => n.Url == site + "/octet" && n.Outcome == "download", "nor did the navigation's file");
        web.Requests.Should().NotContain(r => r.Url == site + "/file.bin", "an <a download> is not routed, and the dead proxy refuses it");
        (await RenderAsync(new Uri(site + "/next"))).Facts.Error.Should().BeNull("the next render works");
    }

    /// <summary>
    /// A <c>blob:</c> and a <c>data:</c> download never reach the router: Chromium serves those addresses itself, so it still
    /// writes them to a temp file of its own before Playwright refuses the download (seen 2026-10-14: a <c>&lt;guid&gt;.tmp</c>
    /// holding the blob, deleted 0 to 23 ms later). For them only "nothing left" can hold: no file holding the marker remains
    /// once the render is over, none reaches Playwright's download folder, and no download completes.
    /// </summary>
    [Fact]
    public async Task A_blob_or_data_download_leaves_nothing_behind()
    {
        var marker = "download-marker-" + Guid.NewGuid().ToString("N");
        var site = "https://" + FakeSite.NewHost();
        var started = DateTime.UtcNow.AddSeconds(-2);
        web.Html(site + "/", $$"""
            <!doctype html><html lang="en"><head><title>Downloads</title></head><body><h1>Downloads</h1>
            <script>
              const click = (href, name) => { const a = document.createElement('a'); a.href = href; a.download = name; document.body.appendChild(a); a.click(); };
              click(URL.createObjectURL(new Blob(['{{marker}} blob'], { type: 'application/octet-stream' })), '{{marker}}-blob.bin');
              click('data:application/octet-stream;base64,' + btoa('{{marker}} data'), '{{marker}}-data.bin');
            </script></body></html>
            """);

        var left = await RenderWatchedAsync(new Uri(site + "/"), marker, started);

        left.Render.Facts.Error.Should().BeNull("the render goes on as if nothing happened");
        left.HeldMarker.Should().BeEmpty("no file holding what the page made reached Playwright's download folder or is left anywhere");
        left.Downloads.Should().NotContain(d => d.EndsWith(" completed"), "no download completes");
    }

    /// <summary>
    /// A render of an address that answers a file to download (a PDF: headless Chromium has no viewer) fails with that
    /// reason, without a hang, and creates nothing in the browser's temp folder; the next render works. Red before the router
    /// stopped downloads: "the page could not be loaded" (Playwright's "Download is starting") and a <c>&lt;guid&gt;.tmp</c>
    /// holding the PDF.
    /// </summary>
    [Fact]
    public async Task A_page_that_is_a_download_is_a_handled_render_failure()
    {
        var marker = "download-marker-" + Guid.NewGuid().ToString("N");
        var site = "https://" + FakeSite.NewHost();
        var started = DateTime.UtcNow.AddSeconds(-2);
        web.Pages[site + "/report.pdf"] = new FakeResponse(HttpStatusCode.OK, "", ContentType: "application/pdf", Bytes: Encoding.UTF8.GetBytes("%PDF-1.4\n" + marker));
        web.Html(site + "/next", FakeSite.Page("After the download"));

        var left = await RenderWatchedAsync(new Uri(site + "/report.pdf"), marker, started);

        left.Render.Facts.Error.Should().Be("the page is a file to download, not a document");
        left.Render.Facts.Mobile.Should().BeNull();
        left.Render.Facts.Network.Should().Contain(n => n.Url == site + "/report.pdf" && n.Outcome == "download");
        left.Created.Should().BeEmpty("no file is created in the browser's temp folder, not even for a moment");
        left.Downloads.Should().BeEmpty("Chromium starts no download: the router never hands it the file");
        left.HeldMarker.Should().BeEmpty();
        (await RenderAsync(new Uri(site + "/next"))).Facts.Error.Should().BeNull("the next render works");
    }

    /// <summary>
    /// A loaded page that navigates to a file to download keeps the rendered page as it was: no error, its title and its
    /// heading, and nothing created in the browser's temp folder. Chromium kept the page before too (a download does not
    /// replace it); red before the router stopped downloads on the file it created and the navigation's outcome.
    /// </summary>
    [Fact]
    public async Task A_navigation_to_a_download_leaves_the_rendered_page_as_it_was()
    {
        var marker = "download-marker-" + Guid.NewGuid().ToString("N");
        var site = "https://" + FakeSite.NewHost();
        var started = DateTime.UtcNow.AddSeconds(-2);
        web.Html(site + "/", """
            <!doctype html><html lang="en"><head><title>Stays</title></head><body><h1>Stays</h1><p>The page that was rendered.</p>
            <script>setTimeout(() => { location.href = '/later.zip'; }, 100);</script></body></html>
            """);
        web.Pages[site + "/later.zip"] = new FakeResponse(HttpStatusCode.OK, "", ContentType: "application/zip", Bytes: [.. "PK\x03\x04"u8, .. Encoding.UTF8.GetBytes(marker)]);

        var left = await RenderWatchedAsync(new Uri(site + "/"), marker, started);

        left.Render.Facts.Error.Should().BeNull();
        left.Render.Facts.Mobile!.Title.Should().Be("Stays");
        left.Render.Facts.Mobile.H1Count.Should().Be(1);
        left.Render.Facts.Network.Should().Contain(n => n.Url == site + "/later.zip" && n.Outcome == "download");
        left.Created.Should().BeEmpty("no file is created in the browser's temp folder, not even for a moment");
        left.Downloads.Should().BeEmpty("Chromium starts no download: the router never hands it the file");
        left.HeldMarker.Should().BeEmpty();
    }

    /// <summary>What one watched render left: the files that held the marker, every file created in the browser's temp folder, the downloads announced.</summary>
    private sealed record Leftovers(PageRender Render, IReadOnlyCollection<string> HeldMarker, IReadOnlyCollection<string> Created, IReadOnlyCollection<string> Downloads);

    /// <summary>
    /// Renders <paramref name="url"/> while watching everywhere the browser could save to. Playwright's download folder is
    /// scanned all along (Playwright deletes a context's downloads when the context closes); the browser's whole temp folder
    /// and the user's Downloads folder once the render is over; and every file created in the temp folder is recorded.
    /// </summary>
    private async Task<Leftovers> RenderWatchedAsync(Uri url, string marker, DateTime started)
    {
        var browser = await chromium.GetAsync(CancellationToken.None);
        var downloadFolders = browserTemp.GetDirectories("playwright-artifacts-*").Select(d => (d.FullName, SearchOption.AllDirectories)).ToList();
        downloadFolders.Should().NotBeEmpty("Playwright's download folder is in the browser's temp folder, the one watched (else the scans prove nothing)");
        var downloads = new ConcurrentQueue<string>();
        var files = new ConcurrentDictionary<string, byte>();
        using var created = new CreatedFiles(browserTemp);
        using var stop = new CancellationTokenSource();
        var watcher = WatchDownloadsAsync(browser, downloads, stop.Token);
        var scanner = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                foreach (var file in MarkerFiles.Holding(marker, started, downloadFolders))
                    files.TryAdd(file, 0);
                await Task.Delay(50);
            }
        });

        var render = await RenderAsync(url);
        await Task.Delay(1500);
        foreach (var file in await Task.Run(() => MarkerFiles.Holding(marker, started, Places())).WaitAsync(ScanBound))
            files.TryAdd(file, 0);
        stop.Cancel();
        await watcher;
        await scanner.WaitAsync(ScanBound);

        foreach (var d in downloads)
            output.WriteLine("download event: " + d);
        foreach (var path in created.Paths)
            output.WriteLine("created in the browser's temp folder: " + path);
        return new Leftovers(render, files.Keys.ToList(), created.Paths, downloads.ToList());
    }

    /// <summary>
    /// Every file created in a browser's temp folder while it watches, but in Chromium's profile: Chromium's own state (a
    /// render's contexts are off the record and keep what a site stores in memory). A lost event is recorded too.
    /// Off Windows, also not Chromium's <c>.org.chromium.Chromium.XXXXXX</c> files. On POSIX that is the name of every temp
    /// file Chromium makes, and Playwright launches it with <c>--disable-dev-shm-usage</c> (its <c>chromiumSwitches.js</c>),
    /// so its shared memory lives in this folder too. CI on Linux saw 8 to 23 per render, a page that never loaded included
    /// (run 38669888652). By name they cannot be told from a download's, so there the proof is that Chromium announces no
    /// download (<see cref="Leftovers.Downloads"/>). On Windows a temp file is a <c>&lt;guid&gt;.tmp</c> and the folder is
    /// otherwise quiet, so every file counts.
    /// </summary>
    private sealed class CreatedFiles : IDisposable
    {
        private readonly DirectoryInfo folder;
        private readonly FileSystemWatcher watcher;
        private readonly ConcurrentQueue<string> paths = new();

        public CreatedFiles(DirectoryInfo folder)
        {
            this.folder = folder;
            watcher = new FileSystemWatcher(folder.FullName) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName, InternalBufferSize = 64 * 1024 };
            watcher.Created += (_, e) => Add(e.FullPath);
            watcher.Renamed += (_, e) => Add(e.FullPath);
            watcher.Error += (_, e) => paths.Enqueue("(the watcher lost events: " + e.GetException().Message + ")");
            watcher.EnableRaisingEvents = true;
        }

        public IReadOnlyCollection<string> Paths => paths.ToArray();

        private void Add(string path)
        {
            var relative = Path.GetRelativePath(folder.FullName, path);
            if (relative.StartsWith("playwright_chromiumdev_profile-", StringComparison.Ordinal))
                return;
            if (!OperatingSystem.IsWindows() && Path.GetFileName(relative).StartsWith(".org.chromium.Chromium.", StringComparison.Ordinal))
                return;
            paths.Enqueue(relative);
        }

        public void Dispose() => watcher.Dispose();
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
    /// Every folder the browser could save to: its own temp folder, all of it (Playwright's download folder, the profile,
    /// Chromium's temp files), and the user's Downloads folder (Chromium's default).
    /// </summary>
    private List<(string Dir, SearchOption Depth)> Places()
    {
        var places = new List<(string Dir, SearchOption Depth)> { (browserTemp.FullName, SearchOption.AllDirectories) };
        var userDownloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        if (Directory.Exists(userDownloads))
            places.Add((userDownloads, SearchOption.TopDirectoryOnly));
        return places;
    }
}
