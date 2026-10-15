using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;
using MintPlayer.Spark.Http;
using SeoTester.Crawler;
using SeoTester.Crawling;
using SeoTester.Rendering;
using Xunit.Abstractions;

namespace SeoTester.Tests.Rendering;

/// <summary>
/// Spike S3's escape test (<c>docs/seo_analyzer_PRD.md</c> §6, §8): a page full of ways to reach a "private service"
/// (TCP and UDP listeners on this machine's loopback) is rendered in a local Chromium. Without SeoTester's layers the
/// listeners are reached; with them (every request routed through the SSRF-safe client, WebSockets closed before they
/// connect, the init script, the hardening flags), over a launched browser and over <c>ConnectAsync</c> to a
/// run-server, nothing arrives. Needs Playwright's Chromium (<c>playwright.ps1 install chromium</c>); no Docker.
/// </summary>
[Collection(BrowserCollection.Name)]
public sealed class BrowserEscapeTests(ITestOutputHelper output) : IAsyncLifetime
{
    private readonly TcpListener tcp = new(IPAddress.Loopback, 0);
    private readonly UdpClient udp = new(new IPEndPoint(IPAddress.Loopback, 0));
    private readonly ConcurrentQueue<string> arrivals = new();
    private readonly CancellationTokenSource stop = new();

    private int TcpPort => ((IPEndPoint)tcp.LocalEndpoint).Port;
    private int UdpPort => ((IPEndPoint)udp.Client.LocalEndPoint!).Port;

    public Task InitializeAsync()
    {
        tcp.Start();
        _ = AcceptAsync();
        _ = ReceiveAsync();
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        stop.Cancel();
        tcp.Stop();
        udp.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>Every vector S3 lists (WebSockets, WebRTC, prefetch) plus the routed ones, against the listeners.</summary>
    private string EscapePage() => $$"""
        <!doctype html><html><head>
        <link rel="preconnect" href="http://127.0.0.1:{{TcpPort}}/">
        <link rel="dns-prefetch" href="http://localhost:{{TcpPort}}/">
        <link rel="prefetch" href="http://127.0.0.1:{{TcpPort}}/prefetch">
        <link rel="stylesheet" href="http://127.0.0.1:{{TcpPort}}/style.css">
        </head><body>
        <img src="http://127.0.0.1:{{TcpPort}}/img">
        <iframe src="http://127.0.0.1:{{TcpPort}}/frame"></iframe>
        <script>
          const t = (f) => { try { f(); } catch (e) { } };
          t(() => new WebSocket('ws://127.0.0.1:{{TcpPort}}/ws'));
          t(() => new WebSocket('ws://localhost:{{TcpPort}}/ws-localhost'));
          t(() => new EventSource('http://127.0.0.1:{{TcpPort}}/eventsource'));
          t(() => fetch('http://127.0.0.1:{{TcpPort}}/fetch', { mode: 'no-cors' }));
          t(() => fetch('http://localhost:{{TcpPort}}/fetch-localhost', { mode: 'no-cors', keepalive: true }));
          t(() => navigator.sendBeacon('http://127.0.0.1:{{TcpPort}}/beacon', 'x'));
          t(() => new WebTransport('https://127.0.0.1:{{UdpPort}}/webtransport'));
          const rtc = (P) => { const pc = new P({ iceServers: [{ urls: 'stun:127.0.0.1:{{UdpPort}}' }, { urls: 'turn:127.0.0.1:{{UdpPort}}?transport=tcp', username: 'u', credential: 'p' }] }); pc.createDataChannel('x'); pc.createOffer().then(o => pc.setLocalDescription(o)); };
          t(() => rtc(window.RTCPeerConnection));
          t(() => { const f = document.createElement('iframe'); document.body.appendChild(f); rtc(f.contentWindow.RTCPeerConnection); });
          t(() => navigator.serviceWorker.register('http://127.0.0.1:{{TcpPort}}/sw.js'));
          t(() => { const a = document.createElement('a'); a.href = 'http://127.0.0.1:{{TcpPort}}/ping'; a.ping = 'http://127.0.0.1:{{TcpPort}}/ping'; document.body.appendChild(a); a.click(); });
        </script></body></html>
        """;

    [Fact]
    public async Task Control_an_unhardened_browser_reaches_the_listeners()
    {
        // Layer 1 alone, perfect: every routed request is aborted. Still the listeners are reached (the test can see an escape).
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var context = await browser.NewContextAsync();
        await context.RouteAsync("**/*", route => route.AbortAsync("blockedbyclient"));
        var page = await context.NewPageAsync();
        await page.SetContentAsync(EscapePage());
        await WaitForArrivalsAsync(TimeSpan.FromSeconds(4));

        output.WriteLine("Reached without the hardening: " + string.Join(" | ", arrivals));
        arrivals.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Nothing_escapes_a_launched_browser()
    {
        var timings = await RenderEscapePageAsync(new BrowserOptions { Mode = BrowserMode.Launch });
        output.WriteLine(timings);
        arrivals.Should().BeEmpty();
    }

    [Fact]
    public async Task Nothing_escapes_a_browser_reached_over_ConnectAsync()
    {
        // The production path (D3): a Playwright run-server, here the driver bundled with Microsoft.Playwright, on loopback.
        var port = FreePort();
        using var server = StartRunServer(port);
        try
        {
            await WaitForPortAsync(port, TimeSpan.FromSeconds(30));
            var timings = await RenderEscapePageAsync(new BrowserOptions { Mode = BrowserMode.Connect, Endpoint = $"ws://127.0.0.1:{port}/" });
            output.WriteLine(timings);
            arrivals.Should().BeEmpty();
        }
        finally
        {
            try { server.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        }
    }

    /// <summary>
    /// The production server (<c>apps/SeoTester/chromium/server.mjs</c>, the Chromium container's command), here run with
    /// Microsoft.Playwright's own node and playwright-core: it fixes the flags itself, so nothing escapes even though a
    /// connecting client cannot set them.
    /// </summary>
    [Fact]
    public async Task Nothing_escapes_the_Chromium_containers_server_script()
    {
        var port = FreePort();
        using var server = StartServerScript(port);
        try
        {
            await WaitForPortAsync(port, TimeSpan.FromSeconds(30));
            var timings = await RenderEscapePageAsync(new BrowserOptions { Mode = BrowserMode.Connect, Endpoint = $"ws://127.0.0.1:{port}/" });
            output.WriteLine(timings);
            arrivals.Should().BeEmpty();
        }
        finally
        {
            try { server.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        }
    }

    /// <summary>
    /// An <c>&lt;a download&gt;</c> of the page's own origin is fetched by Chromium's download manager, not routed (measured
    /// 2026-10-15: no route ever saw it; a cross-origin one is a plain navigation, which is routed). Only the hardening
    /// stands between it and the network: the page here has the listener's own origin (fulfilled by a route, never
    /// fetched). Without the hardening the download reaches the listener (the control); with it, nothing arrives.
    /// </summary>
    [Fact]
    public async Task A_download_the_router_never_sees_reaches_nothing_through_the_hardening()
    {
        using (var playwright = await Playwright.CreateAsync())
        {
            await using var bare = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
            await ClickOwnOriginDownloadAsync(bare);
        }
        output.WriteLine("Reached without the hardening: " + string.Join(" | ", arrivals));
        arrivals.Should().Contain(a => a.StartsWith("tcp: GET /download", StringComparison.Ordinal), "the download manager fetched it from the listener itself (the control)");

        arrivals.Clear();
        await using var chromium = new ChromiumBrowser(Options.Create(new BrowserOptions { Mode = BrowserMode.Launch }), NullLogger<ChromiumBrowser>.Instance);
        await ClickOwnOriginDownloadAsync(await chromium.GetAsync(CancellationToken.None));
        arrivals.Should().BeEmpty("the dead proxy refuses what the router never sees");
    }

    /// <summary>A page of the listener's origin, every request of it routed (and refused), clicks an <c>&lt;a download&gt;</c> of that origin.</summary>
    private async Task ClickOwnOriginDownloadAsync(IBrowser browser)
    {
        var origin = $"http://127.0.0.1:{TcpPort}";
        var context = await browser.NewContextAsync(new BrowserNewContextOptions { AcceptDownloads = false });
        try
        {
            await context.RouteAsync("**/*", route => route.Request.Url == origin + "/"
                ? route.FulfillAsync(new() { Status = 200, ContentType = "text/html", Body = "<a download href=\"/download\">x</a><script>document.querySelector('a').click();</script>" })
                : route.AbortAsync("blockedbyclient"));
            var page = await context.NewPageAsync();
            await page.GotoAsync(origin + "/");
            await WaitForArrivalsAsync(TimeSpan.FromSeconds(4));
        }
        finally
        {
            await context.CloseAsync();
        }
    }

    [Fact]
    public void The_server_script_launches_with_the_same_flags_as_the_app()
    {
        var script = File.ReadAllText(ServerScript);
        foreach (var arg in ChromiumBrowser.HardeningArgs)
            script.Should().Contain($"'{arg}'");
        script.Should().Contain($"proxy: {{ server: '{ChromiumBrowser.DeadProxy.Server}', bypass: '{ChromiumBrowser.DeadProxy.Bypass}' }}");
    }

    private static string ServerScript => Path.Combine(SeoTester.Tests._Infrastructure.SeoTesterPaths.RepositoryRoot, "apps", "SeoTester", "chromium", "server.mjs");

    private static Process StartServerScript(int port)
    {
        var driver = Path.Combine(AppContext.BaseDirectory, ".playwright");
        var node = Directory.GetFiles(Path.Combine(driver, "node"), OperatingSystem.IsWindows() ? "node.exe" : "node", SearchOption.AllDirectories).First();
        var info = new ProcessStartInfo(node) { ArgumentList = { ServerScript }, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        info.Environment["PLAYWRIGHT_CORE"] = new Uri(Path.Combine(driver, "package", "index.mjs")).AbsoluteUri;
        info.Environment["HOST"] = "127.0.0.1";
        info.Environment["PORT"] = port.ToString();
        var process = Process.Start(info)!;
        process.OutputDataReceived += (_, _) => { };
        process.ErrorDataReceived += (_, _) => { };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    private async Task<string> RenderEscapePageAsync(BrowserOptions options)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        // The real SSRF-safe client (no fake web): a routed request to loopback is refused before it connects (layer 1).
        services.AddSafeHttpClient(SeoHttp.Browser, configure: o => o.FollowRedirects = false);
        await using var provider = services.BuildServiceProvider();
        await using var chromium = new ChromiumBrowser(Options.Create(options), NullLogger<ChromiumBrowser>.Instance);

        var watch = Stopwatch.StartNew();
        var browser = await chromium.GetAsync(CancellationToken.None);
        var start = watch.ElapsedMilliseconds;
        using var pacer = new HostPacer(1, TimeSpan.Zero, null, TimeProvider.System);
        using var network = new RenderNetwork(new SeoTester.Fetching.LocalSeoFetcher(new PageFetcher(provider.GetRequiredService<IHttpClientFactory>(), NullLogger<PageFetcher>.Instance), null!, null!), new Uri("https://escape.test/"), RobotsTxt.AllowAll, pacer, NullLogger.Instance);
        var context = await browser.NewContextAsync(new BrowserNewContextOptions { ServiceWorkers = ServiceWorkerPolicy.Block });
        var router = new BrowserRouter(network, CancellationToken.None);
        await router.AttachAsync(context);
        var page = await context.NewPageAsync();
        await page.SetContentAsync(EscapePage());
        await WaitForArrivalsAsync(TimeSpan.FromSeconds(4));
        await context.CloseAsync();

        var log = router.Log;
        log.Should().Contain(e => e.Url.Contains("/img") && e.Outcome == "blocked", "a routed request to loopback is refused by the SSRF-safe client");
        log.Should().NotContain(e => e.Outcome == "fulfilled" && e.Url.Contains("127.0.0.1"));
        return $"{options.Mode}: browser ready in {start} ms; routed: {string.Join(", ", log.Select(e => $"{new Uri(e.Url).AbsolutePath}={e.Outcome}"))}";
    }

    private async Task WaitForArrivalsAsync(TimeSpan wait)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < wait)
            await Task.Delay(100);
    }

    private async Task AcceptAsync()
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                using var client = await tcp.AcceptTcpClientAsync(stop.Token);
                var buffer = new byte[256];
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                var read = 0;
                try
                {
                    read = await client.GetStream().ReadAsync(buffer, timeout.Token);
                }
                catch (OperationCanceledException)
                {
                }
                arrivals.Enqueue("tcp: " + (read > 0 ? Encoding.ASCII.GetString(buffer, 0, read).Split('\n')[0].Trim() : "(connection, no bytes: a preconnect)"));
            }
            catch (Exception) when (stop.IsCancellationRequested)
            {
                return;
            }
            catch (SocketException)
            {
            }
        }
    }

    private async Task ReceiveAsync()
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                var datagram = await udp.ReceiveAsync(stop.Token);
                arrivals.Enqueue($"udp: {datagram.Buffer.Length} bytes (first {datagram.Buffer[0]:x2})");
            }
            catch (Exception) when (stop.IsCancellationRequested)
            {
                return;
            }
            catch (SocketException)
            {
            }
        }
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>Starts <c>playwright run-server</c> from the driver Microsoft.Playwright copies next to the assemblies.</summary>
    internal static Process StartRunServer(int port)
    {
        var driver = Path.Combine(AppContext.BaseDirectory, ".playwright");
        var node = Directory.GetFiles(Path.Combine(driver, "node"), OperatingSystem.IsWindows() ? "node.exe" : "node", SearchOption.AllDirectories).First();
        var info = new ProcessStartInfo(node)
        {
            ArgumentList = { Path.Combine(driver, "package", "cli.js"), "run-server", "--port", port.ToString(), "--host", "127.0.0.1" },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        var process = Process.Start(info)!;
        process.OutputDataReceived += (_, _) => { };
        process.ErrorDataReceived += (_, _) => { };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    internal static async Task WaitForPortAsync(int port, TimeSpan timeout)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < timeout)
        {
            try
            {
                using var probe = new TcpClient();
                await probe.ConnectAsync(IPAddress.Loopback, port);
                return;
            }
            catch (SocketException)
            {
                await Task.Delay(200);
            }
        }
        throw new TimeoutException($"The run-server did not listen on {port} within {timeout}.");
    }
}

/// <summary>The browser tests: one at a time (each starts Chromium), apart from the host tests.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class BrowserCollection
{
    public const string Name = "SeoTester browser";
}
