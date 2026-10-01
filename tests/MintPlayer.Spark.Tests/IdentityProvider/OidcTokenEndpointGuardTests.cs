using System.Net;
using System.Text.Json;
using MintPlayer.Spark.IdentityProvider.Models;
using MintPlayer.Spark.IdentityProvider.Services;

namespace MintPlayer.Spark.Tests.IdentityProvider;

/// <summary>
/// The refusals of <c>/connect/token</c> (the <c>Token</c> endpoint) that
/// <see cref="OidcTokenSecurityTests"/> does not reach: unknown and disabled clients on every
/// grant, missing parameters, unknown credentials, and a user who disappears between issuance
/// and redemption.
/// </summary>
public class OidcTokenEndpointGuardTests(OidcSharedHost host) : OidcTestHost(host), IClassFixture<OidcSharedHost>
{
    private const string Secret = "s3cret-value-for-tests";
    private string Email => UserEmail("grace");

    private Task<HttpResponseMessage> TokenAsync(Dictionary<string, string> form)
        => Client.PostAsync("/connect/token", new FormUrlEncodedContent(form));

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage r)
        => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;

    private async Task<(OidcApplication WebApp, OidcApplication Machine)> SeedClientsAsync()
    {
        var webapp = await SeedApplicationAsync(ClientId("webapp"),
            allowedScopes: ["openid", "profile", "offline_access"],
            grantTypes: ["authorization_code", "refresh_token"]);
        var machine = await SeedApplicationAsync(ClientId("machine"),
            allowedScopes: ["api.read"], grantTypes: ["client_credentials"]);
        await SeedApplicationAsync(ClientId("disabled"),
            grantTypes: ["authorization_code", "refresh_token", "client_credentials"], enabled: false);
        return (webapp, machine);
    }

    /// <summary>
    /// One row per guard clause. <paramref name="secret"/> null omits the field;
    /// <paramref name="parameter"/> null omits the grant's own credential.
    /// </summary>
    [Theory]
    [InlineData("authorization_code", "unknown", Secret, "code", "x", 401, "invalid_client")]
    [InlineData("authorization_code", "disabled", Secret, "code", "x", 401, "invalid_client")]
    [InlineData("authorization_code", "machine", Secret, "code", "x", 400, "unauthorized_client")]
    [InlineData("authorization_code", "webapp", Secret, "code", "never-issued", 400, "invalid_grant")]
    [InlineData("refresh_token", "webapp", Secret, null, null, 400, "invalid_request")]
    [InlineData("refresh_token", "unknown", Secret, "refresh_token", "x", 401, "invalid_client")]
    [InlineData("refresh_token", "disabled", Secret, "refresh_token", "x", 401, "invalid_client")]
    [InlineData("refresh_token", "webapp", null, "refresh_token", "x", 401, "invalid_client")]
    [InlineData("refresh_token", "webapp", "wrong-secret", "refresh_token", "x", 401, "invalid_client")]
    [InlineData("refresh_token", "webapp", Secret, "refresh_token", "never-issued", 400, "invalid_grant")]
    [InlineData("client_credentials", "machine", null, "scope", "api.read", 400, "invalid_request")]
    [InlineData("client_credentials", "unknown", Secret, "scope", "api.read", 401, "invalid_client")]
    [InlineData("client_credentials", "disabled", Secret, "scope", "api.read", 401, "invalid_client")]
    public async Task A_bad_request_is_refused_with_the_right_error(
        string grantType, string clientId, string? secret, string? parameter, string? value, int status, string error)
    {
        await SeedClientsAsync();

        // The rows name clients unscoped; SeedClientsAsync seeded them through ClientId(...).
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = grantType,
            ["client_id"] = ClientId(clientId),
        };
        if (secret != null) form["client_secret"] = secret;
        if (parameter != null) form[parameter] = value!;
        if (grantType == "authorization_code") form["redirect_uri"] = $"https://{ClientId("webapp")}.test/cb";

        var response = await TokenAsync(form);

        ((int)response.StatusCode).Should().Be(status);
        (await BodyAsync(response)).GetProperty("error").GetString().Should().Be(error);
    }

    private async Task<string> SeedRefreshTokenAsync(
        OidcApplication app, string subject, string status = "valid", TimeSpan? expiresIn = null, params string[] scopes)
    {
        var value = OidcTokenReference.GenerateValue();
        await SeedAsync(session => session.StoreAsync(new OidcToken
        {
            Id = OidcTokenReference.DocumentId(value),
            ApplicationId = app.Id!,
            // No authorization id: a token minted before grants were threaded through, which
            // OidcGrants deliberately permits.
            AuthorizationId = "",
            Subject = subject,
            Type = "refresh_token",
            Scopes = scopes.Length == 0 ? ["openid"] : [.. scopes],
            Status = status,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow + (expiresIn ?? TimeSpan.FromDays(1)),
        }));
        return value;
    }

    private Task<HttpResponseMessage> RefreshAsync(OidcApplication app, string refreshToken)
        => TokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = app.ClientId,
            ["client_secret"] = Secret,
            ["refresh_token"] = refreshToken,
        });

    private async Task DeleteUserAsync(string id)
        => await WithUserManagerAsync(async users => await users.DeleteAsync((await users.FindByIdAsync(id))!));

    [Fact]
    public async Task A_code_whose_user_was_deleted_is_refused()
    {
        var (webapp, _) = await SeedClientsAsync();
        var user = await SeedUserAsync(Email);
        var code = await ObtainCodeAsync(webapp, Email, ["openid"]);
        await DeleteUserAsync(user.Id!);

        var response = await TokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = webapp.ClientId,
            ["client_secret"] = Secret,
            ["code"] = code,
            ["redirect_uri"] = webapp.RedirectUris[0],
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await BodyAsync(response)).GetProperty("error_description").GetString().Should().Be("User not found.");
    }

    [Fact]
    public async Task A_refresh_token_whose_user_was_deleted_is_refused()
    {
        var (webapp, _) = await SeedClientsAsync();
        var refresh = await SeedRefreshTokenAsync(webapp, subject: "users/deleted-long-ago");

        var response = await RefreshAsync(webapp, refresh);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await BodyAsync(response)).GetProperty("error").GetString().Should().Be("invalid_grant");
    }

    [Fact]
    public async Task An_expired_refresh_token_is_refused()
    {
        var (webapp, _) = await SeedClientsAsync();
        var user = await SeedUserAsync(Email);
        var refresh = await SeedRefreshTokenAsync(webapp, user.Id!, expiresIn: TimeSpan.FromMinutes(-1));

        var response = await RefreshAsync(webapp, refresh);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await BodyAsync(response)).GetProperty("error").GetString().Should().Be("invalid_grant");
    }

    [Fact]
    public async Task Another_clients_refresh_token_is_refused()
    {
        var (webapp, _) = await SeedClientsAsync();
        var other = await SeedApplicationAsync(ClientId("other"), grantTypes: ["authorization_code", "refresh_token"]);
        var user = await SeedUserAsync(Email);
        var refresh = await SeedRefreshTokenAsync(other, user.Id!);

        var response = await RefreshAsync(webapp, refresh);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await BodyAsync(response)).GetProperty("error").GetString().Should().Be("invalid_grant");
    }

    /// <summary>
    /// A replayed token with no authorization id still revokes itself; there is simply no chain to
    /// sweep beyond it.
    /// </summary>
    [Fact]
    public async Task A_replayed_refresh_token_without_an_authorization_is_revoked_and_refused()
    {
        var (webapp, _) = await SeedClientsAsync();
        var user = await SeedUserAsync(Email);
        var refresh = await SeedRefreshTokenAsync(webapp, user.Id!, status: "redeemed");

        var response = await RefreshAsync(webapp, refresh);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var session = Store.OpenAsyncSession();
        (await session.LoadAsync<OidcToken>(OidcTokenReference.DocumentId(refresh))).Status.Should().Be("revoked");
    }

    [Fact]
    public async Task A_refresh_without_openid_issues_no_id_token()
    {
        var (webapp, _) = await SeedClientsAsync();
        var user = await SeedUserAsync(Email);
        var refresh = await SeedRefreshTokenAsync(webapp, user.Id!, scopes: ["profile"]);

        var response = await RefreshAsync(webapp, refresh);
        var body = await BodyAsync(response);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body.TryGetProperty("access_token", out _).Should().BeTrue();
        body.TryGetProperty("id_token", out _).Should().BeFalse("openid was not on the presented token");
    }
}
