using System.Net;
using System.Text;
using System.Text.Json;
using MintPlayer.Spark.IdentityProvider.Models;

namespace MintPlayer.Spark.Tests.IdentityProvider;

/// <summary>
/// Token exchange (RFC 8693, <c>docs/identity_provider_platform_PRD.md</c> D8, I9): an API exchanges a
/// user's access token for one addressed to another API. The exchanged token keeps the subject, never
/// widens the scopes, says who acts (<c>act</c>), and is refused for a client without the grant, a public
/// client, an inactive subject token, and impersonation the client is not registered for.
/// </summary>
public class OidcTokenExchangeTests(OidcSharedHost host) : OidcTestHost(host), IClassFixture<OidcSharedHost>
{
    private const string Secret = "s3cret-value-for-tests";
    private const string ExchangeGrant = "urn:ietf:params:oauth:grant-type:token-exchange";
    private const string AccessTokenType = "urn:ietf:params:oauth:token-type:access_token";

    /// <summary>Two APIs per case: names unique to the case, so their resources never collide.</summary>
    private string Orders => ClientId("orders");
    private string Billing => ClientId("billing");

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    private static JsonElement Payload(string jwt)
    {
        var part = jwt.Split('.')[1].Replace('-', '+').Replace('_', '/');
        part += new string('=', (4 - part.Length % 4) % 4);
        return JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(part))).RootElement;
    }

    private static IEnumerable<string> Audiences(JsonElement payload)
    {
        var aud = payload.GetProperty("aud");
        return aud.ValueKind == JsonValueKind.Array ? aud.EnumerateArray().Select(a => a.GetString()!) : [aud.GetString()!];
    }

    /// <summary>The front-end client: a user signs in and consents to <c>orders.read</c> and <c>billing.read</c>.</summary>
    private async Task<(OidcApplication App, string AccessToken)> UserAccessTokenAsync()
    {
        var scopes = new[] { "openid", Orders + ".read", Billing + ".read" };
        var app = await SeedApplicationAsync(ClientId("webapp"), allowedScopes: scopes);
        var email = UserEmail("alice");
        await SeedUserAsync(email);
        var code = await ObtainCodeAsync(app, email, scopes);

        var response = await Client.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = app.ClientId,
            ["client_secret"] = Secret,
            ["code"] = code,
            ["redirect_uri"] = app.RedirectUris[0],
        }));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (app, (await BodyAsync(response)).GetProperty("access_token").GetString()!);
    }

    /// <summary>The API that exchanges: may hold every scope of both APIs, and the exchange grant.</summary>
    private async Task<OidcApplication> SeedExchangerAsync(bool allowImpersonation = true, string[]? grantTypes = null,
        string clientType = "confidential", string? secret = Secret)
    {
        var app = await SeedApplicationAsync(ClientId("orders-api"),
            secret: secret,
            clientType: clientType,
            allowedScopes: ["openid", Orders + ".read", Orders + ".write", Billing + ".read"],
            grantTypes: grantTypes ?? [ExchangeGrant, "client_credentials"]);

        if (allowImpersonation)
        {
            using var session = Store.OpenAsyncSession();
            var stored = (await session.LoadAsync<OidcApplication>(app.Id))!;
            stored.AllowImpersonation = true;
            await session.SaveChangesAsync();
            app.AllowImpersonation = true;
        }
        return app;
    }

    private Task<HttpResponseMessage> ExchangeAsync(OidcApplication app, string subjectToken,
        string? scope = null, string? audience = null, string? actorToken = null,
        string subjectTokenType = AccessTokenType, string? secret = Secret)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = ExchangeGrant,
            ["client_id"] = app.ClientId,
            ["subject_token"] = subjectToken,
            ["subject_token_type"] = subjectTokenType,
        };
        if (secret is not null) form["client_secret"] = secret;
        if (scope is not null) form["scope"] = scope;
        if (audience is not null) form["audience"] = audience;
        if (actorToken is not null)
        {
            form["actor_token"] = actorToken;
            form["actor_token_type"] = AccessTokenType;
        }
        return Client.PostAsync("/connect/token", new FormUrlEncodedContent(form));
    }

    private static async Task<string?> ErrorOf(HttpResponseMessage response)
        => (await BodyAsync(response)).TryGetProperty("error", out var e) ? e.GetString() : null;

    [Fact]
    public async Task Impersonation_keeps_the_subject_and_names_the_calling_client_as_actor()
    {
        var (_, subjectToken) = await UserAccessTokenAsync();
        var exchanger = await SeedExchangerAsync();

        var response = await ExchangeAsync(exchanger, subjectToken, scope: Orders + ".read");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await BodyAsync(response);
        body.GetProperty("issued_token_type").GetString().Should().Be(AccessTokenType);
        body.GetProperty("token_type").GetString().Should().Be("Bearer");

        var exchanged = Payload(body.GetProperty("access_token").GetString()!);
        exchanged.GetProperty("sub").GetString().Should().Be(Payload(subjectToken).GetProperty("sub").GetString(),
            "the exchanged token speaks for the same user");
        exchanged.GetProperty("client_id").GetString().Should().Be(exchanger.ClientId);
        exchanged.GetProperty("act").GetProperty("sub").GetString().Should().Be($"client:{exchanger.ClientId}",
            "even impersonation names the calling client, so nothing passes for the user acting alone");
        exchanged.GetProperty("scope").GetString().Should().Be(Orders + ".read");
    }

    [Fact]
    public async Task Without_an_actor_token_a_client_not_registered_for_impersonation_is_refused()
    {
        var (_, subjectToken) = await UserAccessTokenAsync();
        var exchanger = await SeedExchangerAsync(allowImpersonation: false);

        var response = await ExchangeAsync(exchanger, subjectToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorOf(response)).Should().Be("invalid_grant");
    }

    [Fact]
    public async Task Delegation_with_the_clients_own_actor_token_names_that_client_as_actor()
    {
        var (_, subjectToken) = await UserAccessTokenAsync();
        var exchanger = await SeedExchangerAsync(allowImpersonation: false);

        var machine = await BodyAsync(await Client.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = exchanger.ClientId,
            ["client_secret"] = Secret,
            ["scope"] = Orders + ".read",
        })));
        var actorToken = machine.GetProperty("access_token").GetString()!;

        var response = await ExchangeAsync(exchanger, subjectToken, actorToken: actorToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var exchanged = Payload((await BodyAsync(response)).GetProperty("access_token").GetString()!);
        exchanged.GetProperty("act").GetProperty("sub").GetString().Should().Be(exchanger.ClientId,
            "a machine actor token has no sub, so its client id is the actor");
    }

    /// <summary>
    /// The actor token is not checked to belong to the caller (TokenGrants.cs:173-179): a client that is
    /// not registered for impersonation can pass the user's own token as the actor and get a token whose
    /// <c>act</c> names the user, i.e. the user acting for themselves, which is exactly what the
    /// impersonation rule exists to prevent.
    /// </summary>
    [Fact]
    public async Task The_subject_token_cannot_double_as_the_actor_token_to_bypass_the_impersonation_rule()
    {
        var (_, subjectToken) = await UserAccessTokenAsync();
        var exchanger = await SeedExchangerAsync(allowImpersonation: false);

        var response = await ExchangeAsync(exchanger, subjectToken, actorToken: subjectToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "an actor token must represent the calling client; otherwise any client can impersonate without AllowImpersonation");
        (await ErrorOf(response)).Should().Be("invalid_grant");
    }

    [Fact]
    public async Task The_exchanged_token_never_widens_the_subject_tokens_scopes()
    {
        var (_, subjectToken) = await UserAccessTokenAsync();
        var exchanger = await SeedExchangerAsync();

        var response = await ExchangeAsync(exchanger, subjectToken, scope: Orders + ".write");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "orders.write was never granted to the user's token, even though the exchanging client may hold it");
        (await ErrorOf(response)).Should().Be("invalid_scope");
    }

    [Fact]
    public async Task The_audience_parameter_narrows_the_exchanged_tokens_audience()
    {
        var (_, subjectToken) = await UserAccessTokenAsync();
        var exchanger = await SeedExchangerAsync();

        var response = await ExchangeAsync(exchanger, subjectToken, audience: Billing);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var exchanged = Payload((await BodyAsync(response)).GetProperty("access_token").GetString()!);
        Audiences(exchanged).Should().Equal(Billing);
    }

    [Fact]
    public async Task A_revoked_subject_token_cannot_be_exchanged()
    {
        var (owner, subjectToken) = await UserAccessTokenAsync();
        var exchanger = await SeedExchangerAsync();

        (await Client.PostAsync("/connect/revoke", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = owner.ClientId,
            ["client_secret"] = Secret,
            ["token"] = subjectToken,
        }))).StatusCode.Should().Be(HttpStatusCode.OK);

        var response = await ExchangeAsync(exchanger, subjectToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorOf(response)).Should().Be("invalid_grant");
    }

    [Fact]
    public async Task A_client_without_the_exchange_grant_is_refused()
    {
        var (_, subjectToken) = await UserAccessTokenAsync();
        var exchanger = await SeedExchangerAsync(grantTypes: ["client_credentials"]);

        var response = await ExchangeAsync(exchanger, subjectToken);

        (await ErrorOf(response)).Should().Be("unauthorized_client");
    }

    [Fact]
    public async Task A_public_client_cannot_exchange_tokens()
    {
        var (_, subjectToken) = await UserAccessTokenAsync();
        var exchanger = await SeedExchangerAsync(clientType: "public", secret: null);

        var response = await ExchangeAsync(exchanger, subjectToken, secret: null);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorOf(response)).Should().Be("unauthorized_client");
    }

    [Fact]
    public async Task A_subject_token_type_other_than_access_token_is_refused()
    {
        var (_, subjectToken) = await UserAccessTokenAsync();
        var exchanger = await SeedExchangerAsync();

        var response = await ExchangeAsync(exchanger, subjectToken, subjectTokenType: "urn:ietf:params:oauth:token-type:id_token");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorOf(response)).Should().Be("invalid_request");
    }
}
