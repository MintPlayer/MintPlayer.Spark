using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using MintPlayer.Spark.IdentityProvider.Models;
using MintPlayer.Spark.IdentityProvider.Services;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Tests.IdentityProvider;

/// <summary>
/// Revocation, UserInfo, discovery and JWKS — what a resource server relies on. Case ids refer
/// to §R.
/// </summary>
public class OidcResourceServerTests : OidcTestHost
{
    private const string Secret = "s3cret-value-for-tests";
    private const string Email = "alice@test.local";

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage r)
        => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;

    /// <summary>Runs the full flow and returns the issued access token.</summary>
    private async Task<(OidcApplication App, string AccessToken)> IssueAccessTokenAsync(string[]? scopes = null)
    {
        var app = await SeedApplicationAsync("webapp", allowedScopes: ["openid", "profile", "offline_access"]);
        await SeedUserAsync(Email);
        var code = await ObtainCodeAsync(app, Email, scopes ?? ["openid", "profile"]);

        var body = await BodyAsync(await Client.PostAsync("/connect/token", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["client_id"] = app.ClientId,
                ["client_secret"] = Secret,
                ["code"] = code,
                ["redirect_uri"] = app.RedirectUris[0],
            })));

        return (app, body.GetProperty("access_token").GetString()!);
    }

    private Task<HttpResponseMessage> IntrospectAsync(string clientId, string token)
        => Client.PostAsync("/connect/introspect", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = clientId, ["client_secret"] = Secret, ["token"] = token,
        }));

    private Task<HttpResponseMessage> RevokeAsync(string clientId, string token, string? hint = null)
    {
        var form = new Dictionary<string, string>
        {
            ["client_id"] = clientId, ["client_secret"] = Secret, ["token"] = token,
        };
        if (hint != null) form["token_type_hint"] = hint;
        return Client.PostAsync("/connect/revoke", new FormUrlEncodedContent(form));
    }

    private async Task<HttpResponseMessage> UserInfoAsync(string accessToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/connect/userinfo");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return await Client.SendAsync(request);
    }

    // ---------- revocation ----------

    /// <summary>R-V1/R-V3 — revoking an access token resolves it by jti and takes effect.</summary>
    [Fact]
    public async Task Revoking_an_access_token_makes_it_inactive()
    {
        var (app, accessToken) = await IssueAccessTokenAsync();

        (await BodyAsync(await IntrospectAsync(app.ClientId, accessToken)))
            .GetProperty("active").GetBoolean().Should().BeTrue();

        var revoke = await RevokeAsync(app.ClientId, accessToken);
        revoke.StatusCode.Should().Be(HttpStatusCode.OK);

        (await BodyAsync(await IntrospectAsync(app.ClientId, accessToken)))
            .GetProperty("active").GetBoolean().Should().BeFalse();
    }

    /// <summary>R-V4 / N3 — a wrong hint must not silently turn revocation into a no-op.</summary>
    [Fact]
    public async Task Revoking_an_access_token_works_despite_a_wrong_type_hint()
    {
        var (app, accessToken) = await IssueAccessTokenAsync();

        var revoke = await RevokeAsync(app.ClientId, accessToken, hint: "refresh_token");
        revoke.StatusCode.Should().Be(HttpStatusCode.OK);

        (await BodyAsync(await IntrospectAsync(app.ClientId, accessToken)))
            .GetProperty("active").GetBoolean().Should().BeFalse(
                "returning 200 while revoking nothing tells a caller responding to a breach that "
                + "a live credential is dead");
    }

    /// <summary>R-V6 — a foreign client gets 200 (RFC 7009) but revokes nothing.</summary>
    [Fact]
    public async Task Revoking_another_clients_token_returns_200_but_does_not_revoke()
    {
        var (owner, accessToken) = await IssueAccessTokenAsync();
        await SeedApplicationAsync("otherapp");

        var response = await RevokeAsync("otherapp", accessToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK, "RFC 7009 forbids revealing the failure");

        (await BodyAsync(await IntrospectAsync(owner.ClientId, accessToken)))
            .GetProperty("active").GetBoolean().Should().BeTrue("but nothing may actually be revoked");
    }

    /// <summary>R-V5/R-V7 — idempotent and forgiving.</summary>
    [Fact]
    public async Task Revoking_an_unknown_or_already_revoked_token_returns_200()
    {
        var (app, accessToken) = await IssueAccessTokenAsync();

        (await RevokeAsync(app.ClientId, accessToken)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await RevokeAsync(app.ClientId, accessToken)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await RevokeAsync(app.ClientId, OidcTokenReference.GenerateValue())).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>R-V2 / O1 — revoking a refresh token cascades to the access tokens beside it.</summary>
    [Fact]
    public async Task Revoking_a_refresh_token_cascades_to_its_access_tokens()
    {
        var app = await SeedApplicationAsync("webapp",
            allowedScopes: ["openid", "offline_access"],
            grantTypes: ["authorization_code", "refresh_token"]);
        await SeedUserAsync(Email);
        var code = await ObtainCodeAsync(app, Email, ["openid", "offline_access"]);

        var issued = await BodyAsync(await Client.PostAsync("/connect/token", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["client_id"] = app.ClientId,
                ["client_secret"] = Secret,
                ["code"] = code,
                ["redirect_uri"] = app.RedirectUris[0],
            })));

        var accessToken = issued.GetProperty("access_token").GetString()!;
        var refreshToken = issued.GetProperty("refresh_token").GetString()!;

        await RevokeAsync(app.ClientId, refreshToken);

        (await BodyAsync(await IntrospectAsync(app.ClientId, accessToken)))
            .GetProperty("active").GetBoolean().Should().BeFalse(
                "the cascade sweeps by AuthorizationId — it was dead code while that was empty");
    }

    // ---------- userinfo ----------

    /// <summary>R-U1 — claims for the granted scopes.</summary>
    [Fact]
    public async Task UserInfo_returns_claims_for_granted_scopes()
    {
        var (_, accessToken) = await IssueAccessTokenAsync(["openid", "profile"]);

        var response = await UserInfoAsync(accessToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await BodyAsync(response)).TryGetProperty("sub", out _).Should().BeTrue();
    }

    /// <summary>R-U4 / O5 — a revoked token stops yielding claims.</summary>
    [Fact]
    public async Task UserInfo_rejects_a_revoked_access_token()
    {
        var (app, accessToken) = await IssueAccessTokenAsync();

        (await UserInfoAsync(accessToken)).StatusCode.Should().Be(HttpStatusCode.OK);
        await RevokeAsync(app.ClientId, accessToken);

        (await UserInfoAsync(accessToken)).StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "signature and expiry alone cannot tell that a token was taken back");
    }

    /// <summary>R-U3/R-U9/R-U10 — nothing but a live access token gets in.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("not-a-jwt")]
    [InlineData("eyJhbGciOiJub25lIn0.eyJzdWIiOiJhdHRhY2tlciJ9.")]
    public async Task UserInfo_rejects_anything_that_is_not_a_live_access_token(string token)
    {
        await SeedApplicationAsync("webapp");

        var request = new HttpRequestMessage(HttpMethod.Get, "/connect/userinfo");
        if (token.Length > 0)
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);

        var response = await Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    /// <summary>R-U8 — an id_token is not an access token.</summary>
    [Fact]
    public async Task UserInfo_rejects_an_id_token_presented_as_an_access_token()
    {
        var app = await SeedApplicationAsync("webapp");
        await SeedUserAsync(Email);
        var code = await ObtainCodeAsync(app, Email, ["openid"]);

        var body = await BodyAsync(await Client.PostAsync("/connect/token", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["client_id"] = app.ClientId,
                ["client_secret"] = Secret,
                ["code"] = code,
                ["redirect_uri"] = app.RedirectUris[0],
            })));

        var response = await UserInfoAsync(body.GetProperty("id_token").GetString()!);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "id tokens carry no jti and have no governing record, so they cannot resolve");
    }

    // ---------- discovery + jwks ----------

    /// <summary>R-D1/R-D3 — what is advertised is what is enforced.</summary>
    [Fact]
    public async Task Discovery_advertises_the_configured_issuer_and_only_supported_capabilities()
    {
        var body = await BodyAsync(await Client.GetAsync("/.well-known/openid-configuration"));

        body.GetProperty("issuer").GetString().Should().Be(Issuer);
        body.GetProperty("response_types_supported").EnumerateArray()
            .Select(e => e.GetString()).Should().Equal("code");
        body.GetProperty("code_challenge_methods_supported").EnumerateArray()
            .Select(e => e.GetString()).Should().Equal("S256");
    }

    /// <summary>R-D6 / O7 — a forged Host header must not move the issuer.</summary>
    [Fact]
    public async Task A_forged_host_header_does_not_change_the_issuer()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/.well-known/openid-configuration");
        request.Headers.Host = "attacker.test";

        var body = await BodyAsync(await Client.SendAsync(request));

        body.GetProperty("issuer").GetString().Should().Be(Issuer,
            "a Host-derived issuer mints tokens claiming any issuer, signed with the real key");
    }

    /// <summary>R-J1 — the private key must never leave the process.</summary>
    [Fact]
    public async Task Jwks_exposes_no_private_key_material()
    {
        var raw = await (await Client.GetAsync("/.well-known/jwks")).Content.ReadAsStringAsync();

        var keys = JsonDocument.Parse(raw).RootElement.GetProperty("keys").EnumerateArray().ToList();
        keys.Should().NotBeEmpty();

        foreach (var component in new[] { "\"d\"", "\"p\"", "\"q\"", "\"dp\"", "\"dq\"", "\"qi\"" })
            raw.Should().NotContain(component, $"{component} is private key material");

        keys[0].TryGetProperty("n", out _).Should().BeTrue();
        keys[0].TryGetProperty("e", out _).Should().BeTrue();
    }

    /// <summary>R-J2 — a relying party can select the key that signed a token.</summary>
    [Fact]
    public async Task Jwks_kid_matches_the_kid_on_issued_tokens()
    {
        var (_, accessToken) = await IssueAccessTokenAsync();

        var header = JsonDocument.Parse(Base64UrlDecode(accessToken.Split('.')[0])).RootElement;
        var jwks = await BodyAsync(await Client.GetAsync("/.well-known/jwks"));

        var published = jwks.GetProperty("keys").EnumerateArray().Select(k => k.GetProperty("kid").GetString());
        published.Should().Contain(header.GetProperty("kid").GetString());
    }

    /// <summary>R-D5 — discovery and JWKS are public by design.</summary>
    [Fact]
    public async Task Discovery_and_jwks_need_no_credentials()
    {
        (await Client.GetAsync("/.well-known/openid-configuration")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Client.GetAsync("/.well-known/jwks")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private static byte[] Base64UrlDecode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += new string('=', (4 - padded.Length % 4) % 4);
        return Convert.FromBase64String(padded);
    }
}
