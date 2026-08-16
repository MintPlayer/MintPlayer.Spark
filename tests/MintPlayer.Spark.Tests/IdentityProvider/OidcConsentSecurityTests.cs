using MintPlayer.Spark.Testing;
using System.Net;
using MintPlayer.Spark.IdentityProvider.Models;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Tests.IdentityProvider;

/// <summary>
/// The consent hop — the surface that produced this package's worst finding (F6, one-click
/// account takeover) and the refactor that closed it. Case ids refer to §A of the matrix.
/// <para>
/// Every case here exercises a real signed-in session, because the whole point of the hop is
/// what it does with a browser that <em>is</em> authenticated.
/// </para>
/// </summary>
public class OidcConsentSecurityTests : OidcTestHost
{
    private const string ClientId = "webapp";
    private const string RedirectUri = "https://webapp.test/cb";

    private async Task<(OidcApplication App, Browser Browser, string RequestId)> StartFlowAsync(
        string email = "alice@test.local",
        string[]? scopes = null)
    {
        var app = await SeedApplicationAsync(ClientId, allowedScopes: ["openid", "profile"]);
        await SeedUserAsync(email);
        var browser = await SignInAsync(email);

        var authorizeUrl =
            $"/connect/authorize?client_id={ClientId}&redirect_uri={Uri.EscapeDataString(RedirectUri)}"
          + $"&response_type=code&scope={Uri.EscapeDataString(string.Join(' ', scopes ?? ["openid", "profile"]))}&state=st-1";

        var response = await browser.GetAsync(authorizeUrl);
        response.StatusCode.Should().Be(HttpStatusCode.Redirect);

        var location = response.Headers.Location!.OriginalString;
        location.Should().StartWith("/connect/consent?request_id=");

        return (app, browser, location["/connect/consent?request_id=".Length..]);
    }

    private static async Task<string> ConsentTokenAsync(Browser browser, string requestId)
        => AntiforgeryTokenFrom(await (await browser.GetAsync($"/connect/consent?request_id={requestId}")).Content.ReadAsStringAsync());

    // ---------- must succeed ----------

    /// <summary>A-H1/A-H2/A-H3 — the whole happy path, end to end.</summary>
    [Fact]
    public async Task Consent_allow_mints_a_code_for_the_registered_redirect_uri()
    {
        var (_, browser, requestId) = await StartFlowAsync();
        var token = await ConsentTokenAsync(browser, requestId);

        var response = await browser.PostFormAsync("/connect/consent", new Dictionary<string, string>
        {
            ["request_id"] = requestId,
            ["decision"] = "allow",
            ["scopes"] = "openid",
            ["__RequestVerificationToken"] = token,
        });

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var location = response.Headers.Location!.OriginalString;
        location.Should().StartWith(RedirectUri + "?code=");
        location.Should().Contain("state=st-1", "the client's CSRF defence depends on state coming back");

        await Store.WaitForIndexingAsync();
        using var session = Store.OpenAsyncSession();
        var codes = await session.Query<OidcToken>().ToListAsync();
        codes.Should().ContainSingle().Which.Type.Should().Be("authorization_code");
    }

    /// <summary>A-H2 — the rendered form carries the handle and nothing else worth tampering with.</summary>
    [Fact]
    public async Task Consent_page_exposes_no_security_parameters_to_the_browser()
    {
        var (_, browser, requestId) = await StartFlowAsync();

        var html = await (await browser.GetAsync($"/connect/consent?request_id={requestId}")).Content.ReadAsStringAsync();

        html.Should().NotContain("redirect_uri", "the destination must not round-trip through the user agent");
        html.Should().NotContain("code_challenge");
        html.Should().NotContain("client_id");
        html.Should().Contain("request_id");
    }

    /// <summary>A-H8 — denial goes to the stored destination, and mints nothing.</summary>
    [Fact]
    public async Task Consent_deny_redirects_with_access_denied_and_no_code()
    {
        var (_, browser, requestId) = await StartFlowAsync();
        var token = await ConsentTokenAsync(browser, requestId);

        var response = await browser.PostFormAsync("/connect/consent", new Dictionary<string, string>
        {
            ["request_id"] = requestId,
            ["decision"] = "deny",
            ["__RequestVerificationToken"] = token,
        });

        response.Headers.Location!.OriginalString.Should().StartWith(RedirectUri + "?error=access_denied");
        await AssertNoCodeAsync();
    }

    // ---------- must fail ----------

    /// <summary>
    /// A-I5 — the hijack the subject binding exists to close. Bob must not be able to consent
    /// on a request issued to Alice, even though Bob is perfectly well signed in.
    /// </summary>
    [Fact]
    public async Task Consent_rejects_a_request_id_issued_to_a_different_user()
    {
        var (_, alice, requestId) = await StartFlowAsync("alice@test.local");
        var aliceToken = await ConsentTokenAsync(alice, requestId);

        await SeedUserAsync("bob@test.local");
        var bob = await SignInAsync("bob@test.local");

        var get = await bob.GetAsync($"/connect/consent?request_id={requestId}");
        get.StatusCode.Should().Be(HttpStatusCode.BadRequest, "the GET must not even render Alice's request to Bob");

        var post = await bob.PostFormAsync("/connect/consent", new Dictionary<string, string>
        {
            ["request_id"] = requestId,
            ["decision"] = "allow",
            ["scopes"] = "openid",
            ["__RequestVerificationToken"] = aliceToken,
        });

        post.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await AssertNoCodeAsync();
    }

    /// <summary>A-I3 — a handle mints exactly one code.</summary>
    [Fact]
    public async Task Consent_rejects_a_replayed_request_id()
    {
        var (_, browser, requestId) = await StartFlowAsync();
        var token = await ConsentTokenAsync(browser, requestId);

        var form = new Dictionary<string, string>
        {
            ["request_id"] = requestId,
            ["decision"] = "allow",
            ["scopes"] = "openid",
            ["__RequestVerificationToken"] = token,
        };

        (await browser.PostFormAsync("/connect/consent", form)).StatusCode.Should().Be(HttpStatusCode.Redirect);
        var replay = await browser.PostFormAsync("/connect/consent", form);

        replay.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        await Store.WaitForIndexingAsync();
        using var session = Store.OpenAsyncSession();
        (await session.Query<OidcToken>().ToListAsync()).Should().ContainSingle("a replay must not mint a second code");
    }

    /// <summary>A-I4 — denial is terminal; it cannot be retried into an allow.</summary>
    [Fact]
    public async Task Consent_rejects_an_allow_after_a_deny()
    {
        var (_, browser, requestId) = await StartFlowAsync();
        var token = await ConsentTokenAsync(browser, requestId);

        await browser.PostFormAsync("/connect/consent", new Dictionary<string, string>
        {
            ["request_id"] = requestId, ["decision"] = "deny", ["__RequestVerificationToken"] = token,
        });

        var retry = await browser.PostFormAsync("/connect/consent", new Dictionary<string, string>
        {
            ["request_id"] = requestId, ["decision"] = "allow", ["scopes"] = "openid",
            ["__RequestVerificationToken"] = token,
        });

        retry.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await AssertNoCodeAsync();
    }

    /// <summary>A-I2 — the ten-minute bound is enforced.</summary>
    [Fact]
    public async Task Consent_rejects_an_expired_request()
    {
        var app = await SeedApplicationAsync(ClientId);
        var user = await SeedUserAsync("alice@test.local");
        var browser = await SignInAsync("alice@test.local");

        var requestId = await SeedAuthorizationRequestAsync(
            app, user.Id!, expiresAt: DateTime.UtcNow.AddMinutes(-1));

        var response = await browser.GetAsync($"/connect/consent?request_id={requestId}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    /// <summary>A-I1 — an unknown handle.</summary>
    [Fact]
    public async Task Consent_rejects_an_unknown_request_id()
    {
        await SeedApplicationAsync(ClientId);
        await SeedUserAsync("alice@test.local");
        var browser = await SignInAsync("alice@test.local");

        var response = await browser.GetAsync("/connect/consent?request_id=never-issued-this");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    /// <summary>
    /// A-R9 — the structural fix. An injected redirect_uri on the POST must be inert: the code
    /// goes to the destination stored at authorize time, not the one in the form.
    /// </summary>
    [Fact]
    public async Task Consent_ignores_a_redirect_uri_injected_into_the_post()
    {
        var (_, browser, requestId) = await StartFlowAsync();
        var token = await ConsentTokenAsync(browser, requestId);

        var response = await browser.PostFormAsync("/connect/consent", new Dictionary<string, string>
        {
            ["request_id"] = requestId,
            ["decision"] = "allow",
            ["scopes"] = "openid",
            ["redirect_uri"] = "https://evil.example.com/steal",
            ["client_id"] = "some-other-client",
            ["code_challenge"] = "attacker-supplied",
            ["__RequestVerificationToken"] = token,
        });

        response.Headers.Location!.OriginalString.Should().StartWith(RedirectUri,
            "these fields are not read at all — the hop carries only the handle");
    }

    /// <summary>A-S5 — a crafted POST cannot grant a scope the request never carried.</summary>
    [Fact]
    public async Task Consent_drops_scopes_that_were_never_requested()
    {
        var app = await SeedApplicationAsync(ClientId, allowedScopes: ["openid", "profile", "admin"]);
        var user = await SeedUserAsync("alice@test.local");
        var browser = await SignInAsync("alice@test.local");

        // Requested openid only, even though 'admin' is allowed for this client.
        var requestId = await SeedAuthorizationRequestAsync(app, user.Id!, scopes: ["openid"]);
        var token = await ConsentTokenAsync(browser, requestId);

        await browser.PostFormAsync("/connect/consent", new Dictionary<string, string>
        {
            ["request_id"] = requestId,
            ["decision"] = "allow",
            ["scopes"] = "admin",
            ["__RequestVerificationToken"] = token,
        });

        await Store.WaitForIndexingAsync();
        using var session = Store.OpenAsyncSession();
        var code = (await session.Query<OidcToken>().ToListAsync()).SingleOrDefault();
        code?.Scopes.Should().NotContain("admin", "the grant is bounded by what authorize validated");
    }

    /// <summary>A-F1 — the CSRF gate. Same request, token withheld.</summary>
    [Fact]
    public async Task Consent_post_is_rejected_without_an_antiforgery_token()
    {
        var (_, browser, requestId) = await StartFlowAsync();
        await ConsentTokenAsync(browser, requestId);

        var response = await browser.PostFormAsync("/connect/consent", new Dictionary<string, string>
        {
            ["request_id"] = requestId,
            ["decision"] = "allow",
            ["scopes"] = "openid",
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await AssertNoCodeAsync();
    }

    /// <summary>A-U3 — an unauthenticated POST is refused outright, not redirected.</summary>
    [Fact]
    public async Task Consent_post_without_a_session_returns_401()
    {
        var app = await SeedApplicationAsync(ClientId);
        var user = await SeedUserAsync("alice@test.local");
        var requestId = await SeedAuthorizationRequestAsync(app, user.Id!);

        var anonymous = NewBrowser();
        var response = await anonymous.PostFormAsync("/connect/consent", new Dictionary<string, string>
        {
            ["request_id"] = requestId, ["decision"] = "allow", ["scopes"] = "openid",
        });

        // Either the antiforgery gate or the authentication check may answer first; what must
        // not happen is a code being minted.
        response.StatusCode.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.BadRequest);
        await AssertNoCodeAsync();
    }

    private async Task AssertNoCodeAsync()
    {
        // The wait matters most here, and not for flakiness. This asserts *absence*: a stale index
        // returns nothing, so without it the assertion passes whether or not a code was minted —
        // a security check that succeeds for the wrong reason and would never be noticed.
        await Store.WaitForIndexingAsync();
        using var session = Store.OpenAsyncSession();
        var tokens = await session.Query<OidcToken>().ToListAsync();
        tokens.Should().BeEmpty("no authorization code may exist");
    }
}
