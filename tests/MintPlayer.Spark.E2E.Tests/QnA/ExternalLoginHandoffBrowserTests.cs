using System.Text.RegularExpressions;
using Microsoft.Playwright;
using MintPlayer.Spark.E2E.Tests._Infrastructure;

namespace MintPlayer.Spark.E2E.Tests.QnA;

/// <summary>
/// #464/#490 M7: the external-login hand-off when the sign-in page holds no usable popup handle — a
/// popup the provider's COOP severed, or one an installed web app captured. The app's
/// <c>window.open</c> is stubbed to hand back a handle that already reads <c>closed</c>, and the test
/// opens the callback itself with <c>noopener</c>, so the callback page has no <c>window.opener</c>.
/// The result can then only travel the opener-free routes (BroadcastChannel, <c>localStorage</c>),
/// keyed by the nonce.
/// </summary>
/// <remarks>
/// <para>
/// QnA, because its sign-in page declares a provider (<c>oidcProvider('HR', …)</c>) and
/// <see cref="QnATestHost"/> configures that scheme. The authority is never contacted: without an
/// external cookie the real callback answers the refusal <c>no_login_info</c>, which is the result
/// these tests expect to see arrive.
/// </para>
/// <para>
/// ⚠️ The callback page is opened by script (<c>window.open(url, '_blank', 'noopener')</c>), not with
/// <c>context.NewPageAsync()</c> + <c>GotoAsync</c>. A tab the browser opened has a history of two
/// entries (<c>about:blank</c> and the callback), and Chromium ignores <c>window.close()</c> there —
/// measured: the ack arrived and <c>close()</c> was called with <c>history.length == 2</c>, and the page
/// stayed open. A real popup is always script-opened, COOP or not, so this is the faithful setup.
/// </para>
/// </remarks>
[Collection(QnAE2ECollection.Name)]
public class ExternalLoginHandoffBrowserTests
{
    private const string NoLoginInfoText = "The provider did not return a login.";

    /// <summary>
    /// Records every <c>window.open</c> URL and returns a handle that is already closed, keeping the real
    /// one as <c>__realOpen</c>. Also listens on the hand-off channel, so a test can tell that a
    /// broadcast has reached this page.
    /// </summary>
    private const string StubWindowOpen = """
        (() => {
            window.__realOpen = window.open.bind(window);
            window.__sparkOpened = [];
            window.open = (url) => {
                window.__sparkOpened.push(String(url));
                return { closed: true, close() { } };
            };
            window.__sparkBroadcasts = [];
            try {
                const probe = new BroadcastChannel('spark:external-login');
                probe.onmessage = (e) => window.__sparkBroadcasts.push(e.data);
            } catch { }
        })();
        """;

    private static readonly Regex NoncePattern = new("^[A-Za-z0-9_-]{16,64}$");

    /// <summary>Only resolves the relative URL the app passed to <c>window.open</c>.</summary>
    private static readonly Uri RelativeBase = new("https://localhost/");

    private readonly QnAE2ECollectionFixture fixture;

    public ExternalLoginHandoffBrowserTests(QnAE2ECollectionFixture fixture) => this.fixture = fixture;

    [Fact]
    public async Task A_popup_without_an_opener_still_signs_the_result_back()
    {
        var context = await NewContextAsync();
        try
        {
            var (page, nonce) = await StartAttemptAsync(context);

            // D2 final: a closed popup re-enables the buttons and is not an outcome, so nothing is shown.
            await page.Locator(".spark-provider-button:not([disabled])").First.WaitForAsync(new() { Timeout = 15_000 });
            (await page.Locator(".spark-external-error").CountAsync()).Should().Be(0,
                "a popup that reads closed is not a result: the attempt must stay open for a late one");

            var callback = await OpenWithoutOpenerAsync(page, CallbackUrl(nonce));
            var callbackClosed = new TaskCompletionSource();
            callback.Close += (_, _) => callbackClosed.TrySetResult();
            if (callback.IsClosed) callbackClosed.TrySetResult();

            var error = page.Locator(".spark-external-error");
            await error.WaitForAsync(new() { Timeout = 15_000 });
            (await error.InnerTextAsync()).Should().Contain(NoLoginInfoText,
                "the server's code arrives without an opener, not popup_closed (which shows nothing)");

            (await page.EvaluateAsync<string?>("n => localStorage.getItem('spark:external-login-done:' + n)", nonce))
                .Should().Be("1");
            (await page.EvaluateAsync<string?>("n => localStorage.getItem('spark:external-login:' + n)", nonce))
                .Should().BeNull("the accepted payload is consumed");

            // The ack: only the sign-in page's answer on the channel makes the callback page close itself
            // this early (its own fallback merely shows a Close button after ~3 s).
            var first = await Task.WhenAny(callbackClosed.Task, Task.Delay(TimeSpan.FromSeconds(15)));
            (first == callbackClosed.Task).Should().BeTrue(
                "the callback page closes once the sign-in page acknowledges its result");
        }
        finally
        {
            await context.CloseAsync();
        }
    }

    [Fact]
    public async Task A_result_for_another_nonce_is_ignored()
    {
        var context = await NewContextAsync();
        try
        {
            var (page, nonce) = await StartAttemptAsync(context);
            await page.Locator(".spark-provider-button:not([disabled])").First.WaitForAsync(new() { Timeout = 15_000 });

            var otherNonce = Guid.NewGuid().ToString("N");
            otherNonce.Should().NotBe(nonce);
            var stranger = await OpenWithoutOpenerAsync(page, CallbackUrl(otherNonce));

            // Wait until the stranger's broadcast has reached the sign-in page (the probe's channel and
            // the attempt's channel get it from the same post), then let one more task run.
            await page.WaitForFunctionAsync(
                "n => window.__sparkBroadcasts.some(m => m && m.nonce === n)", otherNonce,
                new() { Timeout = 15_000 });
            await page.EvaluateAsync("() => new Promise(r => setTimeout(r, 0))");

            (await page.Locator(".spark-external-error").CountAsync()).Should().Be(0,
                "a payload carrying another attempt's nonce is not this attempt's result");
            (await page.EvaluateAsync<string?>("n => localStorage.getItem('spark:external-login-done:' + n)", otherNonce))
                .Should().BeNull("nobody acknowledged the stranger's result");
            (await page.EvaluateAsync<string?>("n => localStorage.getItem('spark:external-login:' + n)", otherNonce))
                .Should().NotBeNull("the sign-in page did not consume another attempt's entry");
            stranger.IsClosed.Should().BeFalse("an unacknowledged callback page stays open");

            // Control: the attempt is still live, and its own nonce settles it.
            await OpenWithoutOpenerAsync(page, CallbackUrl(nonce));
            var error = page.Locator(".spark-external-error");
            await error.WaitForAsync(new() { Timeout = 15_000 });
            (await error.InnerTextAsync()).Should().Contain(NoLoginInfoText);
        }
        finally
        {
            await context.CloseAsync();
        }
    }

    private Task<IBrowserContext> NewContextAsync()
        => fixture.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            IgnoreHTTPSErrors = true,
            BaseURL = fixture.Host.AppUrl,
        });

    /// <summary>Opens the sign-in page, clicks the HR button and returns the attempt's nonce.</summary>
    private static async Task<(IPage Page, string Nonce)> StartAttemptAsync(IBrowserContext context)
    {
        var page = await context.NewPageAsync();
        await page.AddInitScriptAsync(StubWindowOpen);
        await page.GotoAsync("/sign-in");

        var button = page.Locator(".spark-provider-button").Filter(new() { HasTextString = "Spark HR" });
        await button.WaitForAsync(new() { Timeout = 15_000 });
        await button.ClickAsync();

        await page.WaitForFunctionAsync("() => window.__sparkOpened.length > 0", null, new() { Timeout = 15_000 });
        var opened = await page.EvaluateAsync<string[]>("() => window.__sparkOpened");
        opened.Should().HaveCount(1);

        var url = new Uri(RelativeBase, opened[0]);
        url.AbsolutePath.Should().Be("/spark/auth/external-login");
        var query = System.Web.HttpUtility.ParseQueryString(url.Query);
        query["provider"].Should().Be("HR");
        query["popup"].Should().Be("1");
        query["ngsw-bypass"].Should().Be("true");
        var nonce = query["nonce"];
        nonce.Should().NotBeNull();
        NoncePattern.IsMatch(nonce!).Should().BeTrue($"the nonce '{nonce}' must have the server's accepted shape");
        return (page, nonce!);
    }

    /// <summary>
    /// Opens <paramref name="url"/> from <paramref name="page"/> with <c>noopener</c>: script-opened like a
    /// real popup, but with no <c>window.opener</c> (which is what <c>window.open</c> returning null means).
    /// </summary>
    private static async Task<IPage> OpenWithoutOpenerAsync(IPage page, string url)
    {
        bool? handleReturned = null;
        var opened = await page.Context.RunAndWaitForPageAsync(async () =>
        {
            handleReturned = await page.EvaluateAsync<bool>(
                "u => window.__realOpen(u, '_blank', 'noopener') !== null", url);
        });
        (handleReturned == false).Should().BeTrue("noopener leaves the callback page without an opener");
        return opened;
    }

    private static string CallbackUrl(string nonce)
        => $"/spark/auth/external-login-callback?popup=1&nonce={nonce}&returnUrl=%2F";
}
