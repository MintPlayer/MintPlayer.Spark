using Microsoft.Playwright;
using MintPlayer.Spark.E2E.Tests._Infrastructure;

namespace MintPlayer.Spark.E2E.Tests;

/// <summary>
/// Dark mode in a real browser (issue #462, PRD R7): the first paint, the shell's colours, and the
/// R2 matrix — Auto follows the OS live, an explicit Light/Dark is sticky across reloads and OS
/// changes, and a choice in one tab re-themes the others.
/// </summary>
/// <remarks>
/// <para>
/// The pieces under test are ng-bootstrap's: <c>bs-theme-preboot.js</c> (copied into Fleet's
/// output and loaded from <c>index.html</c> before the stylesheets), the <c>bs-theme-mode</c>
/// cookie, and the <c>&lt;bs-theme-toggle&gt;</c> button that <c>spark-shell</c> renders in its
/// topbar. The toggle is a single button that cycles auto → light → dark; it sits in the open
/// shadow root of <c>&lt;mp-theme-toggle&gt;</c>, which Playwright's CSS locators pierce.
/// </para>
/// <para>
/// ⚠️ Every page here is one Angular boot against Fleet's shared rate-limit bucket (150 requests
/// per 10 s for the whole collection), so the tests stay few and reload only where the reload is
/// the thing being tested.
/// </para>
/// </remarks>
[Collection(FleetE2ECollection.Name)]
public class DarkModeTests
{
    private const string ThemeAttribute = "data-bs-theme";
    private const string ThemeCookie = "bs-theme-mode";
    private const string Toggle = "spark-shell bs-theme-toggle button";

    /// <summary>
    /// Records the theme attribute the moment the DOM is parsed, before Angular has bootstrapped,
    /// so the assertion is about what the pre-boot script did and not what the app did later.
    /// </summary>
    private const string RecordAttributeAtDomContentLoaded = """
        document.addEventListener('DOMContentLoaded', () => {
            window.__themeAtDomContentLoaded = document.documentElement.getAttribute('data-bs-theme');
        });
        """;

    private readonly FleetE2ECollectionFixture _fixture;
    public DarkModeTests(FleetE2ECollectionFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task A_dark_OS_with_no_cookie_paints_dark_from_the_first_frame_and_the_main_area_uses_the_dark_body_background()
    {
        await using var pages = new PageFactory(_fixture);
        var page = await pages.NewPageAsync(colorScheme: ColorScheme.Dark);
        await page.AddInitScriptAsync(RecordAttributeAtDomContentLoaded);

        await page.GotoAsync("/");

        var atDomContentLoaded = await page.EvaluateAsync<string?>("() => window.__themeAtDomContentLoaded ?? null");
        atDomContentLoaded.Should().Be("dark",
            "the pre-boot script must set the attribute while <head> parses, or the page flashes light first");

        await WaitForShellAsync(page);
        (await ReadThemeAsync(page)).Should().Be("dark");
        (await ReadCookieAsync(page)).Should().BeNull("Auto is the default and nobody chose anything");

        var colours = await page.EvaluateAsync<string[]>("""
            () => {
                const probe = (scheme) => {
                    const el = document.createElement('div');
                    el.setAttribute('data-bs-theme', scheme);
                    el.style.backgroundColor = 'var(--bs-body-bg)';
                    document.body.appendChild(el);
                    const value = getComputedStyle(el).backgroundColor;
                    el.remove();
                    return value;
                };
                const main = document.querySelector('spark-shell main');
                return [main ? getComputedStyle(main).backgroundColor : 'no <main>', probe('dark'), probe('light')];
            }
            """);
        var (main, dark, light) = (colours[0], colours[1], colours[2]);
        dark.Should().NotBe(light, "the probes must tell the two schemes apart, or the comparison proves nothing");
        main.Should().Be(dark, "spark-shell's main area sits on the page's body background");
    }

    [Fact]
    public async Task In_Auto_an_OS_change_rethemes_the_page_without_a_reload()
    {
        await using var pages = new PageFactory(_fixture);
        var page = await pages.NewPageAsync(colorScheme: ColorScheme.Dark);
        await page.GotoAsync("/");
        await WaitForShellAsync(page);
        (await ReadThemeAsync(page)).Should().Be("dark");

        await page.EmulateMediaAsync(new() { ColorScheme = ColorScheme.Light });

        await WaitForThemeAsync(page, "light");
        (await ReadCookieAsync(page)).Should().BeNull("following the OS writes no choice");
    }

    [Fact]
    public async Task Choosing_Dark_applies_at_once_survives_a_reload_and_ignores_a_later_OS_change()
    {
        await using var pages = new PageFactory(_fixture);
        var page = await pages.NewPageAsync(colorScheme: ColorScheme.Light);
        await page.GotoAsync("/");
        await WaitForShellAsync(page);
        (await ReadThemeAsync(page)).Should().Be("light");

        await ChooseAsync(page, "dark");

        // Case 2: at once, and after a reload (the cookie carries it to the pre-boot script).
        await WaitForThemeAsync(page, "dark");
        (await ReadCookieAsync(page)).Should().Be("dark");
        await page.AddInitScriptAsync(RecordAttributeAtDomContentLoaded);
        await page.ReloadAsync();
        (await page.EvaluateAsync<string?>("() => window.__themeAtDomContentLoaded ?? null")).Should().Be("dark",
            "a stored choice must paint before Angular boots, not after");
        await WaitForShellAsync(page);
        (await ReadThemeAsync(page)).Should().Be("dark");

        // Case 3: an explicit choice is sticky — the OS turning light changes nothing.
        await page.EmulateMediaAsync(new() { ColorScheme = ColorScheme.Light });
        await page.EvaluateAsync("() => new Promise(r => requestAnimationFrame(() => requestAnimationFrame(r)))");
        (await ReadThemeAsync(page)).Should().Be("dark");
        (await ReadCookieAsync(page)).Should().Be("dark");
    }

    [Fact]
    public async Task A_choice_in_one_tab_rethemes_another_tab_of_the_same_browser()
    {
        await using var pages = new PageFactory(_fixture);
        var context = await pages.NewContextAsync(colorScheme: ColorScheme.Light);
        var tabA = await context.NewPageAsync();
        var tabB = await context.NewPageAsync();
        await tabA.GotoAsync("/");
        await tabB.GotoAsync("/");
        await WaitForShellAsync(tabA);
        await WaitForShellAsync(tabB);
        (await ReadThemeAsync(tabB)).Should().Be("light");

        await ChooseAsync(tabA, "dark");

        await WaitForThemeAsync(tabA, "dark");
        await WaitForThemeAsync(tabB, "dark"); // BroadcastChannel; no reload of B
    }

    private static Task WaitForShellAsync(IPage page)
        => page.Locator(Toggle).WaitForAsync(new() { Timeout = 30_000 });

    private static Task<string?> ReadThemeAsync(IPage page)
        => page.EvaluateAsync<string?>($"() => document.documentElement.getAttribute('{ThemeAttribute}')");

    private static Task WaitForThemeAsync(IPage page, string expected)
        => page.WaitForFunctionAsync(
            $"expected => document.documentElement.getAttribute('{ThemeAttribute}') === expected",
            expected,
            new() { Timeout = 10_000 });

    private static async Task<string?> ReadCookieAsync(IPage page)
    {
        var cookies = await page.Context.CookiesAsync();
        return cookies.FirstOrDefault(c => c.Name == ThemeCookie)?.Value;
    }

    /// <summary>
    /// Clicks the cycling toggle until the cookie holds <paramref name="mode"/>. Bounded at one full
    /// cycle: a toggle that never reaches the mode fails here rather than spinning.
    /// </summary>
    private static async Task ChooseAsync(IPage page, string mode)
    {
        for (var click = 0; click < 3; click++)
        {
            await page.Locator(Toggle).ClickAsync();
            if (await ReadCookieAsync(page) == mode) return;
        }
        throw new InvalidOperationException(
            $"three clicks on the theme toggle never stored '{mode}'; the cookie holds '{await ReadCookieAsync(page)}'");
    }
}
