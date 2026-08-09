using System.Net;
using System.Text;
using System.Text.Json;
using MintPlayer.Spark.IdentityProvider.Models;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Tests.IdentityProvider;

/// <summary>
/// The token and the record of it must agree about scope. Case ids refer to
/// docs/idp-e2e-test-matrix.md §T (N11).
/// <para>
/// Every grant minted its JWT from the scopes that resolve to a defined, enabled
/// <c>OidcScope</c>, and then stored the <em>requested</em> list on the token document. The two
/// diverge exactly when a scope is undefined or has been disabled — and introspection reads the
/// document, so a resource server asking what a token may do was told about scopes the token does
/// not carry. Wrong in the permissive direction, and it made disabling a scope a half-measure: the
/// JWT dropped it at the next issuance while introspection kept vouching for it, for as long as the
/// refresh token lived.
/// </para>
/// </summary>
public class OidcScopeIntegrityTests : OidcTestHost
{
    private const string Secret = "s3cret-value-for-tests";

    private Task<HttpResponseMessage> TokenAsync(Dictionary<string, string> form)
        => Client.PostAsync("/connect/token", new FormUrlEncodedContent(form));

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    /// <summary>
    /// The <c>scope</c> claims the JWT actually carries, read straight off the payload — no
    /// handler, so what the test sees is what a resource server would parse.
    /// </summary>
    private static string[] ScopeClaims(string accessToken)
    {
        var payload = accessToken.Split('.')[1];
        var json = Encoding.UTF8.GetString(
            Convert.FromBase64String(payload.Replace('-', '+').Replace('_', '/')
                .PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=')));

        var scope = JsonDocument.Parse(json).RootElement.GetProperty("scope");
        return scope.ValueKind == JsonValueKind.Array
            ? [.. scope.EnumerateArray().Select(v => v.GetString()!)]
            : [.. scope.GetString()!.Split(' ', StringSplitOptions.RemoveEmptyEntries)];
    }

    private async Task DisableScopeAsync(string name)
    {
        using var session = Store.OpenAsyncSession();
        var scope = await session.LoadAsync<OidcScope>("OidcScopes/" + name.ToLowerInvariant());
        scope.Enabled = false;
        await session.SaveChangesAsync();
        WaitForIndexing(Store);
    }

    /// <summary>T-S1 — the happy path, so the refusals below are not passing trivially.</summary>
    [Fact]
    public async Task A_machine_token_carries_the_scopes_it_asked_for()
    {
        await SeedApplicationAsync("svc-ok",
            allowedScopes: ["api.read"], grantTypes: ["client_credentials"], redirectUris: []);

        var body = await BodyAsync(await TokenAsync(new()
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = "svc-ok",
            ["client_secret"] = Secret,
            ["scope"] = "api.read",
        }));

        body.GetProperty("scope").GetString().Should().Be("api.read");
        ScopeClaims(body.GetProperty("access_token").GetString()!).Should().Equal("api.read");
    }

    /// <summary>
    /// T-S2 — a machine client asking for a disabled scope is refused, not quietly downgraded.
    /// There is no user and no consent step here: the caller named exactly what it needs, so
    /// issuing a token for less produces a client that fails later, far from the cause.
    /// </summary>
    [Fact]
    public async Task A_disabled_scope_is_refused_rather_than_dropped()
    {
        await SeedApplicationAsync("svc-narrowed",
            allowedScopes: ["api.read", "api.write"], grantTypes: ["client_credentials"], redirectUris: []);
        await DisableScopeAsync("api.write");

        var response = await TokenAsync(new()
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = "svc-narrowed",
            ["client_secret"] = Secret,
            ["scope"] = "api.read api.write",
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var body = await BodyAsync(response);
        body.GetProperty("error").GetString().Should().Be("invalid_scope");
        body.GetProperty("error_description").GetString().Should().Contain("api.write");
    }

    /// <summary>
    /// T-S3 — the record matches the token. Introspection is the only way a resource server learns
    /// a token's scopes, so a document claiming more than the JWT carries is an authorization
    /// decision made on authority that was never granted.
    /// </summary>
    [Fact]
    public async Task The_stored_record_matches_what_the_token_carries()
    {
        await SeedApplicationAsync("svc-record",
            allowedScopes: ["api.read"], grantTypes: ["client_credentials"], redirectUris: []);

        var body = await BodyAsync(await TokenAsync(new()
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = "svc-record",
            ["client_secret"] = Secret,
            ["scope"] = "api.read",
        }));

        var introspection = await BodyAsync(await Client.PostAsync("/connect/introspect",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = "svc-record",
                ["client_secret"] = Secret,
                ["token"] = body.GetProperty("access_token").GetString()!,
            })));

        introspection.GetProperty("active").GetBoolean().Should().BeTrue();
        introspection.GetProperty("scope").GetString()!.Split(' ')
            .Should().BeEquivalentTo(ScopeClaims(body.GetProperty("access_token").GetString()!),
                "what introspection reports and what the token carries are the same authority, "
                + "and a resource server has no way to notice if they differ");
    }

    /// <summary>
    /// T-S4 — disabling a scope narrows the tokens minted from an existing refresh token, and the
    /// client is told. A refresh token outlives the configuration it was minted under, so this is
    /// the window in which an operator's revocation either takes effect or does not.
    /// </summary>
    [Fact]
    public async Task Disabling_a_scope_narrows_the_next_refresh_and_says_so()
    {
        var app = await SeedApplicationAsync("web-refresh",
            allowedScopes: ["openid", "api.read", "offline_access"],
            grantTypes: ["authorization_code", "refresh_token"]);

        await SeedUserAsync("scopes@test.local");
        var code = await ObtainCodeAsync(app, "scopes@test.local",
            scopes: ["openid", "api.read", "offline_access"]);

        var first = await BodyAsync(await TokenAsync(new()
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = app.ClientId,
            ["client_secret"] = Secret,
            ["code"] = code,
            ["redirect_uri"] = app.RedirectUris[0],
        }));

        ScopeClaims(first.GetProperty("access_token").GetString()!).Should().Contain("api.read");

        await DisableScopeAsync("api.read");

        var refreshed = await BodyAsync(await TokenAsync(new()
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = app.ClientId,
            ["client_secret"] = Secret,
            ["refresh_token"] = first.GetProperty("refresh_token").GetString()!,
        }));

        var newAccessToken = refreshed.GetProperty("access_token").GetString()!;
        ScopeClaims(newAccessToken).Should().NotContain("api.read",
            "the operator disabled it — that has to reach the token");

        refreshed.GetProperty("scope").GetString().Should().NotContain("api.read",
            "RFC 6749 §5.1 requires announcing a narrowed grant; otherwise the client goes on "
            + "calling an API it believes it still has access to");

        var introspection = await BodyAsync(await Client.PostAsync("/connect/introspect",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = app.ClientId,
                ["client_secret"] = Secret,
                ["token"] = newAccessToken,
            })));

        introspection.GetProperty("scope").GetString().Should().NotContain("api.read",
            "the record kept vouching for a scope the token no longer carried, which is what made "
            + "disabling a scope a half-measure");
    }
}
