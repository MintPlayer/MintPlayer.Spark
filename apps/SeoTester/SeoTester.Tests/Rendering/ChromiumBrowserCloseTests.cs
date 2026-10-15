using System.Diagnostics;
using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;
using SeoTester.Rendering;

namespace SeoTester.Tests.Rendering;

/// <summary>
/// Playwright's close has no timeout of its own, so <see cref="ChromiumBrowser"/> bounds every close it makes
/// (<c>docs/seo_analyzer_PRD.md</c> §12, "CI stall"): past <see cref="BrowserOptions.CloseTimeout"/> it stops waiting and
/// tears the browser down, and the next <see cref="ChromiumBrowser.GetAsync"/> starts afresh. Over one launched Chromium,
/// with a context of it whose close never ends. Needs Playwright's Chromium (<c>playwright.ps1 install chromium</c>).
/// </summary>
[Collection(BrowserCollection.Name)]
public sealed class ChromiumBrowserCloseTests
{
    /// <summary>
    /// A context that closes in time leaves the browser alone (the control). One whose close never ends is given up after
    /// <see cref="BrowserOptions.CloseTimeout"/>: the browser disconnects, its process exits, and the next caller gets a
    /// new browser. Red before the bound: the close never returns.
    /// </summary>
    [Fact]
    public async Task A_close_that_never_ends_is_given_up_and_the_browser_torn_down()
    {
        await using var chromium = new ChromiumBrowser(Options.Create(new BrowserOptions { Mode = BrowserMode.Launch, CloseTimeout = TimeSpan.FromSeconds(1) }),
            NullLogger<ChromiumBrowser>.Instance);
        var browser = await chromium.GetAsync(CancellationToken.None);
        await chromium.CloseAsync(await browser.NewContextAsync());
        (await chromium.GetAsync(CancellationToken.None)).Should().BeSameAs(browser, "a close that ends in time leaves the browser alone");

        using var process = Process.GetProcessById(await BrowserProcessIdAsync(browser));
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        browser.Disconnected += (_, _) => disconnected.TrySetResult();

        var close = chromium.CloseAsync(HangingContext.Of(browser));
        // Failure bounds only: the close is given up after one second, and the driver goes at once.
        (await Task.WhenAny(close, Task.Delay(TimeSpan.FromSeconds(60))) == close).Should().BeTrue("a close that never ends is given up");
        (await Task.WhenAny(disconnected.Task, Task.Delay(TimeSpan.FromSeconds(60))) == disconnected.Task).Should().BeTrue("the browser is torn down");
        using (var exit = new CancellationTokenSource(TimeSpan.FromSeconds(60)))
            await process.WaitForExitAsync(exit.Token);
        var next = await chromium.GetAsync(CancellationToken.None);
        next.Should().NotBeSameAs(browser, "the next caller gets a new browser");
        next.IsConnected.Should().BeTrue();
    }

    /// <summary>The browser process's id, from Chromium itself.</summary>
    private static async Task<int> BrowserProcessIdAsync(IBrowser browser)
    {
        var cdp = await browser.NewBrowserCDPSessionAsync();
        var info = await cdp.SendAsync("SystemInfo.getProcessInfo");
        await cdp.DetachAsync();
        return info!.Value.GetProperty("processInfo").EnumerateArray()
            .Single(p => p.GetProperty("type").GetString() == "browser")
            .GetProperty("id").GetInt32();
    }

    /// <summary>A context of a browser whose close never ends. Nothing else of it is used.</summary>
    public class HangingContext : DispatchProxy
    {
        private IBrowser? browser;

        public static IBrowserContext Of(IBrowser browser)
        {
            var context = Create<IBrowserContext, HangingContext>();
            ((HangingContext)(object)context).browser = browser;
            return context;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => targetMethod?.Name switch
        {
            "get_" + nameof(IBrowserContext.Browser) => browser,
            nameof(IBrowserContext.CloseAsync) => new TaskCompletionSource().Task,
            _ => throw new NotSupportedException(targetMethod?.Name),
        };
    }
}
