using System.Text.Json;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;

namespace SeoTester.Rendering;

/// <summary>Where the headless browser comes from (configuration <c>SeoTester:Browser</c>).</summary>
public enum BrowserMode
{
    /// <summary>No browser: the rendered checks are "not measured" and PDF export is unavailable.</summary>
    Off,

    /// <summary>A Chromium launched on this machine (Development, the browser tests): <c>playwright.ps1 install chromium</c>.</summary>
    Launch,

    /// <summary>The Chromium container, driven remotely over Playwright <c>ConnectAsync</c> (production, D3, D6).</summary>
    Connect,
}

/// <summary>The headless browser's configuration (<c>SeoTester:Browser</c>).</summary>
public sealed class BrowserOptions
{
    public BrowserMode Mode { get; set; } = BrowserMode.Off;

    /// <summary><see cref="BrowserMode.Connect"/>: the run-server's WebSocket, e.g. <c>ws://seo-chromium:3000/</c>.</summary>
    public string? Endpoint { get; set; }

    /// <summary>One page load (navigation to the <c>load</c> event).</summary>
    public TimeSpan NavigationTimeout { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>After <c>load</c>, how long to wait at most for the network to go quiet (lazy content, client rendering).</summary>
    public TimeSpan SettleTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Everything one sampled page may take (both viewports, axe, the checks): beyond it the page is an error. Ten
    /// verified pages × 50 s stay under <c>seo-render</c>'s 10-minute handler timeout.
    /// </summary>
    public TimeSpan PageBudget { get; set; } = TimeSpan.FromSeconds(50);

    /// <summary>
    /// Closing a browser context, or the browser: Playwright's close has no timeout of its own, so past this the browser
    /// is torn down instead (<see cref="ChromiumBrowser.CloseAsync(IBrowserContext)"/>). A failure bound: a close takes
    /// well under a second.
    /// </summary>
    public TimeSpan CloseTimeout { get; set; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// The one browser of the process (<c>docs/seo_analyzer_PRD.md</c> §6, D3, D6): launched locally or reached over
/// <c>ConnectAsync</c>, always with <see cref="HardeningArgs"/>, so that whatever Playwright's routing does not see
/// (a preconnect, a WebSocket, UDP) has nowhere to go. Every context it hands out is routed by
/// <see cref="BrowserRouter"/>: Chromium itself never resolves a name or opens a connection to a site. Reconnects when
/// the browser went away (a container restart), and tears it down when a close of it hangs
/// (<see cref="BrowserOptions.CloseTimeout"/>).
/// </summary>
public sealed class ChromiumBrowser(IOptions<BrowserOptions> options, ILogger<ChromiumBrowser> logger) : IAsyncDisposable
{
    /// <summary>
    /// Chromium's second layer (spike S3), part one: every connection the router does not fulfil goes to a proxy that
    /// does not exist (the discard port on the browser's own loopback: refused), loopback included. Spike S3 measured
    /// what escapes Playwright's routing without it: <c>&lt;link rel=prefetch&gt;</c>, <c>navigator.sendBeacon</c>, a
    /// <c>keepalive</c> fetch and <c>&lt;a ping&gt;</c> (plus WebSockets and WebRTC's UDP, closed by the router and the init
    /// script). A Playwright launch option, so a plain <c>run-server</c> applies it too (it drops <see cref="HardeningArgs"/>
    /// unless started with <c>--unsafe</c>).
    /// </summary>
    public static readonly Proxy DeadProxy = new() { Server = "http://127.0.0.1:9", Bypass = "<-loopback>" };

    /// <summary>
    /// Part two, command-line flags: no name resolution at all, no UDP for WebRTC, no background traffic of its own.
    /// Applied when launched here, and by a run-server that allows client arguments (or a server that launches with them).
    /// </summary>
    public static readonly IReadOnlyList<string> HardeningArgs =
    [
        "--host-resolver-rules=MAP * ~NOTFOUND",
        "--webrtc-ip-handling-policy=disable_non_proxied_udp",
        "--force-webrtc-ip-handling-policy",
        "--disable-background-networking",
        "--disable-component-update",
        "--disable-domain-reliability",
        "--disable-sync",
        "--no-pings",
        "--dns-prefetch-disable",
    ];

    private readonly SemaphoreSlim gate = new(1, 1);
    private IPlaywright? playwright;
    private IBrowser? browser;

    public BrowserOptions Options => options.Value;

    public bool Enabled => options.Value.Mode != BrowserMode.Off;

    /// <summary>The browser, launched or connected on first use. Throws <see cref="PlaywrightException"/> when it cannot be reached.</summary>
    public async Task<IBrowser> GetAsync(CancellationToken cancellationToken)
    {
        if (browser is { IsConnected: true } ready)
            return ready;
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (browser is { IsConnected: true } existing)
                return existing;
            if (browser is { } gone)
            {
                logger.LogWarning("The headless browser went away; reconnecting");
                await CloseOrTearDownAsync(gone.CloseAsync(), gone, "the browser that went away");
                browser = null;
            }
            // A local: a teardown (TearDown) may clear the field meanwhile.
            var driver = playwright ??= await Playwright.CreateAsync();
            var o = options.Value;
            browser = o.Mode switch
            {
                BrowserMode.Launch => await driver.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true, Args = HardeningArgs, Proxy = DeadProxy }),
                BrowserMode.Connect when !string.IsNullOrWhiteSpace(o.Endpoint) => await driver.Chromium.ConnectAsync(o.Endpoint, new BrowserTypeConnectOptions
                {
                    Timeout = 30_000,
                    // The run-server launches Chromium with these: the flags travel with every connection, so the container
                    // cannot be started without them by mistake.
                    Headers = new Dictionary<string, string> { ["x-playwright-launch-options"] = LaunchOptionsHeader() },
                }),
                BrowserMode.Connect => throw new PlaywrightException("SeoTester:Browser:Endpoint is not configured."),
                _ => throw new PlaywrightException("The headless browser is off (SeoTester:Browser:Mode)."),
            };
            logger.LogInformation("Headless browser ready ({Mode}, Chromium {Version})", o.Mode, browser.Version);
            return browser;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>The launch options a run-server applies (<c>x-playwright-launch-options</c>).</summary>
    public static string LaunchOptionsHeader() => JsonSerializer.Serialize(new
    {
        headless = true,
        args = HardeningArgs,
        proxy = new { server = DeadProxy.Server, bypass = DeadProxy.Bypass },
    });

    /// <summary>
    /// Closes a context of this browser (a render's, the PDF export's), waiting at most
    /// <see cref="BrowserOptions.CloseTimeout"/>: past it the browser is torn down (<see cref="TearDown"/>).
    /// </summary>
    public Task CloseAsync(IBrowserContext context) => CloseOrTearDownAsync(context.CloseAsync(), context.Browser, "a browser context");

    public async ValueTask DisposeAsync()
    {
        if (browser is { } open)
            await CloseOrTearDownAsync(open.CloseAsync(), open, "the browser");
        playwright?.Dispose();
        gate.Dispose();
    }

    /// <summary>
    /// Waits for <paramref name="close"/> at most <see cref="BrowserOptions.CloseTimeout"/>, then tears
    /// <paramref name="owner"/> down. Playwright's close has no timeout of its own (it waits for the driver's answer), and
    /// a render that waits on a browser that hangs would hold the render gate for good.
    /// </summary>
    private async Task CloseOrTearDownAsync(Task close, IBrowser? owner, string what)
    {
        var closing = IgnoreGoneAsync(close);
        using var timer = new CancellationTokenSource();
        if (await Task.WhenAny(closing, Task.Delay(options.Value.CloseTimeout, timer.Token)) == closing)
        {
            timer.Cancel();
            await closing;
            return;
        }
        logger.LogWarning("Closing {What} took longer than {Timeout}; tearing the headless browser down", what, options.Value.CloseTimeout);
        TearDown(owner);
    }

    /// <summary>
    /// Disposes this process's Playwright driver, if <paramref name="owner"/> is still the browser (once: a browser that
    /// replaced it is left alone). That fails every call still waiting on the driver, the hung close among them. As the
    /// driver exits it kills the browser it launched, with its process tree (after at most 30 s of its own graceful
    /// close); over <c>ConnectAsync</c> the connection drops. The next <see cref="GetAsync"/> starts a new driver.
    /// </summary>
    private void TearDown(IBrowser? owner)
    {
        if (owner is null || Interlocked.CompareExchange(ref browser, null, owner) != owner)
            return;
        if (Interlocked.Exchange(ref playwright, null) is not { } driver)
            return;
        // Dispose waits for the driver to exit: not on the caller's thread.
        _ = Task.Run(() =>
        {
            try
            {
                driver.Dispose();
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Disposing the torn-down Playwright driver failed");
            }
        });
    }

    private static async Task IgnoreGoneAsync(Task close)
    {
        try
        {
            await close;
        }
        catch (PlaywrightException)
        {
            // Already gone.
        }
    }
}

/// <summary>
/// At most <c>SeoTester:Limits:ConcurrentRenders</c> renders at a time in this process (§6: one, global). Read per
/// render, so an administrator's change applies to the next one.
/// </summary>
public sealed class RenderGate
{
    private readonly object sync = new();
    private readonly LinkedList<TaskCompletionSource> waiting = new();
    private int active;

    public async Task<IDisposable> EnterAsync(Func<int> limit, CancellationToken cancellationToken)
    {
        while (true)
        {
            TaskCompletionSource turn;
            lock (sync)
            {
                if (active < Math.Max(1, limit()))
                {
                    active++;
                    return new Exit(this);
                }
                turn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                waiting.AddLast(turn);
            }
            using (cancellationToken.Register(() => turn.TrySetCanceled(cancellationToken)))
                await turn.Task;
        }
    }

    private void Leave()
    {
        lock (sync)
        {
            active--;
            // A waiter that gave up (cancelled) is skipped, or the next one would never be woken.
            while (waiting.First is { } next)
            {
                waiting.RemoveFirst();
                if (next.Value.TrySetResult())
                    break;
            }
        }
    }

    private sealed class Exit(RenderGate gate) : IDisposable
    {
        private int done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref done, 1) == 0)
                gate.Leave();
        }
    }
}
