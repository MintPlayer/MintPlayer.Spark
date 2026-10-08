using System.Net;
using System.Net.Http.Json;
using System.Text;

namespace MintPlayer.Spark.Tests.IdentityProvider;

/// <summary>
/// The exact responses of the <c>/connect</c> and <c>/.well-known</c> endpoints on the paths no other
/// test pins byte for byte: status, <c>Content-Type</c>, <c>Location</c> and body.
/// </summary>
/// <remarks>
/// Written before M4 folded the static handlers into typed endpoint classes and run against both
/// versions: a handler that wrote its own response and an endpoint that returns an <c>IResult</c> can
/// differ in a header or a body byte without any behavioural test noticing. An OAuth client parses
/// these bodies, and a browser follows these redirects, so the shape is the contract.
/// </remarks>
public class OidcResponseShapeTests(OidcSharedHost host) : OidcTestHost(host), IClassFixture<OidcSharedHost>
{
    private const string Secret = "s3cret-value-for-tests";

    /// <summary>One line per response: status, content type, location and body.</summary>
    private static async Task<string> ShapeAsync(HttpResponseMessage response)
    {
        var sb = new StringBuilder();
        sb.Append((int)response.StatusCode);
        sb.Append(" | ").Append(response.Content.Headers.ContentType?.ToString() ?? "-");
        sb.Append(" | ").Append(response.Headers.Location?.OriginalString ?? "-");
        sb.Append(" | ").Append(await response.Content.ReadAsStringAsync());
        return sb.ToString();
    }

    /// <summary>An anonymous browser holding an antiforgery cookie, and the token that goes with it.</summary>
    private async Task<(Browser Browser, string Token)> AnonymousFormAsync()
    {
        var browser = NewBrowser();
        var page = await browser.GetAsync("/connect/login?returnUrl=%2F");
        return (browser, AntiforgeryTokenFrom(await page.Content.ReadAsStringAsync()));
    }

    // --- /connect/authorize ----------------------------------------------------------------------

    [Fact]
    public async Task Authorize_refusals_keep_their_shape()
    {
        var app = await SeedApplicationAsync(ClientId("webapp"));
        var redirect = Uri.EscapeDataString(app.RedirectUris[0]);

        (await ShapeAsync(await Client.GetAsync("/connect/authorize")))
            .Should().Be("""400 | application/json; charset=utf-8 | - | {"error":"invalid_request","error_description":"Missing required parameters."}""");

        (await ShapeAsync(await Client.GetAsync($"/connect/authorize?client_id={app.ClientId}&redirect_uri={redirect}&response_type=token&scope=openid")))
            .Should().StartWith($"302 | - | {app.RedirectUris[0]}?error=unsupported_response_type&");

        (await ShapeAsync(await Client.GetAsync($"/connect/authorize?client_id=nobody-{Scope}&redirect_uri={redirect}&response_type=code&scope=openid")))
            .Should().Be("""400 | application/json; charset=utf-8 | - | {"error":"invalid_client","error_description":"Unknown or disabled client."}""");

        (await ShapeAsync(await Client.GetAsync($"/connect/authorize?client_id={app.ClientId}&redirect_uri=https%3A%2F%2Felsewhere.test%2Fcb&response_type=code&scope=openid")))
            .Should().Be("""400 | application/json; charset=utf-8 | - | {"error":"invalid_request","error_description":"Invalid redirect_uri."}""");

        (await ShapeAsync(await Client.GetAsync($"/connect/authorize?client_id={app.ClientId}&redirect_uri={redirect}&response_type=code&scope=openid&code_challenge=abc&code_challenge_method=plain&state=s1")))
            .Should().StartWith($"302 | - | {app.RedirectUris[0]}?error=invalid_request&error_description=Only%20S256%20code_challenge_method%20is%20supported.&state=s1&iss=");

        // Valid, but nobody is signed in: the login hop carries the whole original query.
        var query = $"?client_id={app.ClientId}&redirect_uri={redirect}&response_type=code&scope=openid";
        (await ShapeAsync(await Client.GetAsync("/connect/authorize" + query)))
            .Should().Be($"302 | - | /connect/login?returnUrl={Uri.EscapeDataString("/connect/authorize" + query)} | ");
    }

    /// <summary>
    /// D6: the anti-framing headers are on a real <c>/connect/authorize</c> response, and on a path under
    /// <c>/connect</c> that no endpoint answers, which an endpoint filter would have missed.
    /// </summary>
    [Theory]
    [InlineData("/connect/authorize")]
    [InlineData("/connect/no-such-endpoint")]
    public async Task Connect_responses_refuse_to_be_framed(string path)
    {
        var response = await Client.GetAsync(path);

        response.Headers.GetValues("Content-Security-Policy").Should().ContainSingle().Which.Should().Contain("frame-ancestors 'none'");
        response.Headers.GetValues("X-Frame-Options").Should().ContainSingle().Which.Should().Be("DENY");
    }

    /// <summary>The prefix match is per segment: <c>/connectx</c> is not under <c>/connect</c>.</summary>
    [Fact]
    public async Task A_path_that_only_starts_with_the_prefix_is_not_stamped()
    {
        var response = await Client.GetAsync("/connectx/authorize");

        // Antiforgery may add its own SAMEORIGIN on any page that mints a token; DENY and the CSP are ours.
        response.Headers.Contains("Content-Security-Policy").Should().BeFalse();
        (response.Headers.TryGetValues("X-Frame-Options", out var xfo) ? xfo : []).Should().NotContain("DENY");
    }

    // --- /connect/consent ------------------------------------------------------------------------

    [Fact]
    public async Task Consent_refusals_keep_their_shape()
    {
        (await ShapeAsync(await Client.GetAsync("/connect/consent")))
            .Should().StartWith("400 | text/html; charset=utf-8 | - | <!DOCTYPE html>");

        (await ShapeAsync(await Client.GetAsync("/connect/consent?request_id=abc")))
            .Should().Be($"302 | - | /connect/login?returnUrl={Uri.EscapeDataString("/connect/consent?request_id=abc")} | ");

        var (browser, token) = await AnonymousFormAsync();

        (await ShapeAsync(await browser.PostFormAsync("/connect/consent", new Dictionary<string, string>
        {
            ["decision"] = "allow",
            ["__RequestVerificationToken"] = token,
        }))).Should().StartWith("400 | text/html; charset=utf-8 | - | <!DOCTYPE html>");

        (await ShapeAsync(await browser.PostFormAsync("/connect/consent", new Dictionary<string, string>
        {
            ["request_id"] = "abc",
            ["decision"] = "allow",
            ["__RequestVerificationToken"] = token,
        }))).Should().StartWith("401 | text/html; charset=utf-8 | - | <!DOCTYPE html>");

        await SeedUserAsync(UserEmail("consent"));
        var signedIn = await SignInAsync(UserEmail("consent"));
        (await ShapeAsync(await signedIn.GetAsync("/connect/consent?request_id=never-issued")))
            .Should().StartWith("400 | text/html; charset=utf-8 | - | <!DOCTYPE html>");
    }

    // --- /connect/applications -------------------------------------------------------------------

    [Fact]
    public async Task Connected_applications_refusals_keep_their_shape()
    {
        (await ShapeAsync(await Client.GetAsync("/connect/applications?status=revoked")))
            .Should().Be($"302 | - | /connect/login?returnUrl={Uri.EscapeDataString("/connect/applications?status=revoked")} | ");

        var (browser, token) = await AnonymousFormAsync();
        (await ShapeAsync(await browser.PostFormAsync("/connect/applications/revoke", new Dictionary<string, string>
        {
            ["application_id"] = "OidcApplications/1",
            ["__RequestVerificationToken"] = token,
        }))).Should().StartWith("401 | text/html; charset=utf-8 | - | <!DOCTYPE html>");

        await SeedUserAsync(UserEmail("apps"));
        var signedIn = await SignInAsync(UserEmail("apps"));
        var page = await signedIn.GetAsync("/connect/applications?status=revoked");
        var html = await page.Content.ReadAsStringAsync();

        ((int)page.StatusCode).Should().Be(200);
        page.Content.Headers.ContentType!.ToString().Should().Be("text/html; charset=utf-8");
        html.Should().Contain("<div class=\"notice\">Access removed.</div>");

        // A missing application id withdraws nothing and reports success, as before.
        var signedInToken = await FormTokenAsync(signedIn);
        (await ShapeAsync(await signedIn.PostFormAsync("/connect/applications/revoke", new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = signedInToken,
        }))).Should().Be("302 | - | /connect/applications?status=revoked | ");
    }

    /// <summary>A form token for a signed-in browser: the login page renders one for whoever asks.</summary>
    private static async Task<string> FormTokenAsync(Browser browser)
        => AntiforgeryTokenFrom(await (await browser.GetAsync("/connect/login?returnUrl=%2F")).Content.ReadAsStringAsync());

    // --- /connect/logout -------------------------------------------------------------------------

    [Fact]
    public async Task Logout_keeps_its_shape()
    {
        var app = await SeedApplicationAsync(ClientId("logout"), postLogoutRedirectUris: ["https://logout.test/bye"]);

        (await ShapeAsync(await Client.GetAsync("/connect/logout")))
            .Should().StartWith("200 | text/html; charset=utf-8 | - | <!DOCTYPE html>");

        (await ShapeAsync(await Client.GetAsync($"/connect/logout?post_logout_redirect_uri=https%3A%2F%2Fevil.test%2F&client_id={app.ClientId}")))
            .Should().StartWith("400 | text/html; charset=utf-8 | - | <!DOCTYPE html>");

        (await ShapeAsync(await Client.GetAsync($"/connect/logout?post_logout_redirect_uri=https%3A%2F%2Flogout.test%2Fbye&client_id={app.ClientId}&state=st")))
            .Should().Be("302 | - | https://logout.test/bye?state=st | ");
    }

    /// <summary>A signed-in user is signed out: afterwards the session no longer opens a signed-in page.</summary>
    [Fact]
    public async Task Logout_signs_the_user_out()
    {
        await SeedUserAsync(UserEmail("bye"));
        var browser = await SignInAsync(UserEmail("bye"));
        ((int)(await browser.GetAsync("/connect/applications")).StatusCode).Should().Be(200, "signed in before logout");

        var response = await browser.GetAsync("/connect/logout");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        ((int)(await browser.GetAsync("/connect/applications")).StatusCode).Should().Be(302, "signed out after logout");
    }

    // --- /connect/introspect and /connect/revoke -------------------------------------------------

    [Theory]
    [InlineData("/connect/introspect")]
    [InlineData("/connect/revoke")]
    public async Task Machine_endpoint_refusals_keep_their_shape(string path)
    {
        var app = await SeedApplicationAsync(ClientId("machine"));

        (await ShapeAsync(await Client.PostAsync(path, JsonContent.Create(new { token = "x" }))))
            .Should().Be("""400 | application/json; charset=utf-8 | - | {"error":"invalid_request"}""");

        (await ShapeAsync(await Client.PostAsync(path, null)))
            .Should().Be("""400 | application/json; charset=utf-8 | - | {"error":"invalid_request"}""");

        (await ShapeAsync(await Client.PostAsync(path, new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = "x" }))))
            .Should().Be("""401 | application/json; charset=utf-8 | - | {"error":"invalid_client","error_description":"Client authentication failed."}""");

        (await ShapeAsync(await Client.PostAsync(path, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["token"] = "x",
            ["client_id"] = app.ClientId,
            ["client_secret"] = "wrong",
        })))).Should().Be("""401 | application/json; charset=utf-8 | - | {"error":"invalid_client","error_description":"Client authentication failed."}""");

        var unknown = await Client.PostAsync(path, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["token"] = "never-issued",
            ["token_type_hint"] = "refresh_token",
            ["client_id"] = app.ClientId,
            ["client_secret"] = Secret,
        }));

        (await ShapeAsync(unknown)).Should().Be(path == "/connect/introspect"
            ? """200 | application/json; charset=utf-8 | - | {"active":false}"""
            : "200 | - | - | ");
    }

    // --- /connect/userinfo -----------------------------------------------------------------------

    [Fact]
    public async Task Userinfo_refusals_keep_their_shape()
    {
        var none = await Client.GetAsync("/connect/userinfo");
        (await ShapeAsync(none)).Should().Be("""401 | application/json; charset=utf-8 | - | {"error":"invalid_token"}""");
        none.Headers.WwwAuthenticate.ToString().Should().Be("Bearer, DPoP");

        var request = new HttpRequestMessage(HttpMethod.Get, "/connect/userinfo");
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer not-a-jwt");
        var garbage = await Client.SendAsync(request);
        (await ShapeAsync(garbage)).Should().Be("""401 | application/json; charset=utf-8 | - | {"error":"invalid_token"}""");
        garbage.Headers.WwwAuthenticate.ToString().Should().Contain("Bearer error=\"invalid_token\"");
    }

    // --- /connect/login and /connect/two-factor --------------------------------------------------

    [Fact]
    public async Task Login_and_two_factor_refusals_keep_their_shape()
    {
        var (browser, token) = await AnonymousFormAsync();

        (await ShapeAsync(await browser.PostFormAsync("/connect/login", new Dictionary<string, string>
        {
            ["identifier"] = "someone@test.local",
            ["returnUrl"] = "/after",
            ["__RequestVerificationToken"] = token,
        }))).Should().Be("302 | - | /connect/login?returnUrl=%2Fafter&error=missing_fields | ");

        (await ShapeAsync(await browser.PostFormAsync("/connect/login", new Dictionary<string, string>
        {
            ["email"] = $"nobody-{Scope}@test.local",
            ["password"] = "wrong",
            ["returnUrl"] = "https://evil.test/",
            ["__RequestVerificationToken"] = token,
        }))).Should().Be("302 | - | /connect/login?returnUrl=%2F&error=invalid_credentials | ");

        (await ShapeAsync(await browser.PostFormAsync("/connect/two-factor", new Dictionary<string, string>
        {
            ["returnUrl"] = "/after",
            ["__RequestVerificationToken"] = token,
        }))).Should().Be("302 | - | /connect/two-factor?returnUrl=%2Fafter&error=missing_code | ");

        (await ShapeAsync(await browser.PostFormAsync("/connect/two-factor", new Dictionary<string, string>
        {
            ["useRecoveryCode"] = "true",
            ["returnUrl"] = "/after",
            ["__RequestVerificationToken"] = token,
        }))).Should().Be("302 | - | /connect/two-factor?returnUrl=%2Fafter&error=missing_recovery_code&recovery=true | ");

        // No partial sign-in behind the code: the sign-in manager refuses it.
        (await ShapeAsync(await browser.PostFormAsync("/connect/two-factor", new Dictionary<string, string>
        {
            ["code"] = "123 456",
            ["returnUrl"] = "/after",
            ["__RequestVerificationToken"] = token,
        }))).Should().Be("302 | - | /connect/two-factor?returnUrl=%2Fafter&error=invalid_code | ");
    }

    [Fact]
    public async Task The_pages_render_as_html()
    {
        foreach (var url in new[]
        {
            "/connect/login?returnUrl=%2F&error=locked_out",
            "/connect/two-factor?returnUrl=%2F&recovery=true&rememberMe=true&error=invalid_recovery_code",
            "/connect/two-factor?returnUrl=%2F",
        })
        {
            var response = await Client.GetAsync(url);
            ((int)response.StatusCode).Should().Be(200, url);
            response.Content.Headers.ContentType!.ToString().Should().Be("text/html; charset=utf-8", url);
        }

        var recovery = await (await Client.GetAsync("/connect/two-factor?returnUrl=%2Fnext&recovery=true&rememberMe=true")).Content.ReadAsStringAsync();
        recovery.Should().Contain("name=\"useRecoveryCode\" value=\"true\"");
        recovery.Should().Contain("name=\"rememberMe\" value=\"true\"");
        recovery.Should().Contain("href=\"/connect/two-factor?returnUrl=%2Fnext&rememberMe=true\"");

        var login = await (await Client.GetAsync("/connect/login?returnUrl=%2Fnext&error=locked_out")).Content.ReadAsStringAsync();
        login.Should().Contain("Account is locked out. Please try again later.");
        login.Should().Contain("name=\"returnUrl\" value=\"/next\"");
    }

    // --- /.well-known ----------------------------------------------------------------------------

    [Fact]
    public async Task Discovery_and_jwks_keep_their_shape()
    {
        var discovery = await Client.GetAsync("/.well-known/openid-configuration");
        ((int)discovery.StatusCode).Should().Be(200);
        discovery.Content.Headers.ContentType!.ToString().Should().Be("application/json; charset=utf-8");
        (await discovery.Content.ReadAsStringAsync()).Should().StartWith(
            """{"issuer":"https://idp.test","authorization_endpoint":"https://idp.test/connect/authorize","token_endpoint":"https://idp.test/connect/token",""");

        var jwks = await Client.GetAsync("/.well-known/jwks");
        ((int)jwks.StatusCode).Should().Be(200);
        jwks.Content.Headers.ContentType!.ToString().Should().Be("application/json; charset=utf-8");
        (await jwks.Content.ReadAsStringAsync()).Should().StartWith("""{"keys":[{"kty":"RSA","use":"sig","kid":""");
    }
}
