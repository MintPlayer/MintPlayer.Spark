using MintPlayer.Spark.E2E.Tests._Infrastructure;

namespace MintPlayer.Spark.E2E.Tests.Security;

/// <summary>
/// H-5 — after a successful login the app must not navigate the user to an external URL
/// even if <c>?returnUrl=...</c> says so. The remediation validates returnUrl against a
/// configured allow-list (or a relative-only rule); these tests pin each attack shape.
///
/// Assertion strategy: after attempting login with a malicious returnUrl, the post-login
/// URL's *host* must match Fleet's host. Substring matching on "attacker.test" is unsafe
/// because the attacker string is preserved verbatim in <c>?returnUrl=...</c> when the
/// router refuses to navigate — a false positive. Host comparison tells us whether the
/// browser actually left the app origin, which is the real security question.
/// </summary>
[Collection(FleetE2ECollection.Name)]
public class ReturnUrlValidationTests
{
    private readonly FleetE2ECollectionFixture _fixture;
    public ReturnUrlValidationTests(FleetE2ECollectionFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Diagnostic_admin_can_log_in_via_the_form()
    {
        // Baseline: the H-5 suite relies on form-based login working. If this fails, the
        // attack-vector tests' "nav off-site?" assertions are meaningless (they all
        // trivially pass/fail without the router ever seeing a login success).
        var pages = new PageFactory(_fixture);
        var page = await pages.NewPageAsync();

        await page.GotoAsync("/login");

        var emailField = page.Locator("input#email").First;
        await emailField.WaitForAsync(new() { Timeout = 15_000 });
        await emailField.FillAsync(_fixture.Host.AdminEmailAddress);

        var passwordField = page.Locator("input#password, input[type='password']").First;
        await passwordField.FillAsync(_fixture.Host.AdminPass);

        await page.Locator("button[type='submit']").First.ClickAsync();

        var authenticated = await WaitForAuthenticatedAsync(page, TimeSpan.FromSeconds(10));
        authenticated.Should().BeTrue(
            $"form-based login must authenticate the session. Final url=[{page.Url}] " +
            $"error-alert=[{await TryGetErrorAlertAsync(page)}] " +
            $"--- Fleet log tail ---\n{_fixture.Host.RecentLog(40)}");
    }

    [Fact]
    public async Task Login_with_protocol_relative_returnUrl_does_not_navigate_off_site()
        => await AssertLoginStaysOnFleetHostAsync("//attacker.test/phish");

    [Fact]
    public async Task Login_with_absolute_http_returnUrl_does_not_navigate_off_site()
        => await AssertLoginStaysOnFleetHostAsync("http://attacker.test/phish");

    [Fact]
    public async Task Login_with_absolute_https_returnUrl_does_not_navigate_off_site()
        => await AssertLoginStaysOnFleetHostAsync("https://attacker.test/phish");

    [Fact]
    public async Task Login_with_javascript_uri_returnUrl_does_not_execute_script()
    {
        var page = await LoginViaFormAsync("javascript:alert('xss')");
        page.Url.Should().NotStartWith("javascript:",
            "javascript: URIs must never be honored as post-login redirects");
        AssertHostIsFleet(page.Url, "javascript: URI");
    }

    [Fact]
    public async Task Login_with_backslash_authority_returnUrl_does_not_navigate_off_site()
        // Some browsers treat \\host as //host — defence in depth.
        => await AssertLoginStaysOnFleetHostAsync("\\\\attacker.test/phish");

    [Fact]
    public async Task Login_with_whitespace_prefixed_returnUrl_does_not_navigate_off_site()
        // Leading whitespace can make some URL parsers treat remainder as protocol-relative.
        => await AssertLoginStaysOnFleetHostAsync(" //attacker.test/phish");

    private async Task AssertLoginStaysOnFleetHostAsync(string returnUrl)
    {
        var page = await LoginViaFormAsync(returnUrl);
        AssertHostIsFleet(page.Url, returnUrl);
    }

    private void AssertHostIsFleet(string finalUrl, string shape)
    {
        var finalUri = new Uri(finalUrl);
        var fleetUri = new Uri(_fixture.Host.FleetUrl);
        finalUri.Host.Should().Be(fleetUri.Host,
            $"returnUrl='{shape}' must not cause navigation to an external host. Final url=[{finalUrl}]");
    }

    private async Task<Microsoft.Playwright.IPage> LoginViaFormAsync(string returnUrl)
    {
        var pages = new PageFactory(_fixture);
        var page = await pages.NewPageAsync();

        await page.GotoAsync($"/login?returnUrl={Uri.EscapeDataString(returnUrl)}");

        // ng-spark-auth's login component renders the email field as <input type="text"
        // id="email" formControlName="email"> — no name attribute. Match on id.
        var emailField = page.Locator("input#email").First;
        await emailField.WaitForAsync(new() { Timeout = 15_000 });

        await emailField.FillAsync(_fixture.Host.AdminEmailAddress);
        var passwordField = page.Locator("input#password, input[type='password']").First;
        await passwordField.FillAsync(_fixture.Host.AdminPass);

        await page.Locator("button[type='submit']").First.ClickAsync();

        // Wait for the session cookie — login's side effect — to confirm the POST
        // /spark/auth/login round-trip completed before we inspect the URL.
        await WaitForAuthenticatedAsync(page, TimeSpan.FromSeconds(10));

        // Give the router a beat to settle its navigation decision.
        await page.WaitForTimeoutAsync(500);
        return page;
    }

    private async Task<bool> WaitForAuthenticatedAsync(Microsoft.Playwright.IPage page, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var me = await page.APIRequest.GetAsync($"{_fixture.Host.FleetUrl}/spark/auth/me");
            if ((await me.TextAsync()).Contains("\"isAuthenticated\":true")) return true;
            await page.WaitForTimeoutAsync(250);
        }
        return false;
    }

    private static async Task<string> TryGetErrorAlertAsync(Microsoft.Playwright.IPage page)
    {
        try
        {
            var alert = page.Locator("bs-alert").First;
            if (await alert.CountAsync() == 0) return "<none>";
            return await alert.InnerTextAsync(new() { Timeout = 1000 });
        }
        catch { return "<unreadable>"; }
    }
}
