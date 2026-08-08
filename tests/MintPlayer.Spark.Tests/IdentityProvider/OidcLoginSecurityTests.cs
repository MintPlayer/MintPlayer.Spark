using System.Net;

namespace MintPlayer.Spark.Tests.IdentityProvider;

/// <summary>
/// <c>/connect/login</c> and <c>/connect/logout</c> — the CSRF gate, the open-redirect gate,
/// lockout, and enumeration. Case ids refer to §L.
/// </summary>
public class OidcLoginSecurityTests : OidcTestHost
{
    private const string Email = "alice@test.local";

    private async Task<(Browser Browser, string Token)> LoginFormAsync(string returnUrl = "/")
    {
        var browser = NewBrowser();
        var page = await browser.GetAsync($"/connect/login?returnUrl={Uri.EscapeDataString(returnUrl)}");
        return (browser, AntiforgeryTokenFrom(await page.Content.ReadAsStringAsync()));
    }

    // ---------- antiforgery ----------

    /// <summary>L-A1 — the positive control. Without it every refusal below proves nothing.</summary>
    [Fact]
    public async Task Login_with_a_valid_token_signs_the_user_in()
    {
        await SeedUserAsync(Email);
        var (browser, token) = await LoginFormAsync();

        var response = await browser.PostFormAsync("/connect/login", new Dictionary<string, string>
        {
            ["email"] = Email, ["password"] = Password, ["returnUrl"] = "/",
            ["__RequestVerificationToken"] = token,
        });

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.Should().Be("/");
    }

    /// <summary>L-A2 — the same request with the token withheld.</summary>
    [Fact]
    public async Task Login_without_an_antiforgery_token_is_rejected()
    {
        await SeedUserAsync(Email);
        var (browser, _) = await LoginFormAsync();

        var response = await browser.PostFormAsync("/connect/login", new Dictionary<string, string>
        {
            ["email"] = Email, ["password"] = Password, ["returnUrl"] = "/",
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    /// <summary>L-A4 — a token without its cookie is not a token.</summary>
    [Fact]
    public async Task Login_with_a_token_but_no_antiforgery_cookie_is_rejected()
    {
        await SeedUserAsync(Email);
        var (browser, token) = await LoginFormAsync();

        foreach (var name in new[] { ".AspNetCore.Antiforgery", "XSRF-TOKEN" })
            DropMatching(browser, name);

        var response = await browser.PostFormAsync("/connect/login", new Dictionary<string, string>
        {
            ["email"] = Email, ["password"] = Password, ["returnUrl"] = "/",
            ["__RequestVerificationToken"] = token,
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    /// <summary>L-A6 — a token minted for a different session.</summary>
    [Fact]
    public async Task Login_with_a_token_from_another_session_is_rejected()
    {
        await SeedUserAsync(Email);
        var (_, foreignToken) = await LoginFormAsync();
        var (browser, _) = await LoginFormAsync();

        var response = await browser.PostFormAsync("/connect/login", new Dictionary<string, string>
        {
            ["email"] = Email, ["password"] = Password, ["returnUrl"] = "/",
            ["__RequestVerificationToken"] = foreignToken,
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "the token is bound to the cookie's secret, so a mismatched pair must not validate");
    }

    /// <summary>L-A7 — right value, wrong field name.</summary>
    [Fact]
    public async Task Login_with_the_token_under_the_wrong_field_name_is_rejected()
    {
        await SeedUserAsync(Email);
        var (browser, token) = await LoginFormAsync();

        var response = await browser.PostFormAsync("/connect/login", new Dictionary<string, string>
        {
            ["email"] = Email, ["password"] = Password, ["returnUrl"] = "/",
            ["AntiforgeryToken"] = token,
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ---------- returnUrl ----------

    /// <summary>L-R1 — the rendered form never carries an off-origin destination.</summary>
    [Theory]
    [InlineData("https://attacker.test/phish")]
    [InlineData("//attacker.test")]
    [InlineData("/\\evil.com")]
    [InlineData("javascript:alert(1)")]
    public async Task Login_page_sanitizes_the_return_url_it_renders(string returnUrl)
    {
        var page = await NewBrowser().GetAsync($"/connect/login?returnUrl={Uri.EscapeDataString(returnUrl)}");
        var html = await page.Content.ReadAsStringAsync();

        html.Should().NotContain("attacker.test");
        html.Should().NotContain("evil.com");
        html.Should().NotContain("javascript:");
    }

    /// <summary>L-R2..L-R9 — and a successful sign-in never lands off-origin.</summary>
    [Theory]
    [InlineData("https://attacker.test/phish")]
    [InlineData("//attacker.test")]
    [InlineData("/\\evil.com")]
    [InlineData("%2F%2Fevil.com")]
    [InlineData("%252F%252Fevil.com")]
    [InlineData("javascript:alert(1)")]
    [InlineData("/ok\r\nSet-Cookie: injected=1")]
    public async Task Login_never_redirects_off_origin(string returnUrl)
    {
        await SeedUserAsync(Email);
        var (browser, token) = await LoginFormAsync();

        var response = await browser.PostFormAsync("/connect/login", new Dictionary<string, string>
        {
            ["email"] = Email, ["password"] = Password, ["returnUrl"] = returnUrl,
            ["__RequestVerificationToken"] = token,
        });

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var location = response.Headers.Location!.OriginalString;
        location.Should().StartWith("/");
        location.Should().NotStartWith("//");
        location.Should().NotContain("evil.com");
        location.Should().NotContain("attacker.test");
        response.Headers.Contains("Set-Cookie").Should().BeTrue();
        response.Headers.TryGetValues("Set-Cookie", out var cookies);
        cookies!.Should().NotContain(c => c.Contains("injected=1"), "no header injection via returnUrl");
    }

    // ---------- credentials ----------

    /// <summary>L-L4 — unknown address and wrong password are indistinguishable by message.</summary>
    [Fact]
    public async Task Unknown_email_and_wrong_password_produce_the_same_error()
    {
        await SeedUserAsync(Email);

        var unknown = await AttemptAsync("nobody@test.local", Password);
        var wrong = await AttemptAsync(Email, "Aa1!wrong-password");

        unknown.Should().Be(wrong, "differing text would enumerate registered addresses");
    }

    /// <summary>L-O24 — the error box shows our words, not the caller's.</summary>
    [Fact]
    public async Task Login_page_does_not_reflect_attacker_supplied_error_text()
    {
        const string injected = "Your session expired, please confirm your password at evil.com";
        var page = await NewBrowser().GetAsync($"/connect/login?error={Uri.EscapeDataString(injected)}");
        var html = await page.Content.ReadAsStringAsync();

        html.Should().NotContain("evil.com",
            "an unrecognised code must fall back to a fixed message — the styled error box on the "
            + "real origin above a real password field is too good a place to put someone else's words");
    }

    /// <summary>L-L1 — lockout engages, and then even the right password is refused.</summary>
    [Fact]
    public async Task Repeated_failures_lock_the_account()
    {
        await SeedUserAsync(Email);

        for (var i = 0; i < 6; i++)
            await AttemptAsync(Email, "Aa1!wrong-password");

        var withCorrectPassword = await AttemptAsync(Email, Password);

        withCorrectPassword.Should().Contain("locked_out",
            "lockoutOnFailure must reach AccessFailedAsync, or the endpoint is an unlimited password oracle");
    }

    private async Task<string> AttemptAsync(string email, string password)
    {
        var (browser, token) = await LoginFormAsync();
        var response = await browser.PostFormAsync("/connect/login", new Dictionary<string, string>
        {
            ["email"] = email, ["password"] = password, ["returnUrl"] = "/",
            ["__RequestVerificationToken"] = token,
        });

        return response.Headers.Location?.OriginalString ?? "";
    }

    // ---------- logout ----------

    /// <summary>L-G1/L-G2 — the destination must be registered by the client asking.</summary>
    [Fact]
    public async Task Logout_honours_a_registered_post_logout_uri_and_refuses_others()
    {
        await SeedApplicationAsync("webapp", postLogoutRedirectUris: ["https://webapp.test/done"]);

        var ok = await NewBrowser().GetAsync(
            "/connect/logout?client_id=webapp&post_logout_redirect_uri=" + Uri.EscapeDataString("https://webapp.test/done"));
        ok.StatusCode.Should().Be(HttpStatusCode.Redirect);

        var refused = await NewBrowser().GetAsync(
            "/connect/logout?client_id=webapp&post_logout_redirect_uri=" + Uri.EscapeDataString("https://attacker.test/done"));
        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    /// <summary>L-G4 / O12 — one client's registered URI is not a destination for another.</summary>
    [Fact]
    public async Task Logout_refuses_another_applications_post_logout_uri()
    {
        await SeedApplicationAsync("webapp", postLogoutRedirectUris: ["https://webapp.test/done"]);
        await SeedApplicationAsync("otherapp", postLogoutRedirectUris: ["https://otherapp.test/done"]);

        var response = await NewBrowser().GetAsync(
            "/connect/logout?client_id=webapp&post_logout_redirect_uri=" + Uri.EscapeDataString("https://otherapp.test/done"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "validating across every enabled application would give anyone able to register a "
            + "client a redirect through this provider's origin for all of them");
    }

    /// <summary>L-G3 — a registered URI carrying a query string stays well formed.</summary>
    [Fact]
    public async Task Logout_appends_state_to_a_uri_that_already_has_a_query()
    {
        await SeedApplicationAsync("webapp", postLogoutRedirectUris: ["https://webapp.test/done?tenant=1"]);

        var response = await NewBrowser().GetAsync(
            "/connect/logout?client_id=webapp&state=xyz&post_logout_redirect_uri="
            + Uri.EscapeDataString("https://webapp.test/done?tenant=1"));

        var location = response.Headers.Location!.OriginalString;
        location.Should().Be("https://webapp.test/done?tenant=1&state=xyz");
        location.Should().NotContain("?tenant=1?");
    }

    /// <summary>L-S3 — signing out clears the session.</summary>
    [Fact]
    public async Task Logout_clears_the_authentication_cookie()
    {
        await SeedUserAsync(Email);
        var browser = await SignInAsync(Email);
        await SeedApplicationAsync("webapp");

        await browser.GetAsync("/connect/logout");

        var consent = await browser.GetAsync("/connect/consent?request_id=anything");
        consent.StatusCode.Should().Be(HttpStatusCode.Redirect,
            "an unauthenticated consent GET bounces to login, which is what a cleared session looks like");
    }

    private static void DropMatching(Browser browser, string prefix)
    {
        // The antiforgery cookie name carries a generated suffix, so drop by prefix.
        foreach (var name in new[] { prefix, prefix + "." })
            browser.DropCookie(name);

        browser.DropCookiesStartingWith(prefix);
    }
}
