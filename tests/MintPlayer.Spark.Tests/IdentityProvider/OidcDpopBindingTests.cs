using System.Net;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;
using MintPlayer.Spark.IdentityProvider.Models;

namespace MintPlayer.Spark.Tests.IdentityProvider;

/// <summary>
/// DPoP at the token endpoint (RFC 9449 §5, PRD D8): a valid proof binds the access token to the proof's key
/// (<c>cnf.jkt</c>, <c>token_type: DPoP</c>), a bad one is refused before anything is minted, and a bound
/// refresh token rotates only with a proof from the same key.
/// </summary>
public class OidcDpopBindingTests(OidcSharedHost host) : OidcTestHost(host), IClassFixture<OidcSharedHost>
{
    private const string Secret = "s3cret-value-for-tests";
    private const string TokenUrl = Issuer + "/connect/token";

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    private static JsonElement PayloadOf(string jwt)
        => JsonDocument.Parse(Base64UrlEncoder.Decode(jwt.Split('.')[1])).RootElement;

    private async Task<(OidcApplication App, string Code)> CodeAsync(string[]? scopes = null, string[]? grantTypes = null)
    {
        var app = await SeedApplicationAsync(ClientId("dpop-client"),
            allowedScopes: ["openid", "offline_access"],
            grantTypes: grantTypes ?? ["authorization_code"]);
        await SeedUserAsync(UserEmail("dpop-user"));
        return (app, await ObtainCodeAsync(app, UserEmail("dpop-user"), scopes ?? ["openid"]));
    }

    private Task<HttpResponseMessage> TokenAsync(Dictionary<string, string> form, string? proof)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/connect/token") { Content = new FormUrlEncodedContent(form) };
        if (proof is not null)
            request.Headers.TryAddWithoutValidation("DPoP", proof);
        return Client.SendAsync(request);
    }

    private static Dictionary<string, string> CodeForm(OidcApplication app, string code) => new()
    {
        ["grant_type"] = "authorization_code",
        ["client_id"] = app.ClientId,
        ["client_secret"] = Secret,
        ["code"] = code,
        ["redirect_uri"] = app.RedirectUris[0],
    };

    [Fact]
    public async Task A_valid_proof_binds_the_access_token_to_its_key()
    {
        using var key = new DpopTestKey();
        var (app, code) = await CodeAsync();

        var response = await TokenAsync(CodeForm(app, code), key.Proof("POST", TokenUrl));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await BodyAsync(response);
        body.GetProperty("token_type").GetString().Should().Be("DPoP",
            "RFC 9449 §5: a bound token is announced as DPoP so the client sends it with that scheme");

        var cnf = PayloadOf(body.GetProperty("access_token").GetString()!).GetProperty("cnf");
        cnf.GetProperty("jkt").GetString().Should().Be(key.Thumbprint);
    }

    [Fact]
    public async Task Without_a_proof_the_token_is_an_unbound_bearer_token()
    {
        var (app, code) = await CodeAsync();

        var response = await TokenAsync(CodeForm(app, code), proof: null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await BodyAsync(response);
        body.GetProperty("token_type").GetString().Should().Be("Bearer");
        PayloadOf(body.GetProperty("access_token").GetString()!).TryGetProperty("cnf", out _).Should().BeFalse();
    }

    [Fact]
    public async Task A_proof_for_another_endpoint_is_refused_and_nothing_is_minted()
    {
        using var key = new DpopTestKey();
        var (app, code) = await CodeAsync();

        var response = await TokenAsync(CodeForm(app, code), key.Proof("POST", Issuer + "/connect/userinfo"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await BodyAsync(response);
        body.GetProperty("error").GetString().Should().Be("invalid_dpop_proof");
        body.TryGetProperty("access_token", out _).Should().BeFalse();
    }

    [Fact]
    public async Task A_proof_is_accepted_once_at_the_token_endpoint()
    {
        using var key = new DpopTestKey();
        var (app, first) = await CodeAsync();
        var second = await ObtainCodeAsync(app, UserEmail("dpop-user"), ["openid"]);
        var proof = key.Proof("POST", TokenUrl);

        (await TokenAsync(CodeForm(app, first), proof)).StatusCode.Should().Be(HttpStatusCode.OK);

        var replay = await TokenAsync(CodeForm(app, second), proof);
        replay.StatusCode.Should().Be(HttpStatusCode.BadRequest, "the provider's jti cache is in RavenDB, so a replay is caught");
        (await BodyAsync(replay)).GetProperty("error").GetString().Should().Be("invalid_dpop_proof");
    }

    [Fact]
    public async Task A_client_registered_with_RequireDpop_must_send_a_proof()
    {
        var (app, code) = await CodeAsync();
        using (var session = Store.OpenAsyncSession())
        {
            var stored = await session.LoadAsync<OidcApplication>(app.Id!);
            stored.RequireDpop = true;
            await session.SaveChangesAsync();
        }

        var response = await TokenAsync(CodeForm(app, code), proof: null);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await BodyAsync(response)).GetProperty("error").GetString().Should().Be("invalid_dpop_proof");
    }

    [Fact]
    public async Task A_bound_refresh_token_rotates_only_with_a_proof_from_the_same_key()
    {
        using var key = new DpopTestKey();
        using var thief = new DpopTestKey();
        var (app, code) = await CodeAsync(["openid", "offline_access"], ["authorization_code", "refresh_token"]);

        var issued = await BodyAsync(await TokenAsync(CodeForm(app, code), key.Proof("POST", TokenUrl)));
        var refreshToken = issued.GetProperty("refresh_token").GetString()!;

        Dictionary<string, string> RefreshForm() => new()
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = app.ClientId,
            ["client_secret"] = Secret,
            ["refresh_token"] = refreshToken,
        };

        var stolen = await TokenAsync(RefreshForm(), thief.Proof("POST", TokenUrl));
        stolen.StatusCode.Should().Be(HttpStatusCode.BadRequest, "a proof from another key must not rotate a bound refresh token");
        (await BodyAsync(stolen)).GetProperty("error").GetString().Should().Be("invalid_dpop_proof");

        var unproven = await TokenAsync(RefreshForm(), proof: null);
        unproven.StatusCode.Should().Be(HttpStatusCode.BadRequest, "nor may it be redeemed as a plain bearer refresh");

        var rotated = await TokenAsync(RefreshForm(), key.Proof("POST", TokenUrl));
        rotated.StatusCode.Should().Be(HttpStatusCode.OK, "a refused binding check must not spend the refresh token");
        var body = await BodyAsync(rotated);
        body.GetProperty("token_type").GetString().Should().Be("DPoP");
        PayloadOf(body.GetProperty("access_token").GetString()!).GetProperty("cnf").GetProperty("jkt").GetString()
            .Should().Be(key.Thumbprint);
    }
}
