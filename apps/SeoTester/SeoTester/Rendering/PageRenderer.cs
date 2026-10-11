using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using SeoTester.Html;

namespace SeoTester.Rendering;

/// <summary>One sampled page's render: the facts the checks read, plus what the run-level checks collect.</summary>
/// <param name="ServiceWorkerUrl">The script the page asked to register as a service worker, if any.</param>
/// <param name="RouteProbe">The SPA route-change probe (home page only, when analytics loaded): (route changed, hits after it).</param>
/// <param name="Timings">Milliseconds per step (spike S3: navigation, settle, axe, …).</param>
public sealed record PageRender(RenderedPageFacts Facts, string? ServiceWorkerUrl, (bool RouteChanged, int Hits)? RouteProbe, int HitsOnLoad,
    IReadOnlyDictionary<string, long> Timings);

/// <summary>
/// Renders one page twice (<c>docs/seo_analyzer_PRD.md</c> F12, M10): at 375 px as a phone (layout, lazy images, the
/// network log, Chromium's script coverage, axe-core) and at desktop width (the rendered DOM against the served HTML,
/// mobile parity, the SPA analytics probe). Each render is a fresh, routed browser context (no cookies or storage shared
/// with another page or run) with service workers blocked, closed afterwards. A page that hangs is closed at
/// <see cref="BrowserOptions.PageBudget"/>.
/// </summary>
public sealed partial class PageRenderer(ChromiumBrowser chromium, ILogger<PageRenderer> logger)
{
    /// <summary>The axe-core tags run: WCAG 2.0/2.1 A and AA, 2.2 AA (target size), and axe's best practices (landmarks, heading order).</summary>
    public static readonly string[] AxeTags = ["wcag2a", "wcag2aa", "wcag21a", "wcag21aa", "wcag22aa", "best-practice"];

    private static readonly Lazy<string> Axe = new(() => BrowserRouter.Resource("SeoTester.Rendering.axe.min.js"));
    private static readonly Lazy<string> Collect = new(() => BrowserRouter.Resource("SeoTester.Rendering.collect.js"));
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private const string AxeRun = """
        async (tags) => {
          const result = await axe.run(document, { runOnly: { type: 'tag', values: tags }, resultTypes: ['violations'] });
          return result.violations.slice(0, 200).map(v => ({ id: String(v.id), impact: v.impact || null, nodes: v.nodes.length, help: String(v.help || '').slice(0, 200) }));
        }
        """;

    private const string MarkProbeLink = """
        () => {
          for (const a of document.querySelectorAll('a[href]')) {
            try {
              const u = new URL(a.getAttribute('href'), location.href);
              if (u.origin === location.origin && (u.pathname + u.search) !== (location.pathname + location.search) && !a.target && a.offsetParent !== null) {
                a.setAttribute('data-seo-probe', '1');
                return u.href;
              }
            } catch (e) { }
          }
          return null;
        }
        """;

    public async Task<PageRender> RenderAsync(RenderNetwork network, Uri url, bool probeRoute, CancellationToken cancellationToken)
    {
        var options = chromium.Options;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(options.PageBudget);
        var timings = new Dictionary<string, long>(StringComparer.Ordinal);
        var browser = await chromium.GetAsync(cancellationToken);
        var major = browser.Version.Split('.')[0];

        RenderedView? mobile = null, desktop = null;
        IReadOnlyList<AxeViolation>? axe = null;
        ScriptCoverage? coverage = null;
        IReadOnlyList<NetworkEntry> log = [];
        RawView? raw = null;
        string? serviceWorker = null, error = null;
        (bool, int)? probe = null;
        var hitsOnLoad = 0;

        // ---- The phone. ------------------------------------------------------------------------------------------
        var phone = await NewContextAsync(browser, mobile: true, major);
        try
        {
            using var close = budget.Token.Register(() => _ = CloseAsync(phone));
            var router = new BrowserRouter(network, budget.Token);
            await router.AttachAsync(phone);
            var page = await phone.NewPageAsync();
            var cdp = await phone.NewCDPSessionAsync(page);
            await cdp.SendAsync("Profiler.enable");
            await cdp.SendAsync("Profiler.startPreciseCoverage", new Dictionary<string, object> { ["callCount"] = false, ["detailed"] = true });

            error = await NavigateAsync(page, url, options, timings, "mobile");
            if (error is null)
            {
                coverage = await Time(timings, "coverage", () => CoverageAsync(cdp));
                serviceWorker = await page.EvaluateAsync<string?>("() => window.__seoSw ? window.__seoSw.url : null");
                mobile = await Time(timings, "collect", () => CollectAsync(page));
                axe = await Time(timings, "axe", () => AxeAsync(page));
                hitsOnLoad = network.Hits;
            }
            log = router.Log;
            raw = RawOf(router.MainDocument);
        }
        catch (Exception ex) when (ex is PlaywrightException or TimeoutException or JsonException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            error ??= budget.IsCancellationRequested ? "the page took too long to render" : "the page could not be rendered";
            logger.LogDebug(ex, "Phone render of {Url} failed", url);
        }
        finally
        {
            await CloseAsync(phone);
        }

        // ---- The desktop. ----------------------------------------------------------------------------------------
        if (error is null)
        {
            var wide = await NewContextAsync(browser, mobile: false, major);
            try
            {
                using var close = budget.Token.Register(() => _ = CloseAsync(wide));
                var router = new BrowserRouter(network, budget.Token);
                await router.AttachAsync(wide);
                var page = await wide.NewPageAsync();
                if (await NavigateAsync(page, url, options, timings, "desktop") is null)
                {
                    desktop = await CollectAsync(page);
                    if (probeRoute && network.Tools.Count > 0)
                        probe = await Time(timings, "route-probe", () => ProbeRouteAsync(page, router, network));
                }
            }
            catch (Exception ex) when (ex is PlaywrightException or TimeoutException or JsonException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                logger.LogDebug(ex, "Desktop render of {Url} failed", url);
            }
            finally
            {
                await CloseAsync(wide);
            }
        }

        var facts = new RenderedPageFacts
        {
            Url = url.AbsoluteUri,
            Raw = raw,
            Mobile = mobile,
            Desktop = desktop,
            Network = log,
            Axe = axe,
            Coverage = coverage,
            Error = error,
        };
        return new PageRender(facts, serviceWorker, probe, hitsOnLoad, timings);
    }

    /// <summary>The service worker script, fetched like any request of the page: status, caching, a fetch handler (it can serve offline).</summary>
    public static async Task<ServiceWorkerFact> ServiceWorkerAsync(RenderNetwork network, string scriptUrl, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(scriptUrl, UriKind.Absolute, out var url))
            return new ServiceWorkerFact(scriptUrl, null, null, false);
        var answer = await network.FetchAsync(url, "GET", [new("accept", "*/*"), new("service-worker", "script")], null, false, cancellationToken);
        var text = answer.Body.Length > 0 ? Encoding.UTF8.GetString(answer.Body, 0, Math.Min(answer.Body.Length, 4 * 1024 * 1024)) : "";
        var handlesFetch = FetchHandler().IsMatch(text);
        return new ServiceWorkerFact(scriptUrl, answer.Status == 0 ? null : answer.Status, answer.Header("cache-control"), handlesFetch);
    }

    private async Task<IBrowserContext> NewContextAsync(IBrowser browser, bool mobile, string chromeMajor)
    {
        var identity = $" {SeoTesterBot.UserAgent}";
        return await browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = mobile ? new ViewportSize { Width = 375, Height = 667 } : new ViewportSize { Width = 1350, Height = 940 },
            DeviceScaleFactor = mobile ? 2 : 1,
            IsMobile = mobile,
            HasTouch = mobile,
            // The browser's own user agent with the crawler's identity appended (as Googlebot's renderer does): the site
            // sees a phone or a desktop and still knows who is asking (D29).
            UserAgent = (mobile
                ? $"Mozilla/5.0 (Linux; Android 10; K) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/{chromeMajor}.0.0.0 Mobile Safari/537.36"
                : $"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/{chromeMajor}.0.0.0 Safari/537.36") + identity,
            ServiceWorkers = ServiceWorkerPolicy.Block,
            // axe-core is evaluated into the page; a strict CSP must not stop it. The CSP itself is judged on the raw headers.
            BypassCSP = true,
            AcceptDownloads = false,
            Locale = "en-US",
        });
    }

    private static async Task<string?> NavigateAsync(IPage page, Uri url, BrowserOptions options, Dictionary<string, long> timings, string label)
    {
        var watch = Stopwatch.StartNew();
        IResponse? response;
        try
        {
            response = await page.GotoAsync(url.AbsoluteUri, new PageGotoOptions { WaitUntil = WaitUntilState.Load, Timeout = (float)options.NavigationTimeout.TotalMilliseconds });
        }
        catch (TimeoutException)
        {
            return "the page did not finish loading in time";
        }
        catch (PlaywrightException)
        {
            return "the page could not be loaded";
        }
        timings[label + "-load"] = watch.ElapsedMilliseconds;
        if (response is null || response.Status >= 400)
            return response is null ? "the page could not be loaded" : $"the page answered {response.Status}";
        watch.Restart();
        try
        {
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new PageWaitForLoadStateOptions { Timeout = (float)options.SettleTimeout.TotalMilliseconds });
        }
        catch (TimeoutException)
        {
            // A page that keeps polling is judged as it is after the settle time.
        }
        timings[label + "-settle"] = watch.ElapsedMilliseconds;
        return null;
    }

    private static async Task<RenderedView> CollectAsync(IPage page)
    {
        var element = await page.EvaluateAsync<JsonElement>(Collect.Value);
        return element.Deserialize<RenderedView>(Json) ?? new RenderedView();
    }

    private static async Task<IReadOnlyList<AxeViolation>> AxeAsync(IPage page)
    {
        await page.EvaluateAsync(Axe.Value);
        var element = await page.EvaluateAsync<JsonElement>(AxeRun, AxeTags);
        return element.Deserialize<List<AxeViolation>>(Json) ?? [];
    }

    /// <summary>JavaScript loaded by the time the page settled and the share of it that never ran (Chromium's precise coverage).</summary>
    private static async Task<ScriptCoverage?> CoverageAsync(ICDPSession cdp)
    {
        var result = await cdp.SendAsync("Profiler.takePreciseCoverage");
        if (result is not { } json || !json.TryGetProperty("result", out var scripts) || scripts.ValueKind != JsonValueKind.Array)
            return null;
        // Only the site's and third parties' scripts (http/https, inline ones under the page's URL), not Playwright's evaluations.
        long total = 0, unused = 0;
        foreach (var script in scripts.EnumerateArray())
        {
            var scriptUrl = script.TryGetProperty("url", out var u) ? u.GetString() : null;
            if (scriptUrl is null || !(scriptUrl.StartsWith("http://", StringComparison.Ordinal) || scriptUrl.StartsWith("https://", StringComparison.Ordinal)))
                continue;
            var ranges = new List<CoverageRange>();
            foreach (var function in script.GetProperty("functions").EnumerateArray())
                foreach (var range in function.GetProperty("ranges").EnumerateArray())
                    ranges.Add(new CoverageRange(range.GetProperty("startOffset").GetInt32(), range.GetProperty("endOffset").GetInt32(), range.GetProperty("count").GetInt32()));
            var (length, notRun) = CoverageMath.Measure(ranges);
            total += length;
            unused += notRun;
        }
        return new ScriptCoverage(total, unused);
    }

    /// <summary>Clicks a same-site link: did the URL change without a document request (an SPA route), and how many analytics hits followed.</summary>
    private static async Task<(bool, int)?> ProbeRouteAsync(IPage page, BrowserRouter router, RenderNetwork network)
    {
        if (await page.EvaluateAsync<string?>(MarkProbeLink) is null)
            return null;
        var before = page.Url;
        var documents = router.Documents;
        var hits = network.Hits;
        try
        {
            await page.ClickAsync("[data-seo-probe]", new PageClickOptions { Timeout = 3000 });
        }
        catch (TimeoutException)
        {
            return null;
        }
        await page.WaitForTimeoutAsync(2000);
        var changed = page.Url != before && router.Documents == documents;
        return (changed, network.Hits - hits);
    }

    private static RawView? RawOf(RoutedResponse? document)
    {
        if (document is null || document.Body.Length == 0)
            return null;
        var text = Encoding.UTF8.GetString(document.Body, 0, Math.Min(document.Body.Length, 3 * 1024 * 1024));
        var html = HtmlPage.Parse(text);
        return new RawView(html.Title, html.H1s.Count, Words().Matches(html.BodyText).Count, html.Document.QuerySelectorAll("a[href]").Length);
    }

    private static async Task<T> Time<T>(Dictionary<string, long> timings, string step, Func<Task<T>> work)
    {
        var watch = Stopwatch.StartNew();
        var result = await work();
        timings[step] = watch.ElapsedMilliseconds;
        return result;
    }

    private static async Task CloseAsync(IBrowserContext context)
    {
        try
        {
            await context.CloseAsync();
        }
        catch (PlaywrightException)
        {
        }
    }

    [GeneratedRegex(@"\S+")]
    private static partial Regex Words();

    [GeneratedRegex(@"addEventListener\(\s*['""]fetch['""]|\bonfetch\s*=|workbox|ngsw", RegexOptions.IgnoreCase, 1000)]
    private static partial Regex FetchHandler();
}
