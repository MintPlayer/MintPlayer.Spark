using System.Net;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;
using MintPlayer.Spark.IdentityProvider.Models;

namespace MintPlayer.Spark.Tests.IdentityProvider;

/// <summary>
/// What the stock ASP.NET OpenIdConnect handler needs from this provider (#490 M6, spike S6): logout
/// through <c>id_token_hint</c>, the discovery fields, <c>at_hash</c>/<c>auth_time</c> on a real
/// token response, and the login page's external-login outcome display.
/// </summary>
public class OidcRelyingPartyCompatibilityTests(OidcSharedHost host) : OidcTestHost(host), IClassFixture<OidcSharedHost>
{
    private const string Secret = "s3cret-value-for-tests";
    private string Email => UserEmail("rp");

    private async Task<(OidcApplication App, JsonElement Body)> SignInThroughTheFlowAsync(string name, string[]? postLogout = null)
    {
        var app = await SeedApplicationAsync(ClientId(name), postLogoutRedirectUris: postLogout);
        await SeedUserAsync(Email);
        var code = await ObtainCodeAsync(app, Email, ["openid"]);

        var response = await Client.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = app.ClientId,
            ["client_secret"] = Secret,
            ["code"] = code,
            ["redirect_uri"] = app.RedirectUris[0],
        }));
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        return (app, JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement);
    }

    private static JsonElement PayloadOf(string jwt)
        => JsonDocument.Parse(Base64UrlEncoder.Decode(jwt.Split('.')[1])).RootElement;

    // --- logout -----------------------------------------------------------------------------------

    [Fact]
    public async Task Logout_derives_the_client_from_a_valid_id_token_hint()
    {
        var (_, body) = await SignInThroughTheFlowAsync("hint", postLogout: [$"https://{ClientId("hint")}.test/bye"]);
        var idToken = body.GetProperty("id_token").GetString()!;

        // No client_id: what the stock handler sends.
        var response = await Client.GetAsync($"/connect/logout?id_token_hint={Uri.EscapeDataString(idToken)}"
            + $"&post_logout_redirect_uri={Uri.EscapeDataString($"https://{ClientId("hint")}.test/bye")}&state=st");

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.Should().Be($"https://{ClientId("hint")}.test/bye?state=st");
    }

    [Fact]
    public async Task Logout_refuses_a_tampered_id_token_hint()
    {
        var (_, body) = await SignInThroughTheFlowAsync("tamper", postLogout: [$"https://{ClientId("tamper")}.test/bye"]);
        var parts = body.GetProperty("id_token").GetString()!.Split('.');
        // Re-sign nothing: swap the payload for one naming the same client, keep the old signature.
        var forged = $"{parts[0]}.{Base64UrlEncoder.Encode($"{{\"aud\":\"{ClientId("tamper")}\",\"iss\":\"{Issuer}\",\"sub\":\"x\"}}")}.{parts[2]}";

        var response = await Client.GetAsync($"/connect/logout?id_token_hint={Uri.EscapeDataString(forged)}"
            + $"&post_logout_redirect_uri={Uri.EscapeDataString($"https://{ClientId("tamper")}.test/bye")}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("Invalid id_token_hint");
    }

    [Fact]
    public async Task Logout_refuses_an_id_token_hint_for_a_different_client_than_client_id()
    {
        var (_, body) = await SignInThroughTheFlowAsync("hinted", postLogout: [$"https://{ClientId("hinted")}.test/bye"]);
        var other = await SeedApplicationAsync(ClientId("other"), postLogoutRedirectUris: [$"https://{ClientId("other")}.test/bye"]);

        var response = await Client.GetAsync($"/connect/logout?client_id={Uri.EscapeDataString(other.ClientId)}"
            + $"&id_token_hint={Uri.EscapeDataString(body.GetProperty("id_token").GetString()!)}"
            + $"&post_logout_redirect_uri={Uri.EscapeDataString($"https://{ClientId("other")}.test/bye")}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Logout_refuses_an_access_token_presented_as_the_hint()
    {
        var (_, body) = await SignInThroughTheFlowAsync("access", postLogout: [$"https://{ClientId("access")}.test/bye"]);

        var response = await Client.GetAsync($"/connect/logout?id_token_hint={Uri.EscapeDataString(body.GetProperty("access_token").GetString()!)}"
            + $"&post_logout_redirect_uri={Uri.EscapeDataString($"https://{ClientId("access")}.test/bye")}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // --- token response ---------------------------------------------------------------------------

    [Fact]
    public async Task The_id_token_carries_at_hash_auth_time_and_its_own_lifetime()
    {
        var before = DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeSeconds();
        var (_, body) = await SignInThroughTheFlowAsync("claims");
        var accessToken = body.GetProperty("access_token").GetString()!;
        var payload = PayloadOf(body.GetProperty("id_token").GetString()!);

        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(accessToken));
        payload.GetProperty("at_hash").GetString().Should().Be(Base64UrlEncoder.Encode(hash[..16]));
        payload.GetProperty("auth_time").GetInt64().Should().BeGreaterThanOrEqualTo(before,
            "auth_time is the sign-in this flow just performed");
        (payload.GetProperty("exp").GetInt64() - payload.GetProperty("iat").GetInt64()).Should().Be(5 * 60,
            "the id_token has its own 5-minute default, not the access token's 60");
    }

    // --- discovery --------------------------------------------------------------------------------

    [Fact]
    public async Task Discovery_advertises_the_query_response_mode_and_the_supported_claims()
    {
        var document = JsonDocument.Parse(await (await Client.GetAsync("/.well-known/openid-configuration")).Content.ReadAsStringAsync()).RootElement;

        document.GetProperty("response_modes_supported").EnumerateArray().Select(e => e.GetString()).Should().Equal("query");
        var claims = document.GetProperty("claims_supported").EnumerateArray().Select(e => e.GetString()).ToArray();
        new[] { "sub", "iss", "aud", "exp", "iat", "nonce", "at_hash", "auth_time" }.Except(claims).Should().BeEmpty();
    }

    // --- external-login outcome on /connect/login ---------------------------------------------------

    [Fact]
    public async Task Login_page_shows_a_fixed_message_for_an_external_login_code()
    {
        var html = await (await Client.GetAsync("/connect/login?returnUrl=%2F&sparkExternalLogin=email_not_verified")).Content.ReadAsStringAsync();

        html.Should().Contain("The provider has not verified that email address.");
    }

    [Fact]
    public async Task Login_page_never_reflects_an_external_login_code_it_does_not_know()
    {
        const string injected = "<script>alert(1)</script>";

        var html = await (await Client.GetAsync($"/connect/login?returnUrl=%2F&sparkExternalLogin={Uri.EscapeDataString(injected)}")).Content.ReadAsStringAsync();

        html.Should().NotContain(injected);
        html.Should().NotContain("alert(1)");
        html.Should().Contain("Sign-in with the external provider failed.");
    }

    [Fact]
    public async Task Authorize_hands_an_external_login_code_to_the_login_page()
    {
        var app = await SeedApplicationAsync(ClientId("bounce"));
        var authorize = $"/connect/authorize?client_id={Uri.EscapeDataString(app.ClientId)}"
            + $"&redirect_uri={Uri.EscapeDataString(app.RedirectUris[0])}&response_type=code&scope=openid";

        var response = await Client.GetAsync(authorize + "&sparkExternalLogin=no_login_info");

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var location = response.Headers.Location!.OriginalString;
        var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(location[location.IndexOf('?')..]);
        query["sparkExternalLogin"].ToString().Should().Be("no_login_info");
        query["returnUrl"].ToString().Should().NotContain("sparkExternalLogin",
            "the code is lifted out of the pending authorization, so it does not come back on the next bounce");
        query["returnUrl"].ToString().Should().StartWith("/connect/authorize?client_id=");
    }
}
