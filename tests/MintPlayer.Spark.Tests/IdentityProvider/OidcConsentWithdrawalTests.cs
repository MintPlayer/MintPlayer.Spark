using System.Net;
using System.Text.Json;
using MintPlayer.Spark.IdentityProvider.Models;
using MintPlayer.Spark.IdentityProvider.Services;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Tests.IdentityProvider;

/// <summary>
/// Consent withdrawal, driven over HTTP from end to end. Case ids refer to
/// docs/idp-e2e-test-matrix.md §W.
/// <para>
/// These are deliberately seam tests. The predicted failure for this feature was that the page
/// and the token endpoint would disagree about what "withdrawn" is written as — the page asserting
/// the document changed, the token endpoint asserting a seeded grant is honoured, both green, and
/// the feature a no-op with a UI that confirms success. So nothing here seeds a withdrawn grant:
/// every case withdraws through the real endpoint and then asks the real endpoint that is supposed
/// to notice.
/// </para>
/// </summary>
public class OidcConsentWithdrawalTests : OidcTestHost
{
    private const string Secret = "s3cret-value-for-tests";

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    /// <summary>Signs in, consents, and redeems — returning the browser and the token response.</summary>
    private async Task<(Browser Browser, JsonElement Tokens)> EstablishGrantAsync(
        OidcApplication app, string email, string[] scopes)
    {
        var browser = await SignInAsync(email);
        var code = await ObtainCodeAsync(app, email, scopes);

        var tokens = await BodyAsync(await Client.PostAsync("/connect/token", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["client_id"] = app.ClientId,
                ["client_secret"] = Secret,
                ["code"] = code,
                ["redirect_uri"] = app.RedirectUris[0],
            })));

        return (browser, tokens);
    }

    /// <summary>
    /// A token minted from the page while it still has a row to render.
    /// <para>
    /// The listing rides an eventually-consistent index, so the wait is not optional: without it
    /// the page renders before the grant appears, there is no form, and the test fails claiming
    /// there is no antiforgery field — a fixture race that reads exactly like a product defect.
    /// </para>
    /// </summary>
    private async Task<string> AntiforgeryFromApplicationsPageAsync(Browser browser)
    {
        WaitForIndexing(Store);
        var page = await browser.GetAsync("/connect/applications");
        return AntiforgeryTokenFrom(await page.Content.ReadAsStringAsync());
    }

    private async Task<string> WithdrawAsync(Browser browser, string applicationId, bool includeAntiforgery = true)
    {
        var form = new Dictionary<string, string> { ["application_id"] = applicationId };
        if (includeAntiforgery)
            form["__RequestVerificationToken"] = await AntiforgeryFromApplicationsPageAsync(browser);
        else
            await browser.GetAsync("/connect/applications");

        return await PostWithdrawalAsync(browser, form);
    }

    private static async Task<string> PostWithdrawalAsync(Browser browser, Dictionary<string, string> form)
    {
        var response = await browser.PostFormAsync("/connect/applications/revoke", form);
        return response.StatusCode == HttpStatusCode.Redirect
            ? response.Headers.Location!.OriginalString
            : ((int)response.StatusCode).ToString();
    }

    private Task<HttpResponseMessage> RefreshAsync(OidcApplication app, string refreshToken)
        => Client.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = app.ClientId,
            ["client_secret"] = Secret,
            ["refresh_token"] = refreshToken,
        }));

    private async Task<OidcApplication> SeedRefreshableAppAsync(string clientId, string consentType = "explicit")
        => await SeedApplicationAsync(clientId,
            allowedScopes: ["openid", "api.read", "offline_access"],
            grantTypes: ["authorization_code", "refresh_token"],
            consentType: consentType);

    /// <summary>W-H1 — the flow works before anything is withdrawn.</summary>
    [Fact]
    public async Task A_granted_application_can_refresh()
    {
        var app = await SeedRefreshableAppAsync("w-happy");
        await SeedUserAsync("happy@test.local");

        var (_, tokens) = await EstablishGrantAsync(app, "happy@test.local", ["openid", "api.read", "offline_access"]);

        var refreshed = await RefreshAsync(app, tokens.GetProperty("refresh_token").GetString()!);

        refreshed.StatusCode.Should().Be(HttpStatusCode.OK,
            "the refusals below are only meaningful if this succeeds");
    }

    /// <summary>
    /// W-S1 — **the seam test**. Withdraw through the page, then refresh through the token
    /// endpoint. Neither half is seeded; the document shape is precisely what the two are allowed
    /// to disagree about, so it is not asserted here at all.
    /// </summary>
    [Fact]
    public async Task Withdrawing_through_the_page_stops_the_refresh_token()
    {
        var app = await SeedRefreshableAppAsync("w-seam");
        await SeedUserAsync("seam@test.local");

        var (browser, tokens) = await EstablishGrantAsync(app, "seam@test.local", ["openid", "api.read", "offline_access"]);
        var refreshToken = tokens.GetProperty("refresh_token").GetString()!;

        await WithdrawAsync(browser, app.Id!);

        var refreshed = await RefreshAsync(app, refreshToken);

        refreshed.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "the user took the authority back; the client cannot keep minting on it");
        (await BodyAsync(refreshed)).GetProperty("error").GetString().Should().Be("invalid_grant");
    }

    /// <summary>W-S2 — introspection must stop vouching for tokens issued under a withdrawn grant.</summary>
    [Fact]
    public async Task Withdrawal_makes_the_access_token_inactive_to_introspection()
    {
        var app = await SeedRefreshableAppAsync("w-introspect");
        await SeedUserAsync("introspect@test.local");

        var (browser, tokens) = await EstablishGrantAsync(app, "introspect@test.local", ["openid", "api.read", "offline_access"]);
        var accessToken = tokens.GetProperty("access_token").GetString()!;

        async Task<bool> ActiveAsync() =>
            (await BodyAsync(await Client.PostAsync("/connect/introspect", new FormUrlEncodedContent(
                new Dictionary<string, string>
                {
                    ["client_id"] = app.ClientId,
                    ["client_secret"] = Secret,
                    ["token"] = accessToken,
                })))).GetProperty("active").GetBoolean();

        (await ActiveAsync()).Should().BeTrue("before withdrawal it is a live token");

        await WithdrawAsync(browser, app.Id!);

        (await ActiveAsync()).Should().BeFalse(
            "a resource server that asks us must not be told a withdrawn token is good — this is "
            + "the one window the architecture can actually close, since an offline validator cannot be reached");
    }

    /// <summary>
    /// W-R1 — the escalation. Re-authorizing after a withdrawal must not restore scopes the user
    /// did not just consent to.
    /// </summary>
    [Fact]
    public async Task Re_consenting_does_not_restore_the_old_scope_set()
    {
        var app = await SeedRefreshableAppAsync("w-rewiden");
        await SeedUserAsync("rewiden@test.local");

        var (browser, _) = await EstablishGrantAsync(app, "rewiden@test.local", ["openid", "api.read", "offline_access"]);
        await WithdrawAsync(browser, app.Id!);

        // The client comes back asking for the least it can.
        await ObtainCodeAsync(app, "rewiden@test.local", ["openid"]);

        using var session = Store.OpenAsyncSession();
        var grant = await session.LoadAsync<OidcAuthorization>(
            OidcAuthorizationReferenceProbe.DocumentId(await SubjectOfAsync("rewiden@test.local"), app.Id!));

        grant.GrantedScopes.Should().Equal(["openid"],
            "the merge only ever adds and the list was never reset, so reinstatement used to hand "
            + "back the full historical union — api.read included, thereafter auto-approved");
    }

    /// <summary>
    /// W-R2 — an implicit-consent client must not be able to resurrect a withdrawn grant silently.
    /// This is the branch that returned before the status check ever ran.
    /// </summary>
    [Fact]
    public async Task An_implicit_client_cannot_silently_resurrect_a_withdrawn_grant()
    {
        var app = await SeedRefreshableAppAsync("w-implicit", consentType: "implicit");
        await SeedUserAsync("implicit@test.local");

        var (browser, _) = await EstablishGrantAsync(app, "implicit@test.local", ["openid", "api.read", "offline_access"]);
        await WithdrawAsync(browser, app.Id!);

        var authorize = await browser.GetAsync(
            $"/connect/authorize?client_id={app.ClientId}"
            + $"&redirect_uri={Uri.EscapeDataString(app.RedirectUris[0])}&response_type=code&scope=openid");

        authorize.StatusCode.Should().Be(HttpStatusCode.Redirect);
        authorize.Headers.Location!.OriginalString.Should().StartWith("/connect/consent",
            "auto-approval asserts the user already trusts this client, which is exactly what a "
            + "withdrawal retracts — they have to be asked again");
    }

    /// <summary>W-C1 — a cross-site POST must not be able to withdraw anything.</summary>
    [Fact]
    public async Task Withdrawal_requires_an_antiforgery_token()
    {
        var app = await SeedRefreshableAppAsync("w-csrf");
        await SeedUserAsync("csrf@test.local");

        var (browser, tokens) = await EstablishGrantAsync(app, "csrf@test.local", ["openid", "api.read", "offline_access"]);

        var result = await WithdrawAsync(browser, app.Id!, includeAntiforgery: false);
        result.Should().Be("400");

        var refreshed = await RefreshAsync(app, tokens.GetProperty("refresh_token").GetString()!);
        refreshed.StatusCode.Should().Be(HttpStatusCode.OK,
            "the rejected request must not have withdrawn anything");
    }

    /// <summary>W-I1 — one user cannot withdraw another user's grant.</summary>
    [Fact]
    public async Task A_user_cannot_withdraw_someone_elses_grant()
    {
        var victimApp = await SeedRefreshableAppAsync("w-idor-victim");
        var attackerApp = await SeedRefreshableAppAsync("w-idor-attacker");
        await SeedUserAsync("victim@test.local");
        await SeedUserAsync("attacker@test.local");

        var (_, victimTokens) = await EstablishGrantAsync(victimApp, "victim@test.local", ["openid", "api.read", "offline_access"]);

        // The attacker holds a grant of their own, which is what gets them a page carrying a
        // valid antiforgery token — then they post the *victim's* application id with it.
        var (attacker, _) = await EstablishGrantAsync(attackerApp, "attacker@test.local", ["openid", "offline_access"]);
        WaitForIndexing(Store);
        await WithdrawAsync(attacker, victimApp.Id!);

        var app = victimApp;

        var refreshed = await RefreshAsync(app, victimTokens.GetProperty("refresh_token").GetString()!);

        refreshed.StatusCode.Should().Be(HttpStatusCode.OK,
            "the grant id is derived from the session's user, so the attacker's post can only ever "
            + "name their own grant — there is no parameter that could reach the victim's");
    }

    /// <summary>W-L1 — the list shows the caller's grants and nobody else's.</summary>
    [Fact]
    public async Task The_list_shows_only_the_signed_in_users_grants()
    {
        var mine = await SeedRefreshableAppAsync("w-list-mine");
        var theirs = await SeedRefreshableAppAsync("w-list-theirs");
        await SeedUserAsync("mine@test.local");
        await SeedUserAsync("theirs@test.local");

        await EstablishGrantAsync(mine, "mine@test.local", ["openid", "offline_access"]);
        await EstablishGrantAsync(theirs, "theirs@test.local", ["openid", "offline_access"]);

        WaitForIndexing(Store);

        var browser = await SignInAsync("mine@test.local");
        var html = await (await browser.GetAsync("/connect/applications")).Content.ReadAsStringAsync();

        html.Should().Contain("w-list-mine");
        html.Should().NotContain("w-list-theirs");
    }

    /// <summary>W-L2 — the page is not framable; every click on it is a security decision.</summary>
    [Fact]
    public async Task The_page_refuses_to_be_framed()
    {
        await SeedUserAsync("frame@test.local");
        var browser = await SignInAsync("frame@test.local");

        var response = await browser.GetAsync("/connect/applications");

        response.Headers.GetValues("Content-Security-Policy")
            .Should().ContainSingle().Which.Should().Contain("frame-ancestors 'none'");
    }

    /// <summary>
    /// W-W1 — withdrawing twice is not an error. The user asked for it gone; it is gone. Reporting
    /// failure the second time would send someone hunting for a problem that does not exist, and
    /// the page cannot distinguish a double submit from a genuine one.
    /// </summary>
    [Fact]
    public async Task Withdrawing_twice_reports_success_both_times()
    {
        var app = await SeedRefreshableAppAsync("w-idempotent");
        await SeedUserAsync("idempotent@test.local");

        var (browser, _) = await EstablishGrantAsync(app, "idempotent@test.local", ["openid", "offline_access"]);

        // Taken once, while a row still exists to render the form — and reused, which is exactly
        // what a double submit is.
        var token = await AntiforgeryFromApplicationsPageAsync(browser);
        var form = new Dictionary<string, string>
        {
            ["application_id"] = app.Id!,
            ["__RequestVerificationToken"] = token,
        };

        (await PostWithdrawalAsync(browser, form)).Should().EndWith("status=revoked");
        (await PostWithdrawalAsync(browser, form)).Should().EndWith("status=revoked");
    }

    /// <summary>
    /// W-W2 — a post naming no application at all is harmless. Worth pinning because the derived
    /// id is built from user + application, and an empty application id must not produce an id
    /// that happens to resolve to something.
    /// </summary>
    [Fact]
    public async Task A_post_with_no_application_changes_nothing()
    {
        var app = await SeedRefreshableAppAsync("w-empty");
        await SeedUserAsync("empty@test.local");

        var (browser, tokens) = await EstablishGrantAsync(app, "empty@test.local", ["openid", "offline_access"]);

        await browser.PostFormAsync("/connect/applications/revoke", new Dictionary<string, string>
        {
            ["application_id"] = "",
            ["__RequestVerificationToken"] = await AntiforgeryFromApplicationsPageAsync(browser),
        });

        var refreshed = await RefreshAsync(app, tokens.GetProperty("refresh_token").GetString()!);
        refreshed.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>W-A1 — a signed-out visitor is sent to log in, not shown an empty list.</summary>
    [Fact]
    public async Task An_anonymous_visitor_is_redirected_to_login()
    {
        var response = await NewBrowser().GetAsync("/connect/applications");

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.Should().StartWith("/connect/login");
    }

    private async Task<string> SubjectOfAsync(string email)
        => await WithUserManagerAsync(async users =>
        {
            var user = await users.FindByEmailAsync(email)
                ?? throw new InvalidOperationException($"No user '{email}'.");
            return user.Id;
        });
}

/// <summary>
/// Mirrors the package's internal grant-id derivation so a test can point-load the document.
/// Deliberately a copy rather than an <c>InternalsVisibleTo</c>: if the real derivation changes,
/// this stops matching and the tests fail loudly, which is the correct outcome — the id is
/// persisted, so changing it is a migration, not a refactor.
/// </summary>
internal static class OidcAuthorizationReferenceProbe
{
    public static string DocumentId(string subject, string applicationId)
    {
        var key = System.Text.Encoding.UTF8.GetBytes($"{subject.Length}:{subject}|{applicationId}");
        return "OidcAuthorizations/" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(key));
    }
}
