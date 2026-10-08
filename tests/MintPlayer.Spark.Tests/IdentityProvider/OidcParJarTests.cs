using System.Net;
using System.Text.Json;
using MintPlayer.Spark.IdentityProvider.Models;
using MintPlayer.Spark.IdentityProvider.Services;

namespace MintPlayer.Spark.Tests.IdentityProvider;

/// <summary>
/// Pushed authorization requests (<c>/connect/par</c>, RFC 9126) and signed request objects
/// (<c>OidcRequestObjects</c>, RFC 9101), I9 / PRD D8. Two implementation decisions are pinned here:
/// a pushed <c>request_uri</c> is reusable until it expires (90 s), because the bounce through sign-in
/// redeems it again; and JAR by reference (a non-PAR <c>request_uri</c>) is refused.
/// </summary>
public class OidcParJarTests(OidcSharedHost host) : OidcTestHost(host), IClassFixture<OidcSharedHost>
{
    private const string Secret = "s3cret-value-for-tests";
    private const string State = "state-par";

    private Task<OidcApplication> SeedImplicitAppAsync(string name = "webapp")
        => SeedApplicationAsync(ClientId(name), consentType: "implicit");

    private async Task UpdateAppAsync(OidcApplication app, Action<OidcApplication> change)
        => await SeedAsync(async session => change((await session.LoadAsync<OidcApplication>(app.Id))!));

    private Task<HttpResponseMessage> PushAsync(OidcApplication app, Dictionary<string, string>? parameters = null, string secret = Secret)
    {
        var form = parameters ?? new Dictionary<string, string>
        {
            ["redirect_uri"] = app.RedirectUris[0],
            ["response_type"] = "code",
            ["scope"] = "openid",
            ["state"] = State,
        };
        form["client_id"] = app.ClientId;
        form["client_secret"] = secret;
        return Client.PostAsync("/connect/par", new FormUrlEncodedContent(form));
    }

    private async Task<string> PushedRequestUriAsync(OidcApplication app)
    {
        var response = await PushAsync(app);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await BodyAsync(response)).GetProperty("request_uri").GetString()!;
    }

    private static string RedeemUrl(OidcApplication app, string requestUri)
        => $"/connect/authorize?client_id={Uri.EscapeDataString(app.ClientId)}&request_uri={Uri.EscapeDataString(requestUri)}";

    private static string JarUrl(OidcApplication app, string requestObject)
        => $"/connect/authorize?client_id={Uri.EscapeDataString(app.ClientId)}&request={Uri.EscapeDataString(requestObject)}";

    private async Task<Browser> SignedInAsync()
    {
        await SeedUserAsync(UserEmail("alice"));
        return await SignInAsync(UserEmail("alice"));
    }

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    private static System.Collections.Specialized.NameValueCollection ClientRedirect(HttpResponseMessage response, OidcApplication app)
    {
        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var location = response.Headers.Location!.OriginalString;
        location.Should().StartWith(app.RedirectUris[0]);
        return System.Web.HttpUtility.ParseQueryString(new Uri(location).Query);
    }

    /// <summary>A request object for <paramref name="app"/>: iss = client, aud = this provider.</summary>
    private static Dictionary<string, object> JarClaims(OidcApplication app, string? jti = null) => new()
    {
        ["client_id"] = app.ClientId,
        ["redirect_uri"] = app.RedirectUris[0],
        ["response_type"] = "code",
        ["scope"] = "openid",
        ["state"] = State,
        ["jti"] = jti ?? Guid.NewGuid().ToString("N"),
    };

    private async Task<(OidcApplication App, OidcTestClientKey Key)> SeedJarAppAsync(bool requireSigned = false)
    {
        var key = new OidcTestClientKey();
        var app = await SeedImplicitAppAsync("jar-app");
        await UpdateAppAsync(app, a => { a.Jwks = key.JwksJson; a.RequireSignedRequestObject = requireSigned; });
        app.Jwks = key.JwksJson;
        app.RequireSignedRequestObject = requireSigned;
        return (app, key);
    }

    // ---------- PAR ----------

    [Fact]
    public async Task Push_answers_a_request_uri_valid_for_90_seconds_and_no_store()
    {
        var app = await SeedImplicitAppAsync();

        var response = await PushAsync(app);

        response.StatusCode.Should().Be(HttpStatusCode.Created, "RFC 9126 §2.2");
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        var body = await BodyAsync(response);
        body.GetProperty("request_uri").GetString().Should().StartWith(OidcAuthorizeParameters.PushedRequestUriPrefix);
        body.GetProperty("expires_in").GetInt32().Should().Be(90);
    }

    [Fact]
    public async Task Push_requires_client_authentication()
    {
        var app = await SeedImplicitAppAsync();

        (await PushAsync(app, secret: "wrong")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Push_refuses_an_unregistered_redirect_uri_and_a_nested_request_uri()
    {
        var app = await SeedImplicitAppAsync();

        var badRedirect = await PushAsync(app, new Dictionary<string, string>
        {
            ["redirect_uri"] = "https://attacker.test/cb", ["response_type"] = "code", ["scope"] = "openid",
        });
        badRedirect.StatusCode.Should().Be(HttpStatusCode.BadRequest, "a bad request fails at the push, not in the user's browser");
        (await BodyAsync(badRedirect)).GetProperty("error").GetString().Should().Be("invalid_request");

        var nested = await PushAsync(app, new Dictionary<string, string>
        {
            ["redirect_uri"] = app.RedirectUris[0], ["response_type"] = "code", ["scope"] = "openid",
            ["request_uri"] = OidcAuthorizeParameters.PushedRequestUriPrefix + "abc",
        });
        nested.StatusCode.Should().Be(HttpStatusCode.BadRequest, "RFC 9126 §2.1: request_uri cannot itself be pushed");
    }

    [Fact]
    public async Task A_pushed_request_is_redeemed_and_reusable_until_it_expires()
    {
        var app = await SeedImplicitAppAsync();
        var browser = await SignedInAsync();
        var requestUri = await PushedRequestUriAsync(app);

        var first = ClientRedirect(await browser.GetAsync(RedeemUrl(app, requestUri)), app);
        first["code"].Should().NotBeNullOrEmpty();
        first["state"].Should().Be(State, "the pushed parameters are what the request carries");

        var second = ClientRedirect(await browser.GetAsync(RedeemUrl(app, requestUri)), app);
        second["code"].Should().NotBeNullOrEmpty(
            "decision: reusable until expiry, because the bounce through sign-in redeems the same request_uri again");
    }

    [Fact]
    public async Task The_sign_in_bounce_carries_only_the_request_uri()
    {
        var app = await SeedImplicitAppAsync();
        var requestUri = await PushedRequestUriAsync(app);

        var response = await Client.GetAsync(RedeemUrl(app, requestUri));

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var returnUrl = System.Web.HttpUtility.ParseQueryString(new Uri("https://x" + response.Headers.Location!.OriginalString).Query)["returnUrl"]!;
        returnUrl.Should().Contain("request_uri=");
        returnUrl.Should().NotContain("redirect_uri", "RFC 9126 §4: nothing the client pushed is exposed in the browser");
        returnUrl.Should().NotContain(State);
    }

    [Fact]
    public async Task An_expired_pushed_request_is_refused()
    {
        var app = await SeedImplicitAppAsync();
        var browser = await SignedInAsync();
        var requestUri = await PushedRequestUriAsync(app);

        var handle = requestUri[OidcAuthorizeParameters.PushedRequestUriPrefix.Length..];
        await SeedAsync(async session =>
            (await session.LoadAsync<OidcToken>(OidcTokenReference.DocumentId("par:" + handle)))!.ExpiresAt = DateTime.UtcNow.AddSeconds(-1));

        var response = await browser.GetAsync(RedeemUrl(app, requestUri));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await BodyAsync(response)).GetProperty("error").GetString().Should().Be("invalid_request_uri");
    }

    [Fact]
    public async Task A_request_uri_pushed_by_another_client_is_refused()
    {
        var owner = await SeedImplicitAppAsync("owner");
        var other = await SeedImplicitAppAsync("other");
        var browser = await SignedInAsync();
        var requestUri = await PushedRequestUriAsync(owner);

        var response = await browser.GetAsync(RedeemUrl(other, requestUri));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await BodyAsync(response)).GetProperty("error").GetString().Should().Be("invalid_request_uri");
    }

    [Fact]
    public async Task A_client_that_requires_par_cannot_authorize_by_value()
    {
        var app = await SeedImplicitAppAsync();
        await UpdateAppAsync(app, a => a.RequirePushedAuthorizationRequests = true);

        var query = ClientRedirect(await Client.GetAsync(
            $"/connect/authorize?client_id={Uri.EscapeDataString(app.ClientId)}&redirect_uri={Uri.EscapeDataString(app.RedirectUris[0])}"
            + "&response_type=code&scope=openid"), app);

        query["error"].Should().Be("invalid_request");
    }

    // ---------- JAR ----------

    [Fact]
    public async Task Jar_by_reference_is_refused()
    {
        var app = await SeedImplicitAppAsync();

        var response = await Client.GetAsync(RedeemUrl(app, "https://" + ClientId("webapp") + ".test/request.jwt"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "decision: request_uri_parameter_supported is false; fetching a client URL is an SSRF vector");
        (await BodyAsync(response)).GetProperty("error").GetString().Should().Be("request_uri_not_supported");
    }

    [Fact]
    public async Task A_signed_request_object_is_honoured_and_cannot_be_replayed()
    {
        var (app, key) = await SeedJarAppAsync();
        using var _ = key;
        var browser = await SignedInAsync();
        var requestObject = key.Sign(app.ClientId, Issuer, JarClaims(app));

        var query = ClientRedirect(await browser.GetAsync(JarUrl(app, requestObject)), app);
        query["code"].Should().NotBeNullOrEmpty();
        query["state"].Should().Be(State, "the request object's parameters are the request's");

        var replay = await browser.GetAsync(JarUrl(app, requestObject));
        replay.StatusCode.Should().Be(HttpStatusCode.BadRequest, "a request object's jti is used once");
        (await BodyAsync(replay)).GetProperty("error").GetString().Should().Be("invalid_request_object");
    }

    [Fact]
    public async Task An_unsigned_or_misaddressed_request_object_is_refused()
    {
        var (app, key) = await SeedJarAppAsync();
        using var _ = key;
        var browser = await SignedInAsync();

        var unsigned = await browser.GetAsync(JarUrl(app, OidcTestClientKey.Unsigned(app.ClientId, Issuer, JarClaims(app))));
        unsigned.StatusCode.Should().Be(HttpStatusCode.BadRequest, "alg=none proves nothing");
        (await BodyAsync(unsigned)).GetProperty("error").GetString().Should().Be("invalid_request_object");

        var otherAudience = await browser.GetAsync(JarUrl(app, key.Sign(app.ClientId, "https://other-idp.test", JarClaims(app))));
        otherAudience.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await BodyAsync(otherAudience)).GetProperty("error").GetString().Should().Be("invalid_request_object");
    }

    [Fact]
    public async Task A_client_that_requires_signed_request_objects_cannot_authorize_by_value_or_push_plain_parameters()
    {
        var (app, key) = await SeedJarAppAsync(requireSigned: true);
        using var _ = key;

        var byValue = ClientRedirect(await Client.GetAsync(
            $"/connect/authorize?client_id={Uri.EscapeDataString(app.ClientId)}&redirect_uri={Uri.EscapeDataString(app.RedirectUris[0])}"
            + "&response_type=code&scope=openid"), app);
        byValue["error"].Should().Be("invalid_request");

        var plainPush = await PushAsync(app);
        plainPush.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_signed_request_object_can_be_pushed_and_redeemed()
    {
        var (app, key) = await SeedJarAppAsync(requireSigned: true);
        using var _ = key;
        var browser = await SignedInAsync();

        var push = await Client.PostAsync("/connect/par", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = app.ClientId,
            ["client_secret"] = Secret,
            ["request"] = key.Sign(app.ClientId, Issuer, JarClaims(app)),
        }));
        push.StatusCode.Should().Be(HttpStatusCode.Created);
        var requestUri = (await BodyAsync(push)).GetProperty("request_uri").GetString()!;

        var query = ClientRedirect(await browser.GetAsync(RedeemUrl(app, requestUri)), app);
        query["code"].Should().NotBeNullOrEmpty("a pushed request object still counts as signed when it is redeemed");
    }

    /// <summary>
    /// A request object sent by value to a person who is not signed in yet must survive the bounce
    /// through sign-in. The handler's return URL carries the request object's <em>resolved</em>
    /// parameters as plain query values, so on the way back the request no longer counts as signed
    /// and a client with <c>RequireSignedRequestObject</c> is refused: JAR by value only works for a
    /// person who already has a session.
    /// </summary>
    [Fact]
    public async Task A_signed_request_object_survives_the_sign_in_bounce_for_a_client_that_requires_it()
    {
        var (app, key) = await SeedJarAppAsync(requireSigned: true);
        using var _ = key;

        var anonymous = await Client.GetAsync(JarUrl(app, key.Sign(app.ClientId, Issuer, JarClaims(app))));
        anonymous.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var location = anonymous.Headers.Location!.OriginalString;
        location.Should().StartWith("/connect/login");
        var returnUrl = System.Web.HttpUtility.ParseQueryString(location[location.IndexOf('?')..])["returnUrl"]!;

        var browser = await SignedInAsync();
        var query = ClientRedirect(await browser.GetAsync(returnUrl), app);

        query["error"].Should().BeNull("the person signed in; the signed request they started must now complete");
        query["code"].Should().NotBeNullOrEmpty();
    }
}
