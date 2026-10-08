using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.IdentityModel.JsonWebTokens;
using MintPlayer.Spark.IdentityProvider.Models;

namespace MintPlayer.Spark.Tests.IdentityProvider;

/// <summary>
/// <c>OidcAuthorizeHandler</c> (I8, PRD D8): what <c>/connect/authorize</c> decides once a person may
/// or may not be signed in: <c>prompt</c>, <c>max_age</c>, the <c>spark_reauth</c> marker that makes a
/// forced re-authentication happen once, <c>iss</c> on every response (RFC 9207), <c>form_post</c>, and
/// the parameters it refuses. The checks that need no user are in <see cref="OidcAuthorizeSecurityTests"/>.
/// </summary>
public class OidcAuthorizeHandlerTests(OidcSharedHost host) : OidcTestHost(host), IClassFixture<OidcSharedHost>
{
    private const string Secret = "s3cret-value-for-tests";
    private const string State = "state-abc";

    /// <summary>An implicit-consent client, so a signed-in request goes straight to the code.</summary>
    private Task<OidcApplication> SeedImplicitAppAsync()
        => SeedApplicationAsync(ClientId("webapp"), consentType: "implicit");

    private static string Url(OidcApplication app, string extra = "", string scope = "openid", string responseType = "code")
        => $"/connect/authorize?client_id={Uri.EscapeDataString(app.ClientId)}"
         + $"&redirect_uri={Uri.EscapeDataString(app.RedirectUris[0])}"
         + $"&response_type={Uri.EscapeDataString(responseType)}"
         + $"&scope={Uri.EscapeDataString(scope)}&state={State}"
         + extra;

    /// <summary>The query of a redirect to the client's redirect URI.</summary>
    private static System.Collections.Specialized.NameValueCollection ClientRedirect(HttpResponseMessage response, OidcApplication app)
    {
        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var location = response.Headers.Location!.OriginalString;
        location.Should().StartWith(app.RedirectUris[0], "the answer goes back to the client");
        return System.Web.HttpUtility.ParseQueryString(new Uri(location).Query);
    }

    /// <summary>The returnUrl the sign-in redirect carries back to <c>/connect/authorize</c>.</summary>
    private static string ReturnUrlOfLoginRedirect(HttpResponseMessage response)
    {
        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var location = response.Headers.Location!.OriginalString;
        location.Should().StartWith("/connect/login");
        var returnUrl = System.Web.HttpUtility.ParseQueryString(location[location.IndexOf('?')..])["returnUrl"];
        returnUrl.Should().NotBeNull();
        return returnUrl!;
    }

    // ---------- prompt=none ----------

    [Fact]
    public async Task Prompt_none_without_a_session_answers_login_required_to_the_client()
    {
        var app = await SeedImplicitAppAsync();

        var query = ClientRedirect(await Client.GetAsync(Url(app, "&prompt=none")), app);

        query["error"].Should().Be("login_required", "OIDC Core §3.1.2.6: prompt=none must never show a page");
        query["state"].Should().Be(State);
        query["iss"].Should().Be(Issuer, "RFC 9207: error responses carry iss too");
        query["code"].Should().BeNull();
    }

    [Fact]
    public async Task Prompt_none_combined_with_another_value_is_invalid_request()
    {
        var app = await SeedImplicitAppAsync();

        var query = ClientRedirect(await Client.GetAsync(Url(app, "&prompt=" + Uri.EscapeDataString("none login"))), app);

        query["error"].Should().Be("invalid_request");
    }

    [Fact]
    public async Task An_unknown_prompt_value_is_invalid_request()
    {
        var app = await SeedImplicitAppAsync();

        var query = ClientRedirect(await Client.GetAsync(Url(app, "&prompt=bogus")), app);

        query["error"].Should().Be("invalid_request");
    }

    [Fact]
    public async Task Prompt_none_with_a_session_and_implicit_consent_issues_a_code()
    {
        var app = await SeedImplicitAppAsync();
        await SeedUserAsync(UserEmail("alice"));
        var browser = await SignInAsync(UserEmail("alice"));

        var query = ClientRedirect(await browser.GetAsync(Url(app, "&prompt=none")), app);

        query["code"].Should().NotBeNullOrEmpty();
        query["error"].Should().BeNull();
    }

    // ---------- prompt=login and the spark_reauth marker ----------

    [Fact]
    public async Task Prompt_login_forces_one_fresh_sign_in_and_then_proceeds()
    {
        var app = await SeedImplicitAppAsync();
        await SeedUserAsync(UserEmail("alice"));
        var browser = await SignInAsync(UserEmail("alice"));

        var first = await browser.GetAsync(Url(app, "&prompt=login"));
        var returnUrl = ReturnUrlOfLoginRedirect(first);

        returnUrl.Should().StartWith("/connect/authorize");
        returnUrl.Should().Contain("spark_reauth=1", "the way back is marked so it is not forced again");
        returnUrl.Should().NotContain("prompt=login", "otherwise the return would ask for a sign-in again, forever");

        // Coming back (here: with the same session, as if the person had signed in again).
        var query = ClientRedirect(await browser.GetAsync(returnUrl), app);
        query["code"].Should().NotBeNullOrEmpty("the forced sign-in happens once, then the request proceeds");
        query["state"].Should().Be(State);
    }

    [Fact]
    public async Task Without_a_session_the_sign_in_redirect_is_not_marked_as_a_reauthentication()
    {
        var app = await SeedImplicitAppAsync();

        var returnUrl = ReturnUrlOfLoginRedirect(await Client.GetAsync(Url(app)));

        returnUrl.Should().NotContain("spark_reauth", "only a forced re-authentication marks its return");
    }

    // ---------- max_age ----------

    [Fact]
    public async Task Max_age_satisfied_by_a_fresh_sign_in_issues_a_code_whose_id_token_carries_auth_time()
    {
        var app = await SeedImplicitAppAsync();
        await SeedUserAsync(UserEmail("alice"));
        var browser = await SignInAsync(UserEmail("alice"));

        var query = ClientRedirect(await browser.GetAsync(Url(app, "&max_age=300")), app);
        var code = query["code"];
        code.Should().NotBeNullOrEmpty("a sign-in seconds old satisfies max_age=300 without another prompt");

        var token = JsonDocument.Parse(await (await Client.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = app.ClientId,
            ["client_secret"] = Secret,
            ["code"] = code!,
            ["redirect_uri"] = app.RedirectUris[0],
        }))).Content.ReadAsStringAsync()).RootElement;

        var idToken = new JsonWebToken(token.GetProperty("id_token").GetString());
        idToken.TryGetPayloadValue<long>("auth_time", out var authTime).Should().BeTrue(
            "OIDC Core §3.1.2.1: when max_age is requested the id_token MUST carry auth_time");
        DateTimeOffset.FromUnixTimeSeconds(authTime).UtcDateTime.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(2));
    }

    // ---------- iss and response modes ----------

    [Fact]
    public async Task A_successful_response_carries_iss()
    {
        var app = await SeedImplicitAppAsync();
        await SeedUserAsync(UserEmail("alice"));
        var browser = await SignInAsync(UserEmail("alice"));

        var query = ClientRedirect(await browser.GetAsync(Url(app)), app);

        query["iss"].Should().Be(Issuer, "RFC 9207: a client talking to several providers can tell which one answered");
        query["code"].Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Form_post_delivers_the_code_and_iss_as_a_self_submitting_form()
    {
        var app = await SeedImplicitAppAsync();
        await SeedUserAsync(UserEmail("alice"));
        var browser = await SignInAsync(UserEmail("alice"));

        var response = await browser.GetAsync(Url(app, "&response_mode=form_post"));

        response.StatusCode.Should().Be(HttpStatusCode.OK, "form_post answers with a page, not a redirect");
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
        html.Should().Contain($"action=\"{app.RedirectUris[0]}\"");
        Hidden(html, "code").Should().NotBeNullOrEmpty();
        Hidden(html, "state").Should().Be(State);
        Hidden(html, "iss").Should().Be(Issuer);
    }

    [Fact]
    public async Task Form_post_delivers_errors_as_a_form_too()
    {
        var app = await SeedImplicitAppAsync();

        var response = await Client.GetAsync(Url(app, "&response_mode=form_post&prompt=none"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
        Hidden(html, "error").Should().Be("login_required");
        Hidden(html, "iss").Should().Be(Issuer);
    }

    [Fact]
    public async Task An_unsupported_response_mode_is_refused_back_to_the_client()
    {
        var app = await SeedImplicitAppAsync();

        var query = ClientRedirect(await Client.GetAsync(Url(app, "&response_mode=fragment")), app);

        query["error"].Should().Be("invalid_request");
        query["iss"].Should().Be(Issuer);
    }

    [Fact]
    public async Task A_response_type_other_than_code_is_unsupported_response_type()
    {
        var app = await SeedImplicitAppAsync();

        var query = ClientRedirect(await Client.GetAsync(Url(app, responseType: "token")), app);

        query["error"].Should().Be("unsupported_response_type", "the implicit flow is not offered");
    }

    [Fact]
    public async Task A_plain_pkce_method_is_refused()
    {
        var app = await SeedImplicitAppAsync();

        var query = ClientRedirect(await Client.GetAsync(Url(app, "&code_challenge=abc&code_challenge_method=plain")), app);

        query["error"].Should().Be("invalid_request", "only S256 is supported");
    }

    [Fact]
    public async Task A_claims_parameter_that_is_not_a_json_object_is_invalid_request()
    {
        var app = await SeedImplicitAppAsync();

        var query = ClientRedirect(await Client.GetAsync(Url(app, "&claims=" + Uri.EscapeDataString("not json"))), app);

        query["error"].Should().Be("invalid_request");
    }

    [Fact]
    public async Task A_resource_indicator_naming_no_requested_api_is_invalid_target()
    {
        var app = await SeedImplicitAppAsync();

        var query = ClientRedirect(await Client.GetAsync(Url(app, "&resource=" + Uri.EscapeDataString("https://unrelated.test"))), app);

        query["error"].Should().Be("invalid_target", "RFC 8707: a resource must name an API the request's scopes belong to");
    }

    [Fact]
    public async Task The_authorize_endpoint_accepts_post()
    {
        var app = await SeedImplicitAppAsync();
        await SeedUserAsync(UserEmail("alice"));
        var browser = await SignInAsync(UserEmail("alice"));

        var response = await browser.PostFormAsync("/connect/authorize", new Dictionary<string, string>
        {
            ["client_id"] = app.ClientId,
            ["redirect_uri"] = app.RedirectUris[0],
            ["response_type"] = "code",
            ["scope"] = "openid",
            ["state"] = State,
        });

        ClientRedirect(response, app)["code"].Should().NotBeNullOrEmpty("OIDC Core §3.1.2.1: GET and POST MUST both be supported");
    }

    private static string? Hidden(string html, string name)
    {
        var match = Regex.Match(html, $"<input type=\"hidden\" name=\"{Regex.Escape(name)}\" value=\"([^\"]*)\"");
        return match.Success ? match.Groups[1].Value : null;
    }
}
