using MintPlayer.Spark.E2E.Tests._Infrastructure;

namespace MintPlayer.Spark.E2E.Tests.Security;

/// <summary>
/// H-5 — after a successful login the app must not navigate the user to an external URL
/// even if <c>?returnUrl=...</c> says so. The remediation validates returnUrl against a
/// configured allow-list (or a relative-only rule); these tests pin each attack shape.
/// </summary>
[Collection(FleetE2ECollection.Name)]
public class ReturnUrlValidationTests
{
    private readonly FleetE2ECollectionFixture _fixture;
    public ReturnUrlValidationTests(FleetE2ECollectionFixture fixture) => _fixture = fixture;

    private async Task<Microsoft.Playwright.IPage> LoginViaFormAsync(string returnUrl)
    {
        var pages = new PageFactory(_fixture);
        var page = await pages.NewPageAsync();

        await page.GotoAsync($"/login?returnUrl={Uri.EscapeDataString(returnUrl)}");

        // Wait for the login form to render (Angular lazy-loads the chunk).
        var emailField = page.Locator("input[type='email'], input[name='email'], input[name='username']").First;
        await emailField.WaitForAsync(new() { Timeout = 15_000 });

        await emailField.FillAsync(_fixture.Host.AdminEmailAddress);
        var passwordField = page.Locator("input[type='password']").First;
        await passwordField.FillAsync(_fixture.Host.AdminPass);

        var submitButton = page.Locator("button[type='submit']").First;
        await submitButton.ClickAsync();

        // Give the router time to navigate.
        await page.WaitForTimeoutAsync(2000);
        return page;
    }

    [Fact]
    public async Task Login_with_protocol_relative_returnUrl_does_not_navigate_off_site()
    {
        var page = await LoginViaFormAsync("//attacker.test/phish");
        var finalUrl = page.Url;

        finalUrl.Should().StartWith(_fixture.Host.FleetUrl,
            "protocol-relative returnUrl must not leave the app origin");
        finalUrl.Should().NotContain("attacker.test");
    }

    [Fact]
    public async Task Login_with_absolute_http_returnUrl_does_not_navigate_off_site()
    {
        var page = await LoginViaFormAsync("http://attacker.test/phish");
        var finalUrl = page.Url;

        finalUrl.Should().StartWith(_fixture.Host.FleetUrl,
            "absolute http returnUrl must not leave the app origin");
        finalUrl.Should().NotContain("attacker.test");
    }

    [Fact]
    public async Task Login_with_absolute_https_returnUrl_does_not_navigate_off_site()
    {
        var page = await LoginViaFormAsync("https://attacker.test/phish");
        var finalUrl = page.Url;

        finalUrl.Should().StartWith(_fixture.Host.FleetUrl,
            "absolute https returnUrl must not leave the app origin");
        finalUrl.Should().NotContain("attacker.test");
    }

    [Fact]
    public async Task Login_with_javascript_uri_returnUrl_does_not_execute_script()
    {
        var page = await LoginViaFormAsync("javascript:alert('xss')");
        var finalUrl = page.Url;

        finalUrl.Should().NotStartWith("javascript:",
            "javascript: URIs must never be honored as post-login redirects");
    }

    [Fact]
    public async Task Login_with_backslash_authority_returnUrl_does_not_navigate_off_site()
    {
        // Some browsers treat \\host as //host — defence in depth.
        var page = await LoginViaFormAsync("\\\\attacker.test/phish");
        var finalUrl = page.Url;

        finalUrl.Should().StartWith(_fixture.Host.FleetUrl);
        finalUrl.Should().NotContain("attacker.test");
    }

    [Fact]
    public async Task Login_with_whitespace_prefixed_returnUrl_does_not_navigate_off_site()
    {
        // " //attacker" — some URL parsers trim leading whitespace and treat remainder
        // as protocol-relative. Defence in depth.
        var page = await LoginViaFormAsync(" //attacker.test/phish");
        var finalUrl = page.Url;

        finalUrl.Should().StartWith(_fixture.Host.FleetUrl);
        finalUrl.Should().NotContain("attacker.test");
    }
}
