using System.Net;
using Microsoft.AspNetCore.Identity;

namespace MintPlayer.Spark.Tests.IdentityProvider;

/// <summary>
/// Two-factor authentication. Case ids refer to §L.4 of the matrix.
/// <para>
/// The load-bearing property is that the password step alone is <em>not</em> a session: it
/// yields a partial-authentication cookie under a different scheme, and everything downstream —
/// <c>/connect/authorize</c>, the consent hop — resolves identity against the application
/// scheme only. If that ever stopped holding, 2FA would become decorative while every screen
/// still displayed it.
/// </para>
/// </summary>
public class OidcTwoFactorSecurityTests : OidcTestHost
{
    private const string Email = "alice@test.local";

    // ---------- must succeed ----------

    /// <summary>L-T1 — password alone is not enough, and the flow says so.</summary>
    [Fact]
    public async Task Password_step_redirects_to_the_second_factor()
    {
        await SeedTwoFactorUserAsync(Email);

        var browser = await PasswordStepAsync(Email);

        browser.HasCookie(".SparkAuth").Should().BeFalse("no application cookie yet");
    }

    /// <summary>L-T2 — a valid authenticator code completes the sign-in.</summary>
    [Fact]
    public async Task A_valid_authenticator_code_completes_sign_in()
    {
        await SeedTwoFactorUserAsync(Email);
        var browser = await PasswordStepAsync(Email);

        var response = await SubmitTwoFactorAsync(browser, code: await AuthenticatorCodeAsync(Email));

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.Should().Be("/");
    }

    /// <summary>L-T3 — a recovery code is the documented fallback and must work.</summary>
    [Fact]
    public async Task A_valid_recovery_code_completes_sign_in()
    {
        var codes = await SeedTwoFactorUserAsync(Email);
        var browser = await PasswordStepAsync(Email);

        var response = await SubmitTwoFactorAsync(browser, recoveryCode: codes[0]);

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.Should().Be("/");
    }

    /// <summary>L-T4 — completing both factors yields a session that actually works.</summary>
    [Fact]
    public async Task A_completed_two_factor_sign_in_can_drive_the_authorization_flow()
    {
        var app = await SeedApplicationAsync("webapp");
        await SeedTwoFactorUserAsync(Email);

        var browser = await PasswordStepAsync(Email);
        await SubmitTwoFactorAsync(browser, code: await AuthenticatorCodeAsync(Email));

        var authorize = await browser.GetAsync(
            $"/connect/authorize?client_id={app.ClientId}&redirect_uri={Uri.EscapeDataString(app.RedirectUris[0])}"
          + "&response_type=code&scope=openid");

        authorize.StatusCode.Should().Be(HttpStatusCode.Redirect);
        authorize.Headers.Location!.OriginalString.Should().StartWith("/connect/consent",
            "the positive control — without it, every skip test below would pass against a flow "
            + "that simply never works");
    }

    // ---------- the second factor cannot be skipped ----------

    /// <summary>
    /// L-T5 — the central case. After only the password step, the partial cookie must not
    /// satisfy <c>/connect/authorize</c>.
    /// </summary>
    [Fact]
    public async Task The_partial_authentication_cookie_cannot_drive_the_authorization_flow()
    {
        var app = await SeedApplicationAsync("webapp");
        await SeedTwoFactorUserAsync(Email);

        var browser = await PasswordStepAsync(Email);

        var authorize = await browser.GetAsync(
            $"/connect/authorize?client_id={app.ClientId}&redirect_uri={Uri.EscapeDataString(app.RedirectUris[0])}"
          + "&response_type=code&scope=openid");

        authorize.StatusCode.Should().Be(HttpStatusCode.Redirect);
        authorize.Headers.Location!.OriginalString.Should().StartWith("/connect/login",
            "half-authenticated must be indistinguishable from unauthenticated to everything "
            + "downstream, or the second factor is decorative");
    }

    /// <summary>L-T6 — nor the consent hop, reached directly.</summary>
    [Fact]
    public async Task The_partial_authentication_cookie_cannot_reach_the_consent_page()
    {
        var app = await SeedApplicationAsync("webapp");
        await SeedTwoFactorUserAsync(Email);
        var user = await WithUserManagerAsync(async u => await u.FindByEmailAsync(Email));
        var requestId = await SeedAuthorizationRequestAsync(app, user!.Id!);

        var browser = await PasswordStepAsync(Email);

        var consent = await browser.GetAsync($"/connect/consent?request_id={requestId}");

        consent.StatusCode.Should().Be(HttpStatusCode.Redirect);
        consent.Headers.Location!.OriginalString.Should().StartWith("/connect/login");
    }

    /// <summary>L-T7 — and a consent POST is refused outright.</summary>
    [Fact]
    public async Task The_partial_authentication_cookie_cannot_post_consent()
    {
        var app = await SeedApplicationAsync("webapp");
        await SeedTwoFactorUserAsync(Email);
        var user = await WithUserManagerAsync(async u => await u.FindByEmailAsync(Email));
        var requestId = await SeedAuthorizationRequestAsync(app, user!.Id!);

        var browser = await PasswordStepAsync(Email);

        var consent = await browser.PostFormAsync("/connect/consent", new Dictionary<string, string>
        {
            ["request_id"] = requestId, ["decision"] = "allow", ["scopes"] = "openid",
        });

        consent.StatusCode.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.BadRequest);
    }

    /// <summary>L-T8 — submitting the 2FA form without ever passing the password step.</summary>
    [Fact]
    public async Task The_two_factor_step_cannot_be_completed_without_the_password_step()
    {
        await SeedTwoFactorUserAsync(Email);
        var code = await AuthenticatorCodeAsync(Email);

        var stranger = NewBrowser();
        var response = await SubmitTwoFactorAsync(stranger, code: code);

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.Should().Contain("error",
            "there is no partial-auth principal to resolve, so a correct code proves nothing");
        response.Headers.Location!.OriginalString.Should().NotBe("/");
    }

    // ---------- wrong credentials ----------

    /// <summary>L-T9 — a wrong authenticator code.</summary>
    [Fact]
    public async Task An_invalid_authenticator_code_is_refused()
    {
        await SeedTwoFactorUserAsync(Email);
        var browser = await PasswordStepAsync(Email);

        var response = await SubmitTwoFactorAsync(browser, code: "000000");

        response.Headers.Location!.OriginalString.Should().Contain("error=invalid_code");
    }

    /// <summary>L-T10 — a wrong recovery code.</summary>
    [Fact]
    public async Task An_invalid_recovery_code_is_refused()
    {
        await SeedTwoFactorUserAsync(Email);
        var browser = await PasswordStepAsync(Email);

        var response = await SubmitTwoFactorAsync(browser, recoveryCode: "not-a-real-code");

        response.Headers.Location!.OriginalString.Should().Contain("error=invalid_recovery_code");
    }

    /// <summary>L-T11 — an empty submission is refused rather than treated as a pass.</summary>
    [Fact]
    public async Task An_empty_code_is_refused()
    {
        await SeedTwoFactorUserAsync(Email);
        var browser = await PasswordStepAsync(Email);

        var response = await SubmitTwoFactorAsync(browser, code: "");

        response.Headers.Location!.OriginalString.Should().Contain("error=missing_code");
    }

    /// <summary>
    /// L-T12 — another user's valid code must not complete this user's sign-in. The code is
    /// verified against the partial-auth principal, not merely against "some user".
    /// </summary>
    [Fact]
    public async Task Another_users_authenticator_code_is_refused()
    {
        await SeedTwoFactorUserAsync(Email);
        await SeedTwoFactorUserAsync("bob@test.local");

        var browser = await PasswordStepAsync(Email);
        var bobsCode = await AuthenticatorCodeAsync("bob@test.local");

        var response = await SubmitTwoFactorAsync(browser, code: bobsCode);

        response.Headers.Location!.OriginalString.Should().Contain("error=invalid_code");
    }

    /// <summary>L-T13 — likewise another user's recovery code.</summary>
    [Fact]
    public async Task Another_users_recovery_code_is_refused()
    {
        await SeedTwoFactorUserAsync(Email);
        var bobsCodes = await SeedTwoFactorUserAsync("bob@test.local");

        var browser = await PasswordStepAsync(Email);

        var response = await SubmitTwoFactorAsync(browser, recoveryCode: bobsCodes[0]);

        response.Headers.Location!.OriginalString.Should().Contain("error=invalid_recovery_code");
    }

    // ---------- recovery-code lifecycle ----------

    /// <summary>L-T14 — a recovery code is spent by use.</summary>
    [Fact]
    public async Task A_recovery_code_cannot_be_used_twice()
    {
        var codes = await SeedTwoFactorUserAsync(Email);

        var first = await SubmitTwoFactorAsync(await PasswordStepAsync(Email), recoveryCode: codes[0]);
        first.Headers.Location!.OriginalString.Should().Be("/");

        var second = await SubmitTwoFactorAsync(await PasswordStepAsync(Email), recoveryCode: codes[0]);

        second.Headers.Location!.OriginalString.Should().Contain("error=invalid_recovery_code",
            "a recovery code is single-use — otherwise a leaked one is a permanent bypass");
    }

    /// <summary>L-T15 — spending one code does not spend the others.</summary>
    [Fact]
    public async Task Spending_one_recovery_code_leaves_the_rest_usable()
    {
        var codes = await SeedTwoFactorUserAsync(Email);

        await SubmitTwoFactorAsync(await PasswordStepAsync(Email), recoveryCode: codes[0]);
        var second = await SubmitTwoFactorAsync(await PasswordStepAsync(Email), recoveryCode: codes[1]);

        second.Headers.Location!.OriginalString.Should().Be("/");
    }

    /// <summary>L-T16 — the remaining count is what it should be.</summary>
    [Fact]
    public async Task Recovery_codes_are_counted_down_as_they_are_spent()
    {
        var codes = await SeedTwoFactorUserAsync(Email, recoveryCodes: 3);

        await SubmitTwoFactorAsync(await PasswordStepAsync(Email), recoveryCode: codes[0]);

        var remaining = await WithUserManagerAsync(async users =>
        {
            var user = await users.FindByEmailAsync(Email);
            return await users.CountRecoveryCodesAsync(user!);
        });

        remaining.Should().Be(2);
    }

    // ---------- antiforgery and returnUrl ----------

    /// <summary>L-T17 — the CSRF gate applies here too.</summary>
    [Fact]
    public async Task The_two_factor_post_is_rejected_without_an_antiforgery_token()
    {
        await SeedTwoFactorUserAsync(Email);
        var browser = await PasswordStepAsync(Email);

        var response = await SubmitTwoFactorAsync(
            browser, code: await AuthenticatorCodeAsync(Email), includeAntiforgery: false);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    /// <summary>L-T18 — a token minted for another session does not validate.</summary>
    [Fact]
    public async Task The_two_factor_post_rejects_a_token_from_another_session()
    {
        await SeedTwoFactorUserAsync(Email);
        var victim = await PasswordStepAsync(Email);

        var stranger = NewBrowser();
        var strangerToken = AntiforgeryTokenFrom(
            await (await stranger.GetAsync("/connect/two-factor?returnUrl=/")).Content.ReadAsStringAsync());

        var response = await victim.PostFormAsync("/connect/two-factor", new Dictionary<string, string>
        {
            ["code"] = await AuthenticatorCodeAsync(Email),
            ["returnUrl"] = "/",
            ["__RequestVerificationToken"] = strangerToken,
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    /// <summary>L-T19 — a completed second factor still cannot be sent off-origin.</summary>
    [Theory]
    [InlineData("https://attacker.test/phish")]
    [InlineData("//attacker.test")]
    [InlineData("/\\evil.com")]
    [InlineData("javascript:alert(1)")]
    public async Task Two_factor_never_redirects_off_origin(string returnUrl)
    {
        await SeedTwoFactorUserAsync(Email);
        var browser = await PasswordStepAsync(Email, returnUrl: returnUrl);

        var response = await SubmitTwoFactorAsync(
            browser, code: await AuthenticatorCodeAsync(Email), returnUrl: returnUrl);

        var location = response.Headers.Location!.OriginalString;
        location.Should().StartWith("/");
        location.Should().NotStartWith("//");
        location.Should().NotContain("attacker.test");
        location.Should().NotContain("evil.com");
    }

    /// <summary>L-T20 — the rendered form does not carry an off-origin destination either.</summary>
    [Fact]
    public async Task The_two_factor_page_sanitizes_the_return_url_it_renders()
    {
        await SeedTwoFactorUserAsync(Email);
        var browser = await PasswordStepAsync(Email);

        var page = await browser.GetAsync(
            "/connect/two-factor?returnUrl=" + Uri.EscapeDataString("https://attacker.test/phish"));

        (await page.Content.ReadAsStringAsync()).Should().NotContain("attacker.test");
    }

    /// <summary>L-T21 — attacker-authored copy cannot be placed in the error box.</summary>
    [Fact]
    public async Task The_two_factor_page_does_not_reflect_supplied_error_text()
    {
        await SeedTwoFactorUserAsync(Email);
        var browser = await PasswordStepAsync(Email);

        var page = await browser.GetAsync(
            "/connect/two-factor?error=" + Uri.EscapeDataString("Call support at evil.com to verify"));

        (await page.Content.ReadAsStringAsync()).Should().NotContain("evil.com");
    }

    // ---------- account state ----------

    /// <summary>
    /// L-T22 — a locked-out account must not reach the second factor at all: lockout is checked
    /// before the password is even evaluated, so the correct password gives nothing.
    /// </summary>
    [Fact]
    public async Task A_locked_out_account_does_not_reach_the_second_factor()
    {
        await SeedTwoFactorUserAsync(Email);

        for (var i = 0; i < 6; i++)
        {
            var browser = NewBrowser();
            var page = await browser.GetAsync("/connect/login?returnUrl=/");
            await browser.PostFormAsync("/connect/login", new Dictionary<string, string>
            {
                ["email"] = Email,
                ["password"] = "Aa1!wrong-password",
                ["returnUrl"] = "/",
                ["__RequestVerificationToken"] = AntiforgeryTokenFrom(await page.Content.ReadAsStringAsync()),
            });
        }

        var final = NewBrowser();
        var loginPage = await final.GetAsync("/connect/login?returnUrl=/");
        var response = await final.PostFormAsync("/connect/login", new Dictionary<string, string>
        {
            ["email"] = Email,
            ["password"] = Password,
            ["returnUrl"] = "/",
            ["__RequestVerificationToken"] = AntiforgeryTokenFrom(await loginPage.Content.ReadAsStringAsync()),
        });

        var location = response.Headers.Location!.OriginalString;
        location.Should().Contain("locked_out");
        location.Should().NotContain("two-factor");
    }

    /// <summary>
    /// L-T7 — the second factor must not override the choice made on the login page. It used to:
    /// <c>TwoFactorAuthenticatorSignInAsync</c> was called with <c>isPersistent: true</c>
    /// hardcoded, and the checkbox value was dropped entirely between the two hops, so a user who
    /// declined a persistent cookie got one anyway.
    /// </summary>
    [Fact]
    public async Task Declining_remember_me_does_not_yield_a_persistent_cookie_after_two_factor()
    {
        await SeedTwoFactorUserAsync(Email);
        var browser = await PasswordStepAsync(Email);

        var response = await SubmitTwoFactorAsync(browser, code: await AuthenticatorCodeAsync(Email));

        ApplicationCookie(response).Should().NotContain("expires=",
            "declining remember-me must yield a session cookie, not a persistent one");
    }

    /// <summary>L-T8 — and accepting it must still work.</summary>
    [Fact]
    public async Task Accepting_remember_me_survives_the_two_factor_hop()
    {
        await SeedTwoFactorUserAsync(Email);

        var browser = NewBrowser();
        var page = await browser.GetAsync("/connect/login?returnUrl=%2F");
        var login = await browser.PostFormAsync("/connect/login", new Dictionary<string, string>
        {
            ["email"] = Email,
            ["password"] = Password,
            ["returnUrl"] = "/",
            ["rememberMe"] = "true",
            ["__RequestVerificationToken"] = AntiforgeryTokenFrom(await page.Content.ReadAsStringAsync()),
        });

        login.Headers.Location!.OriginalString.Should().Contain("rememberMe=true",
            "the choice has to travel to the hop that spends it");

        var response = await SubmitTwoFactorAsync(
            browser, code: await AuthenticatorCodeAsync(Email), rememberMe: true);

        ApplicationCookie(response).Should().Contain("expires=");
    }

    /// <summary>
    /// L-T16 — once locked out, even a correct code yields the generic message. TwoFactor.cs has
    /// no <c>IsLockedOut</c> branch, unlike Login.cs. That is not a defect — it discloses less —
    /// but it is asymmetric, so pin it as observed behaviour rather than leaving it to be
    /// "fixed" into an oracle.
    /// </summary>
    [Fact]
    public async Task A_locked_out_account_at_the_second_factor_gets_the_generic_message()
    {
        await SeedTwoFactorUserAsync(Email);
        var browser = await PasswordStepAsync(Email);

        for (var i = 0; i < 8; i++)
            await SubmitTwoFactorAsync(browser, code: "000000");

        var response = await SubmitTwoFactorAsync(browser, code: await AuthenticatorCodeAsync(Email));

        response.Headers.Location!.OriginalString.Should().Contain("error=invalid_code");
        response.Headers.Location!.OriginalString.Should().NotContain("locked_out");
    }

    /// <summary>
    /// L-T15 — repeated wrong codes must eventually stop working even when the code is right,
    /// i.e. failures at the second factor feed the same lockout counter as failures at the first.
    /// Written to observe: this rides framework behaviour, not Spark's own code.
    /// </summary>
    [Fact]
    public async Task Repeated_wrong_codes_eventually_lock_the_account()
    {
        await SeedTwoFactorUserAsync(Email);
        var browser = await PasswordStepAsync(Email);

        for (var i = 0; i < 8; i++)
            await SubmitTwoFactorAsync(browser, code: "000000");

        var locked = await WithUserManagerAsync(async users =>
        {
            var user = await users.FindByEmailAsync(Email);
            return await users.IsLockedOutAsync(user!);
        });

        locked.Should().BeTrue("otherwise the second factor is an unthrottled six-digit oracle");
    }

    /// <summary>L-T18 — 2FA enabled with no authenticator key must fail closed, not fault.</summary>
    [Fact]
    public async Task Two_factor_enabled_without_an_authenticator_key_fails_closed()
    {
        await SeedUserAsync(Email);
        await WithUserManagerAsync(async users =>
        {
            var user = await users.FindByEmailAsync(Email);
            return await users.SetTwoFactorEnabledAsync(user!, true);
        });

        var browser = await PasswordStepAsync(Email);
        var response = await SubmitTwoFactorAsync(browser, code: "123456");

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.Should().Contain("error=invalid_code",
            "no key means nothing can verify — refuse, do not throw");
    }

    /// <summary>
    /// L-T14 — a recovery code is single-use even under concurrent redemption. Read-modify-write
    /// over the user document meant two requests could each load the user, each remove the same
    /// code from their own copy, and both succeed.
    /// </summary>
    [Fact]
    public async Task A_recovery_code_cannot_be_spent_twice_concurrently()
    {
        var codes = await SeedTwoFactorUserAsync(Email, recoveryCodes: 1);

        var browsers = new List<Browser>();
        for (var i = 0; i < 4; i++)
            browsers.Add(await PasswordStepAsync(Email));

        var attempts = await Task.WhenAll(browsers.Select(b => SubmitTwoFactorAsync(b, recoveryCode: codes[0])));

        var succeeded = attempts.Count(r => r.Headers.Location?.OriginalString == "/");

        succeeded.Should().Be(1,
            "a single-use two-factor bypass spendable more than once is not single-use");

        var remaining = await WithUserManagerAsync(async users =>
        {
            var user = await users.FindByEmailAsync(Email);
            return await users.CountRecoveryCodesAsync(user!);
        });

        remaining.Should().Be(0);
    }

    /// <summary>
    /// L-T13 — recovery codes are never at rest in the clear. A recovery code is a standing
    /// second-factor bypass, so a database dump must not be a dump of working bypasses.
    /// </summary>
    [Fact]
    public async Task Recovery_codes_are_stored_hashed()
    {
        var codes = await SeedTwoFactorUserAsync(Email);

        var stored = await WithUserManagerAsync(async users => (await users.FindByEmailAsync(Email))!);

        stored.TwoFactorRecoveryCodes.Should().NotBeEmpty();
        stored.TwoFactorRecoveryCodes.Should().NotContain(codes[0],
            "the issued code itself must not be recoverable from the document");
        stored.TwoFactorRecoveryCodes.Should().OnlyContain(h => h.Length == 64,
            "each entry should be a SHA-256 hex digest, not a code");
    }

    private static string ApplicationCookie(HttpResponseMessage response)
    {
        response.Headers.TryGetValues("Set-Cookie", out var cookies).Should().BeTrue();
        return cookies!.FirstOrDefault(c => c.StartsWith(".SparkAuth", StringComparison.Ordinal)
                                         || c.Contains("Identity.Application", StringComparison.Ordinal))
            ?? throw new InvalidOperationException(
                "No application cookie in: " + string.Join(" | ", cookies!));
    }

    /// <summary>L-T23 — disabling 2FA signs the user in at the password step, as configured.</summary>
    [Fact]
    public async Task Disabling_two_factor_returns_the_account_to_a_single_step()
    {
        await SeedTwoFactorUserAsync(Email);
        await WithUserManagerAsync(async users =>
        {
            var user = await users.FindByEmailAsync(Email);
            return await users.SetTwoFactorEnabledAsync(user!, false);
        });

        var browser = NewBrowser();
        var page = await browser.GetAsync("/connect/login?returnUrl=/");
        var response = await browser.PostFormAsync("/connect/login", new Dictionary<string, string>
        {
            ["email"] = Email,
            ["password"] = Password,
            ["returnUrl"] = "/",
            ["__RequestVerificationToken"] = AntiforgeryTokenFrom(await page.Content.ReadAsStringAsync()),
        });

        response.Headers.Location!.OriginalString.Should().Be("/");
    }
}
