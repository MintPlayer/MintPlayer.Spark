using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace MintPlayer.Spark.Tests.IdentityProvider;

/// <summary>
/// Forged access tokens. The audit called algorithm confusion and <c>alg=none</c> "verified
/// sound" on the strength of reading the validation parameters; nothing had ever presented a
/// forged token to the running endpoints. Case ids refer to §R (R-I10–R-I14).
/// <para>
/// Every case here goes through both <c>/connect/introspect</c> and <c>/connect/userinfo</c>,
/// because they are separate entry points to the same resolver and a regression could plausibly
/// reach only one.
/// </para>
/// </summary>
public class OidcTokenForgeryTests : OidcTestHost
{
    private const string Secret = "s3cret-value-for-tests";

    private async Task<string> SetUpClientAsync()
    {
        await SeedApplicationAsync("resource-a");
        return "resource-a";
    }

    private async Task AssertRejectedAsync(string clientId, string token, string because)
    {
        var introspect = await Client.PostAsync("/connect/introspect", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["client_id"] = clientId, ["client_secret"] = Secret, ["token"] = token,
            }));

        introspect.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonDocument.Parse(await introspect.Content.ReadAsStringAsync())
            .RootElement.GetProperty("active").GetBoolean().Should().BeFalse(because);

        var request = new HttpRequestMessage(HttpMethod.Get, "/connect/userinfo");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        (await Client.SendAsync(request)).StatusCode.Should().Be(HttpStatusCode.Unauthorized, because);
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Base64Url(string value) => Base64Url(Encoding.UTF8.GetBytes(value));

    /// <summary>R-I11 — an unsigned token must never be accepted as signed.</summary>
    [Fact]
    public async Task An_alg_none_token_is_refused()
    {
        var clientId = await SetUpClientAsync();

        var header = Base64Url("""{"alg":"none","typ":"JWT"}""");
        var payload = Base64Url($$"""{"sub":"SparkUsers/attacker","iss":"{{Issuer}}","jti":"forged","exp":{{DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds()}}}""");

        await AssertRejectedAsync(clientId, $"{header}.{payload}.", "alg=none must be unreachable");
    }

    /// <summary>R-I10 — signed correctly, but by a key that is not ours.</summary>
    [Fact]
    public async Task A_token_signed_by_a_foreign_key_is_refused()
    {
        var clientId = await SetUpClientAsync();

        using var attackerKey = RSA.Create(2048);
        var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity([new Claim("sub", "SparkUsers/attacker"), new Claim("jti", "forged")]),
            Issuer = Issuer,
            Audience = clientId,
            Expires = DateTime.UtcNow.AddHours(1),
            SigningCredentials = new SigningCredentials(
                new RsaSecurityKey(attackerKey), SecurityAlgorithms.RsaSha256),
        });

        await AssertRejectedAsync(clientId, token, "only this provider's key may sign a token it accepts");
    }

    /// <summary>
    /// R-I12 — the classic RS256→HS256 confusion: sign with HMAC, using the provider's *public*
    /// key as the shared secret. It works against implementations that pick the algorithm from
    /// the token's own header.
    /// </summary>
    [Fact]
    public async Task An_hmac_signed_token_using_the_public_key_as_secret_is_refused()
    {
        var clientId = await SetUpClientAsync();

        var jwks = JsonDocument.Parse(await (await Client.GetAsync("/.well-known/jwks")).Content.ReadAsStringAsync());
        var key = jwks.RootElement.GetProperty("keys")[0];
        var modulus = key.GetProperty("n").GetString()!;

        var header = Base64Url("""{"alg":"HS256","typ":"JWT"}""");
        var payload = Base64Url($$"""{"sub":"SparkUsers/attacker","iss":"{{Issuer}}","jti":"forged","exp":{{DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds()}}}""");

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(modulus));
        var signature = Base64Url(hmac.ComputeHash(Encoding.ASCII.GetBytes($"{header}.{payload}")));

        await AssertRejectedAsync(clientId, $"{header}.{payload}.{signature}",
            "the signing key is pinned server-side, so the token's header cannot steer key selection");
    }

    /// <summary>R-I13 — a real token with its payload edited and the original signature kept.</summary>
    [Fact]
    public async Task A_tampered_payload_with_the_original_signature_is_refused()
    {
        var app = await SeedApplicationAsync("webapp");
        await SeedUserAsync("alice@test.local");
        var code = await ObtainCodeAsync(app, "alice@test.local", ["openid"]);

        var issued = JsonDocument.Parse(await (await Client.PostAsync("/connect/token", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["client_id"] = app.ClientId,
                ["client_secret"] = Secret,
                ["code"] = code,
                ["redirect_uri"] = app.RedirectUris[0],
            }))).Content.ReadAsStringAsync()).RootElement.GetProperty("access_token").GetString()!;

        var parts = issued.Split('.');
        var payloadJson = Encoding.UTF8.GetString(Base64UrlDecode(parts[1]));
        var tampered = payloadJson.Replace("\"scope\":\"openid\"", "\"scope\":\"openid admin\"");
        tampered.Should().NotBe(payloadJson, "the test must actually change something");

        await AssertRejectedAsync(app.ClientId, $"{parts[0]}.{Base64Url(tampered)}.{parts[2]}",
            "editing the payload invalidates the signature over it");
    }

    /// <summary>R-I14 — a forged <c>kid</c> must not redirect key resolution.</summary>
    [Fact]
    public async Task A_token_with_a_forged_kid_is_refused()
    {
        var clientId = await SetUpClientAsync();

        var jwks = JsonDocument.Parse(await (await Client.GetAsync("/.well-known/jwks")).Content.ReadAsStringAsync());
        var realKid = jwks.RootElement.GetProperty("keys")[0].GetProperty("kid").GetString()!;

        using var attackerKey = RSA.Create(2048);
        var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity([new Claim("sub", "SparkUsers/attacker"), new Claim("jti", "forged")]),
            Issuer = Issuer,
            Audience = clientId,
            Expires = DateTime.UtcNow.AddHours(1),
            SigningCredentials = new SigningCredentials(
                new RsaSecurityKey(attackerKey) { KeyId = realKid }, SecurityAlgorithms.RsaSha256),
        });

        await AssertRejectedAsync(clientId, token,
            "claiming our kid does not make an attacker's signature ours");
    }

    /// <summary>R-I15 / O7 — a token minted for another issuer, signed by nobody we trust.</summary>
    [Fact]
    public async Task A_token_from_a_different_issuer_is_refused()
    {
        var clientId = await SetUpClientAsync();

        using var attackerKey = RSA.Create(2048);
        var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity([new Claim("sub", "SparkUsers/attacker"), new Claim("jti", "forged")]),
            Issuer = "https://some-other-idp.test",
            Audience = clientId,
            Expires = DateTime.UtcNow.AddHours(1),
            SigningCredentials = new SigningCredentials(
                new RsaSecurityKey(attackerKey), SecurityAlgorithms.RsaSha256),
        });

        await AssertRejectedAsync(clientId, token, "issuer is validated on every resolution path");
    }

    /// <summary>
    /// R-I9 — a well-formed token this provider genuinely signed, but with no governing record,
    /// must fail closed. This is the case that would slip through if the database check were
    /// ever dropped in favour of "the signature is good enough".
    /// </summary>
    [Fact]
    public async Task A_validly_signed_token_with_no_record_is_refused()
    {
        var clientId = await SetUpClientAsync();

        var signingKey = Factory.GetService<MintPlayer.Spark.IdentityProvider.Services.OidcSigningKeyService>();
        var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(
            [
                new Claim("sub", "SparkUsers/attacker"),
                new Claim("jti", "never-issued-by-us"),
                new Claim("scope", "openid"),
            ]),
            Issuer = Issuer,
            Audience = clientId,
            Expires = DateTime.UtcNow.AddHours(1),
            SigningCredentials = new SigningCredentials(signingKey.GetSigningKey(), SecurityAlgorithms.RsaSha256),
        });

        await AssertRejectedAsync(clientId, token,
            "a missing record means the token was reaped or never ours — either way, not live");
    }

    private static byte[] Base64UrlDecode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += new string('=', (4 - padded.Length % 4) % 4);
        return Convert.FromBase64String(padded);
    }
}
