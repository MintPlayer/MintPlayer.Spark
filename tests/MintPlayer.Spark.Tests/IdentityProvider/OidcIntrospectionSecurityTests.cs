using System.Net;
using System.Text.Json;
using MintPlayer.Spark.IdentityProvider.Models;
using MintPlayer.Spark.IdentityProvider.Services;

namespace MintPlayer.Spark.Tests.IdentityProvider;

/// <summary>
/// <c>/connect/introspect</c> — caller authentication and, above all, token <em>ownership</em>.
/// Case ids refer to docs/idp-e2e-test-matrix.md §R.
/// </summary>
public class OidcIntrospectionSecurityTests : OidcTestHost
{
    private async Task<string> SeedRefreshTokenAsync(OidcApplication app, string subject, string[]? scopes = null)
    {
        var value = OidcTokenReference.GenerateValue();
        using var session = Store.OpenAsyncSession();
        await session.StoreAsync(new OidcToken
        {
            Id = OidcTokenReference.DocumentId(value),
            ApplicationId = app.Id!,
            Subject = subject,
            Type = "refresh_token",
            Scopes = [.. scopes ?? ["openid", "profile"]],
            Status = "valid",
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddDays(14),
        });
        await session.SaveChangesAsync();
        return value;
    }

    private Task<HttpResponseMessage> IntrospectAsync(string clientId, string secret, string token)
        => Client.PostAsync("/connect/introspect", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["client_secret"] = secret,
            ["token"] = token,
        }));

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    private const string Secret = "s3cret-value-for-tests";

    /// <summary>R-I2 — a client may introspect its own token.</summary>
    [Fact]
    public async Task Introspect_reports_its_own_valid_refresh_token_active()
    {
        var app = await SeedApplicationAsync("resource-a");
        var token = await SeedRefreshTokenAsync(app, "SparkUsers/alice");

        var body = await BodyAsync(await IntrospectAsync("resource-a", Secret, token));

        body.GetProperty("active").GetBoolean().Should().BeTrue();
        body.GetProperty("sub").GetString().Should().Be("SparkUsers/alice");
    }

    /// <summary>
    /// R-N1 — the Critical. Authenticating as *some* client is not authority over another
    /// client's tokens; before the fix this returned the owner's subject and scopes.
    /// </summary>
    [Fact]
    public async Task Introspect_refuses_another_clients_refresh_token()
    {
        var owner = await SeedApplicationAsync("resource-a");
        await SeedApplicationAsync("resource-b");
        var token = await SeedRefreshTokenAsync(owner, "SparkUsers/alice", ["openid", "profile", "billing"]);

        var response = await IntrospectAsync("resource-b", Secret, token);
        var body = await BodyAsync(response);

        body.GetProperty("active").GetBoolean().Should().BeFalse(
            "each resource server is its own application, so without an ownership gate one "
            + "resource server's introspection credentials enumerate another's users");

        var raw = body.GetRawText();
        raw.Should().NotContain("SparkUsers/alice", "the subject must not leak to a foreign caller");
        raw.Should().NotContain("billing", "nor must the scopes");
    }

    /// <summary>R-N3 — the ownership gate must not become its own oracle.</summary>
    [Fact]
    public async Task Introspect_cannot_distinguish_not_yours_from_never_issued()
    {
        var owner = await SeedApplicationAsync("resource-a");
        await SeedApplicationAsync("resource-b");
        var foreignToken = await SeedRefreshTokenAsync(owner, "SparkUsers/alice");

        var foreign = await (await IntrospectAsync("resource-b", Secret, foreignToken)).Content.ReadAsStringAsync();
        var unknown = await (await IntrospectAsync("resource-b", Secret, OidcTokenReference.GenerateValue())).Content.ReadAsStringAsync();

        foreign.Should().Be(unknown,
            "telling the caller which of the two it hit would turn the gate into a probe for "
            + "whether a given token value exists");
    }

    /// <summary>
    /// R-X3 / N2 — a resource server may introspect a token minted <em>for</em> it, even though
    /// it did not issue it. This is the deployment RFC 7662 exists for, and gating on ownership
    /// alone had refused it: tokens are issued to clients, so a resource server never owns the
    /// ones it is meant to accept.
    /// </summary>
    [Fact]
    public async Task A_resource_server_may_introspect_a_token_minted_for_its_audience()
    {
        var (app, accessToken) = await IssueAccessTokenForAudienceAsync("billing-api");
        await SeedApplicationAsync("billing-api");

        var body = await BodyAsync(await IntrospectAsync("billing-api", Secret, accessToken));

        body.GetProperty("active").GetBoolean().Should().BeTrue(
            "the token names this resource server as its audience");
        body.GetProperty("client_id").GetString().Should().Be(app.ClientId, "and reports the true issuer");
    }

    /// <summary>R-X4 / N2 — but a third party named by nobody still sees nothing.</summary>
    [Fact]
    public async Task An_unrelated_client_still_cannot_introspect_by_audience()
    {
        var (_, accessToken) = await IssueAccessTokenForAudienceAsync("billing-api");
        await SeedApplicationAsync("billing-api");
        await SeedApplicationAsync("unrelated");

        var body = await BodyAsync(await IntrospectAsync("unrelated", Secret, accessToken));

        body.GetProperty("active").GetBoolean().Should().BeFalse();
        body.GetRawText().Should().NotContain("SparkUsers/");
    }

    /// <summary>R-X5 / N2 — the gateway opt-out, which must be explicit.</summary>
    [Fact]
    public async Task A_gateway_may_opt_out_of_the_audience_restriction()
    {
        var (_, accessToken) = await IssueAccessTokenForAudienceAsync("billing-api");
        var gateway = await SeedApplicationAsync("gateway");

        using (var session = Store.OpenAsyncSession())
        {
            var stored = await session.LoadAsync<OidcApplication>(gateway.Id!);
            stored.MayIntrospectAnyAudience = true;
            await session.SaveChangesAsync();
        }

        var body = await BodyAsync(await IntrospectAsync("gateway", Secret, accessToken));

        body.GetProperty("active").GetBoolean().Should().BeTrue(
            "a gateway introspecting for the resources behind it — opted into deliberately");
    }

    /// <summary>Issues an access token whose granted scope declares <paramref name="audience"/>.</summary>
    private async Task<(OidcApplication App, string AccessToken)> IssueAccessTokenForAudienceAsync(string audience)
    {
        var app = await SeedApplicationAsync("webapp", allowedScopes: ["openid", "api.read"]);

        using (var session = Store.OpenAsyncSession())
        {
            var scope = await session.LoadAsync<OidcScope>("OidcScopes/api.read");
            scope.Audiences = [audience];
            await session.SaveChangesAsync();
        }

        await SeedUserAsync("alice@test.local");
        var code = await ObtainCodeAsync(app, "alice@test.local", ["openid", "api.read"]);

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

    /// <summary>R-A1.</summary>
    [Fact]
    public async Task Introspect_rejects_missing_client_credentials()
    {
        var response = await Client.PostAsync("/connect/introspect",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = "anything" }));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    /// <summary>R-A2/R-A3 — unknown client and bad secret must look the same.</summary>
    [Fact]
    public async Task Introspect_rejects_unknown_client_and_wrong_secret_alike()
    {
        await SeedApplicationAsync("resource-a");
        var token = await SeedRefreshTokenAsync(await SeedApplicationAsync("resource-c"), "SparkUsers/bob");

        var unknown = await IntrospectAsync("no-such-client", Secret, token);
        var wrongSecret = await IntrospectAsync("resource-a", "wrong-secret", token);

        unknown.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        wrongSecret.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await unknown.Content.ReadAsStringAsync())
            .Should().Be(await wrongSecret.Content.ReadAsStringAsync(),
                "distinguishing them lets an attacker enumerate client ids with no secret at all");
    }

    /// <summary>R-I5 — the whole point of the O5 fix.</summary>
    [Fact]
    public async Task Introspect_reports_a_revoked_token_inactive()
    {
        var app = await SeedApplicationAsync("resource-a");
        var token = await SeedRefreshTokenAsync(app, "SparkUsers/alice");

        using (var session = Store.OpenAsyncSession())
        {
            var doc = await session.LoadAsync<OidcToken>(OidcTokenReference.DocumentId(token));
            doc.Status = "revoked";
            await session.SaveChangesAsync();
        }

        var body = await BodyAsync(await IntrospectAsync("resource-a", Secret, token));

        body.GetProperty("active").GetBoolean().Should().BeFalse();
    }

    /// <summary>R-I8/R-I9 — unknown and malformed values answer, rather than fault.</summary>
    [Theory]
    [InlineData("not.a.jwt")]
    [InlineData("completely-made-up-value")]
    public async Task Introspect_reports_unrecognised_tokens_inactive(string token)
    {
        await SeedApplicationAsync("resource-a");

        var response = await IntrospectAsync("resource-a", Secret, token);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await BodyAsync(response)).GetProperty("active").GetBoolean().Should().BeFalse();
    }
}
