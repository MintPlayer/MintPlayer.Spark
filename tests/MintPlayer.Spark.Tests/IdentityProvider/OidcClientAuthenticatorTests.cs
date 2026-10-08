using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using MintPlayer.Spark.IdentityProvider.Configuration;
using MintPlayer.Spark.IdentityProvider.Models;
using MintPlayer.Spark.IdentityProvider.Services;

namespace MintPlayer.Spark.Tests.IdentityProvider;

/// <summary>
/// <c>OidcClientAuthenticator</c> (I8, PRD D8/O15, and the D9 failure throttle), driven through
/// <c>/connect/revoke</c>: revoking an unknown token is a no-op that answers 200 once the client has
/// authenticated, so the status code is the authenticator's verdict and nothing else
/// (401 <c>invalid_client</c>, 400 <c>invalid_request</c>, or 200).
/// </summary>
public class OidcClientAuthenticatorTests(OidcClientAuthenticatorTests.ThrottleHost host)
    : OidcTestHost(host), IClassFixture<OidcClientAuthenticatorTests.ThrottleHost>
{
    private const string Secret = "s3cret-value-for-tests";
    private const int FailureLimit = 3;
    private const string RemoteIpHeader = "X-Test-Remote-IP";

    /// <summary>
    /// A low failure limit, and a way to choose the caller's IP address per request (TestServer
    /// leaves <c>RemoteIpAddress</c> unset), so the (client, IP) keying of the throttle is observable.
    /// </summary>
    public sealed class ThrottleHost : OidcSharedHost
    {
        protected override void ConfigureIdentityProvider(SparkIdentityProviderOptions options)
            => options.RateLimits.ClientAuthenticationFailures = FailureLimit;

        protected override void ConfigureServices(IServiceCollection services)
            => services.AddTransient<IStartupFilter, RemoteIpFromHeader>();

        private sealed class RemoteIpFromHeader : IStartupFilter
        {
            public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
            {
                app.Use((context, nextMiddleware) =>
                {
                    if (context.Request.Headers.TryGetValue(RemoteIpHeader, out var value) && IPAddress.TryParse(value.ToString(), out var ip))
                        context.Connection.RemoteIpAddress = ip;
                    return nextMiddleware(context);
                });
                next(app);
            };
        }
    }

    private Task<HttpResponseMessage> RevokeAsync(
        IDictionary<string, string> form, AuthenticationHeaderValue? authorization = null, string ip = "10.0.0.1")
    {
        form["token"] = OidcTokenReference.GenerateValue();
        var request = new HttpRequestMessage(HttpMethod.Post, "/connect/revoke") { Content = new FormUrlEncodedContent(form) };
        request.Headers.Authorization = authorization;
        request.Headers.Add(RemoteIpHeader, ip);
        return Client.SendAsync(request);
    }

    private static AuthenticationHeaderValue Basic(string clientId, string secret)
        => new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Uri.EscapeDataString(clientId)}:{Uri.EscapeDataString(secret)}")));

    private static async Task<string?> ErrorOf(HttpResponseMessage response)
    {
        var raw = await response.Content.ReadAsStringAsync();
        if (string.IsNullOrWhiteSpace(raw)) return null;
        return JsonDocument.Parse(raw).RootElement.TryGetProperty("error", out var e) ? e.GetString() : null;
    }

    /// <summary>Registers <paramref name="key"/> as the client's JWKS and, optionally, pins its auth method.</summary>
    private async Task<OidcApplication> SeedKeyClientAsync(string name, OidcTestClientKey key, string? method = OidcClientAuthMethods.PrivateKeyJwt, string? secret = null)
    {
        var app = await SeedApplicationAsync(ClientId(name), secret: secret);
        await SeedAsync(async session =>
        {
            var stored = await session.LoadAsync<OidcApplication>(app.Id);
            stored!.Jwks = key.JwksJson;
            stored.TokenEndpointAuthMethod = method;
        });
        return app;
    }

    private static string Assertion(OidcTestClientKey key, string clientId, string? audience = Issuer, string? jti = null, DateTime? expires = null, string? subject = null)
        => key.Sign(clientId, audience, new Dictionary<string, object>
        {
            ["sub"] = subject ?? clientId,
            ["jti"] = jti ?? Guid.NewGuid().ToString("N"),
        }, expires);

    private static Dictionary<string, string> AssertionForm(string assertion) => new()
    {
        ["client_assertion_type"] = OidcClientAuthenticator.JwtBearerAssertionType,
        ["client_assertion"] = assertion,
    };

    // ---------- secrets ----------

    [Fact]
    public async Task Client_secret_basic_and_client_secret_post_both_authenticate()
    {
        var app = await SeedApplicationAsync(ClientId("secret-app"));

        (await RevokeAsync(new Dictionary<string, string>(), Basic(app.ClientId, Secret))).StatusCode.Should().Be(HttpStatusCode.OK);
        (await RevokeAsync(new Dictionary<string, string> { ["client_id"] = app.ClientId, ["client_secret"] = Secret }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_wrong_secret_and_an_unknown_client_answer_the_same_invalid_client()
    {
        var app = await SeedApplicationAsync(ClientId("secret-app"));

        var wrong = await RevokeAsync(new Dictionary<string, string> { ["client_id"] = app.ClientId, ["client_secret"] = "wrong" });
        var unknown = await RevokeAsync(new Dictionary<string, string> { ["client_id"] = ClientId("nobody"), ["client_secret"] = Secret });

        wrong.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        unknown.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await wrong.Content.ReadAsStringAsync()).Should().Be(await unknown.Content.ReadAsStringAsync(),
            "O15: the answer must not tell which client ids exist");
        wrong.Headers.WwwAuthenticate.Should().NotBeEmpty("RFC 6749 §5.2: invalid_client answers 401 with WWW-Authenticate");
    }

    [Fact]
    public async Task Presenting_two_authentication_methods_at_once_is_invalid_request()
    {
        var app = await SeedApplicationAsync(ClientId("secret-app"));

        var response = await RevokeAsync(
            new Dictionary<string, string> { ["client_id"] = app.ClientId, ["client_secret"] = Secret },
            Basic(app.ClientId, Secret));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, "RFC 6749 §2.3: a client MUST NOT use more than one method");
        (await ErrorOf(response)).Should().Be("invalid_request");
    }

    [Fact]
    public async Task A_basic_header_naming_another_client_than_the_form_is_refused()
    {
        var app = await SeedApplicationAsync(ClientId("secret-app"));
        var other = await SeedApplicationAsync(ClientId("other-app"));

        var response = await RevokeAsync(new Dictionary<string, string> { ["client_id"] = other.ClientId }, Basic(app.ClientId, Secret));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorOf(response)).Should().Be("invalid_request");
    }

    // ---------- public clients ----------

    [Fact]
    public async Task A_public_client_authenticates_with_its_client_id_alone_but_a_confidential_one_does_not()
    {
        var spa = await SeedApplicationAsync(ClientId("spa"), secret: null, clientType: "public");
        var web = await SeedApplicationAsync(ClientId("web"));

        (await RevokeAsync(new Dictionary<string, string> { ["client_id"] = spa.ClientId }))
            .StatusCode.Should().Be(HttpStatusCode.OK, "RFC 7009 §2.1: a public client may revoke its own tokens");
        (await RevokeAsync(new Dictionary<string, string> { ["client_id"] = web.ClientId }))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized, "a confidential client that presents nothing is not authenticated");
    }

    [Fact]
    public async Task A_public_client_cannot_introspect()
    {
        var spa = await SeedApplicationAsync(ClientId("spa"), secret: null, clientType: "public");

        var response = await Client.PostAsync("/connect/introspect", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = spa.ClientId,
            ["token"] = OidcTokenReference.GenerateValue(),
        }));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "introspection discloses subjects and scopes, and a public client proves nothing about who it is");
    }

    // ---------- registered method ----------

    [Fact]
    public async Task A_client_registered_for_private_key_jwt_cannot_fall_back_to_a_secret()
    {
        using var key = new OidcTestClientKey();
        var app = await SeedKeyClientAsync("pkjwt-app", key, secret: Secret);

        var response = await RevokeAsync(new Dictionary<string, string> { ["client_id"] = app.ClientId, ["client_secret"] = Secret });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "a client registered with token_endpoint_auth_method may use only that method");
    }

    [Fact]
    public async Task A_client_registered_for_one_secret_method_may_use_the_other()
    {
        var app = await SeedApplicationAsync(ClientId("basic-app"));
        await SeedAsync(async session =>
            (await session.LoadAsync<OidcApplication>(app.Id))!.TokenEndpointAuthMethod = OidcClientAuthMethods.SecretBasic);

        (await RevokeAsync(new Dictionary<string, string> { ["client_id"] = app.ClientId, ["client_secret"] = Secret }))
            .StatusCode.Should().Be(HttpStatusCode.OK, "basic and post carry the same secret");
    }

    // ---------- private_key_jwt (RFC 7523) ----------

    [Fact]
    public async Task A_valid_client_assertion_authenticates_and_cannot_be_replayed()
    {
        using var key = new OidcTestClientKey();
        var app = await SeedKeyClientAsync("pkjwt-app", key);
        var assertion = Assertion(key, app.ClientId);

        (await RevokeAsync(AssertionForm(assertion))).StatusCode.Should().Be(HttpStatusCode.OK);
        (await RevokeAsync(AssertionForm(assertion))).StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "RFC 7523 §3: the jti may be used once, or a captured assertion is a reusable credential");
    }

    [Fact]
    public async Task An_assertion_for_another_audience_is_refused()
    {
        using var key = new OidcTestClientKey();
        var app = await SeedKeyClientAsync("pkjwt-app", key);

        (await RevokeAsync(AssertionForm(Assertion(key, app.ClientId, audience: "https://some-other-idp.test"))))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized, "an assertion minted for another provider must not work here");
    }

    [Fact]
    public async Task An_expired_assertion_is_refused()
    {
        using var key = new OidcTestClientKey();
        var app = await SeedKeyClientAsync("pkjwt-app", key);

        (await RevokeAsync(AssertionForm(Assertion(key, app.ClientId, expires: DateTime.UtcNow.AddMinutes(-10)))))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task An_assertion_signed_by_a_key_the_client_did_not_register_is_refused()
    {
        using var registered = new OidcTestClientKey();
        using var attacker = new OidcTestClientKey(fresh: true);
        var app = await SeedKeyClientAsync("pkjwt-app", registered);

        (await RevokeAsync(AssertionForm(Assertion(attacker, app.ClientId))))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task An_assertion_whose_subject_is_not_the_client_is_refused()
    {
        using var key = new OidcTestClientKey();
        var app = await SeedKeyClientAsync("pkjwt-app", key);

        (await RevokeAsync(AssertionForm(Assertion(key, app.ClientId, subject: "someone-else"))))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized, "RFC 7523 §3: sub MUST be the client id");
    }

    [Fact]
    public async Task An_unsupported_assertion_type_is_invalid_request()
    {
        using var key = new OidcTestClientKey();
        var app = await SeedKeyClientAsync("pkjwt-app", key);
        var form = AssertionForm(Assertion(key, app.ClientId));
        form["client_assertion_type"] = "urn:ietf:params:oauth:client-assertion-type:saml2-bearer";

        var response = await RevokeAsync(form);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorOf(response)).Should().Be("invalid_request");
    }

    // ---------- failure throttle (D9) ----------

    [Fact]
    public async Task After_the_failure_limit_even_the_correct_secret_is_refused_for_that_client_and_ip()
    {
        var app = await SeedApplicationAsync(ClientId("throttled"));
        const string ip = "10.1.0.1";

        for (var i = 0; i < FailureLimit; i++)
            (await RevokeAsync(new Dictionary<string, string> { ["client_id"] = app.ClientId, ["client_secret"] = "wrong" }, ip: ip))
                .StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var locked = await RevokeAsync(new Dictionary<string, string> { ["client_id"] = app.ClientId, ["client_secret"] = Secret }, ip: ip);

        locked.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "the pair is throttled for the rest of the window");
        (await ErrorOf(locked)).Should().Be("invalid_client", "the throttle answers like every other failure, so it is no oracle");
    }

    [Fact]
    public async Task The_throttle_is_keyed_on_client_and_ip_so_others_are_unaffected()
    {
        var victim = await SeedApplicationAsync(ClientId("throttled"));
        var bystander = await SeedApplicationAsync(ClientId("bystander"));
        const string attackerIp = "10.2.0.1";

        for (var i = 0; i < FailureLimit; i++)
            await RevokeAsync(new Dictionary<string, string> { ["client_id"] = victim.ClientId, ["client_secret"] = "wrong" }, ip: attackerIp);

        (await RevokeAsync(new Dictionary<string, string> { ["client_id"] = bystander.ClientId, ["client_secret"] = Secret }, ip: attackerIp))
            .StatusCode.Should().Be(HttpStatusCode.OK, "another client id from the same address is not throttled");

        (await RevokeAsync(new Dictionary<string, string> { ["client_id"] = victim.ClientId, ["client_secret"] = Secret }, ip: "10.2.0.2"))
            .StatusCode.Should().Be(HttpStatusCode.OK,
                "keyed on the client alone, anyone who knows a client id could lock the real client out");
    }
}

/// <summary>
/// A client's RSA signing key and its public JWKS, for <c>private_key_jwt</c> assertions and signed
/// request objects (JAR). One RSA key per process by default (generating RSA-2048 is a prime search);
/// <c>fresh: true</c> when a test needs a key the client did not register.
/// </summary>
internal sealed class OidcTestClientKey(bool fresh = false) : IDisposable
{
    private static readonly Lazy<RSA> Shared = new(() => RSA.Create(2048));

    private readonly RSA rsa = fresh ? RSA.Create(2048) : Shared.Value;

    public string KeyId { get; } = "k-" + Guid.NewGuid().ToString("N")[..8];

    /// <summary>The public half only, as a client would register it.</summary>
    public string JwksJson
    {
        get
        {
            var p = rsa.ExportParameters(includePrivateParameters: false);
            return JsonSerializer.Serialize(new
            {
                keys = new[]
                {
                    new { kty = "RSA", use = "sig", alg = "RS256", kid = KeyId, n = Base64UrlEncoder.Encode(p.Modulus!), e = Base64UrlEncoder.Encode(p.Exponent!) },
                },
            });
        }
    }

    /// <summary>A JWT signed with RS256 under <see cref="KeyId"/>.</summary>
    public string Sign(string issuer, string? audience, IDictionary<string, object> claims, DateTime? expires = null)
    {
        var exp = expires ?? DateTime.UtcNow.AddMinutes(2);
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            Claims = claims,
            IssuedAt = exp.AddMinutes(-3),
            NotBefore = exp.AddMinutes(-3),
            Expires = exp,
            SigningCredentials = new SigningCredentials(new RsaSecurityKey(rsa) { KeyId = KeyId }, SecurityAlgorithms.RsaSha256),
        });
    }

    /// <summary>The same claims, unsigned (<c>alg: none</c>).</summary>
    public static string Unsigned(string issuer, string? audience, IDictionary<string, object> claims)
        => new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            Claims = claims,
            Expires = DateTime.UtcNow.AddMinutes(2),
        });

    public void Dispose()
    {
        if (fresh) rsa.Dispose();
    }
}
