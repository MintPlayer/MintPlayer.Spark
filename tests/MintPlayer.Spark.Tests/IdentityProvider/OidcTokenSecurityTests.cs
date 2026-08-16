using MintPlayer.Spark.Testing;
using System.Net;
using System.Text.Json;
using MintPlayer.Spark.IdentityProvider.Models;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Tests.IdentityProvider;

/// <summary>
/// <c>/connect/token</c> — all three grants, success and refusal. Case ids refer to §T.
/// </summary>
public class OidcTokenSecurityTests : OidcTestHost
{
    private const string Secret = "s3cret-value-for-tests";
    private const string Email = "alice@test.local";

    private Task<HttpResponseMessage> TokenAsync(Dictionary<string, string> form)
        => Client.PostAsync("/connect/token", new FormUrlEncodedContent(form));

    private Task<HttpResponseMessage> RedeemAsync(
        OidcApplication app, string code, string? secret = Secret, string? redirectUri = null, string? verifier = null)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = app.ClientId,
            ["code"] = code,
            ["redirect_uri"] = redirectUri ?? app.RedirectUris[0],
        };
        if (secret != null) form["client_secret"] = secret;
        if (verifier != null) form["code_verifier"] = verifier;
        return TokenAsync(form);
    }

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage r)
        => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;

    // ---------- authorization_code: success ----------

    /// <summary>T-H1.</summary>
    [Fact]
    public async Task Code_redemption_issues_access_and_id_tokens()
    {
        var app = await SeedApplicationAsync("webapp");
        await SeedUserAsync(Email);
        var code = await ObtainCodeAsync(app, Email, ["openid"]);

        var response = await RedeemAsync(app, code);
        var body = await BodyAsync(response);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body.GetProperty("access_token").GetString().Should().NotBeNullOrEmpty();
        body.GetProperty("token_type").GetString().Should().Be("Bearer");
        body.TryGetProperty("id_token", out _).Should().BeTrue("openid was granted");
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
    }

    /// <summary>T-G4 / O8 — a refresh token is only issued when asked for.</summary>
    [Fact]
    public async Task Code_redemption_issues_no_refresh_token_without_offline_access()
    {
        var app = await SeedApplicationAsync("webapp");
        await SeedUserAsync(Email);
        var code = await ObtainCodeAsync(app, Email, ["openid"]);

        var body = await BodyAsync(await RedeemAsync(app, code));

        body.TryGetProperty("refresh_token", out _).Should().BeFalse(
            "a 14-day credential must be requested, not handed out by default");
    }

    /// <summary>T-M4 / O22 — no id_token when openid was not granted.</summary>
    [Fact]
    public async Task Code_redemption_issues_no_id_token_without_openid()
    {
        var app = await SeedApplicationAsync("webapp", allowedScopes: ["profile"]);
        await SeedUserAsync(Email);
        var code = await ObtainCodeAsync(app, Email, ["profile"]);

        var body = await BodyAsync(await RedeemAsync(app, code));

        body.TryGetProperty("id_token", out _).Should().BeFalse(
            "an id_token asserts an authentication event, which is what openid requests");
    }

    /// <summary>T-P5 — PKCE round trip.</summary>
    [Fact]
    public async Task Code_redemption_succeeds_with_a_correct_pkce_verifier()
    {
        var (verifier, challenge) = Pkce();
        var app = await SeedApplicationAsync("webapp", requirePkce: true);
        await SeedUserAsync(Email);
        var code = await ObtainCodeAsync(app, Email, ["openid"], challenge);

        var response = await RedeemAsync(app, code, verifier: verifier);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ---------- authorization_code: refusal ----------

    /// <summary>T-B1 — RFC 6749 §4.1.3.</summary>
    [Fact]
    public async Task Code_cannot_be_redeemed_by_a_different_client()
    {
        var owner = await SeedApplicationAsync("webapp");
        var other = await SeedApplicationAsync("otherapp");
        await SeedUserAsync(Email);
        var code = await ObtainCodeAsync(owner, Email, ["openid"]);

        var response = await TokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = other.ClientId,
            ["client_secret"] = Secret,
            ["code"] = code,
            ["redirect_uri"] = owner.RedirectUris[0],
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await BodyAsync(response)).GetProperty("error").GetString().Should().Be("invalid_grant");
    }

    /// <summary>T-R1 — replay tears down the whole chain, not just the presented code.</summary>
    [Fact]
    public async Task Replayed_code_is_refused_and_revokes_the_issued_tokens()
    {
        var app = await SeedApplicationAsync("webapp",
            allowedScopes: ["openid", "offline_access"],
            grantTypes: ["authorization_code", "refresh_token"]);
        await SeedUserAsync(Email);
        var code = await ObtainCodeAsync(app, Email, ["openid", "offline_access"]);

        (await RedeemAsync(app, code)).StatusCode.Should().Be(HttpStatusCode.OK);
        var replay = await RedeemAsync(app, code);

        replay.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        await Store.WaitForIndexingAsync();
        using var session = Store.OpenAsyncSession();
        var issued = await session.Query<OidcToken>().Where(t => t.Type != "authorization_code").ToListAsync();
        issued.Should().NotBeEmpty();
        issued.Should().OnlyContain(t => t.Status == "revoked",
            "a code presented twice means the value leaked, so everything derived from it is suspect");
    }

    /// <summary>T-A1/T-A2 — client authentication.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("wrong-secret")]
    public async Task Code_redemption_requires_the_right_client_secret(string? secret)
    {
        var app = await SeedApplicationAsync("webapp");
        await SeedUserAsync(Email);
        var code = await ObtainCodeAsync(app, Email, ["openid"]);

        var response = await RedeemAsync(app, code, secret);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await BodyAsync(response)).GetProperty("error").GetString().Should().Be("invalid_client");
    }

    /// <summary>T-A8 — whitespace in ClientType must not disable authentication.</summary>
    [Fact]
    public async Task A_client_type_with_whitespace_still_requires_authentication()
    {
        var app = await SeedApplicationAsync("webapp", secret: null, clientType: " public");
        await SeedUserAsync(Email);
        var code = await ObtainCodeAsync(app, Email, ["openid"]);

        var response = await RedeemAsync(app, code, secret: null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "the check must fail closed — only an exactly-public client holding no secrets skips it");
    }

    /// <summary>T-A6 — a genuinely public client may omit the secret.</summary>
    [Fact]
    public async Task A_public_client_with_no_secrets_may_omit_the_secret()
    {
        var app = await SeedApplicationAsync("webapp", secret: null, clientType: "public");
        await SeedUserAsync(Email);
        var code = await ObtainCodeAsync(app, Email, ["openid"]);

        var response = await RedeemAsync(app, code, secret: null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>T-P1/T-P2 — PKCE refusals.</summary>
    [Fact]
    public async Task Code_redemption_rejects_a_missing_or_wrong_verifier()
    {
        var (_, challenge) = Pkce();
        var app = await SeedApplicationAsync("webapp", requirePkce: true);
        await SeedUserAsync(Email);

        var missing = await RedeemAsync(app, await ObtainCodeAsync(app, Email, ["openid"], challenge));
        missing.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var wrong = await RedeemAsync(app, await ObtainCodeAsync(app, Email, ["openid"], challenge), verifier: Pkce().Verifier);
        wrong.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    /// <summary>T-D1 — redirect_uri must match the one the code was issued for.</summary>
    [Fact]
    public async Task Code_redemption_rejects_a_mismatched_redirect_uri()
    {
        var app = await SeedApplicationAsync("webapp", redirectUris: ["https://webapp.test/cb", "https://webapp.test/other"]);
        await SeedUserAsync(Email);
        var code = await ObtainCodeAsync(app, Email, ["openid"], redirectUri: "https://webapp.test/cb");

        var response = await RedeemAsync(app, code, redirectUri: "https://webapp.test/other");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await BodyAsync(response)).GetProperty("error_description").GetString().Should().Contain("redirect_uri");
    }

    /// <summary>T-E1 — an expired code.</summary>
    [Fact]
    public async Task Code_redemption_rejects_an_expired_code()
    {
        var app = await SeedApplicationAsync("webapp");
        await SeedUserAsync(Email);
        var code = await ObtainCodeAsync(app, Email, ["openid"]);

        using (var session = Store.OpenAsyncSession())
        {
            var doc = await session.LoadAsync<OidcToken>(
                MintPlayer.Spark.IdentityProvider.Services.OidcTokenReference.DocumentId(code));
            doc.ExpiresAt = DateTime.UtcNow.AddMinutes(-1);
            await session.SaveChangesAsync();
        }

        var response = await RedeemAsync(app, code);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    /// <summary>T-M1/T-M2/T-M3 — protocol basics.</summary>
    [Fact]
    public async Task Token_endpoint_rejects_malformed_requests()
    {
        await SeedApplicationAsync("webapp");

        var unsupported = await TokenAsync(new Dictionary<string, string> { ["grant_type"] = "password" });
        (await BodyAsync(unsupported)).GetProperty("error").GetString().Should().Be("unsupported_grant_type");

        var missing = await TokenAsync(new Dictionary<string, string> { ["grant_type"] = "authorization_code" });
        (await BodyAsync(missing)).GetProperty("error").GetString().Should().Be("invalid_request");

        var json = await Client.PostAsync("/connect/token",
            new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));
        json.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ---------- refresh_token ----------

    private async Task<string> ObtainRefreshTokenAsync(OidcApplication app)
    {
        await SeedUserAsync(Email);
        var code = await ObtainCodeAsync(app, Email, ["openid", "offline_access"]);
        var body = await BodyAsync(await RedeemAsync(app, code));
        return body.GetProperty("refresh_token").GetString()!;
    }

    /// <summary>T-H2 — rotation.</summary>
    [Fact]
    public async Task Refresh_rotates_the_token_and_retires_the_old_one()
    {
        var app = await SeedApplicationAsync("webapp",
            allowedScopes: ["openid", "offline_access"], grantTypes: ["authorization_code", "refresh_token"]);
        var refresh = await ObtainRefreshTokenAsync(app);

        var response = await TokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = app.ClientId,
            ["client_secret"] = Secret,
            ["refresh_token"] = refresh,
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var rotated = (await BodyAsync(response)).GetProperty("refresh_token").GetString();
        rotated.Should().NotBe(refresh);
    }

    /// <summary>T-R2 — reusing a rotated refresh token revokes the chain (RFC 6819 §5.2.2.3).</summary>
    [Fact]
    public async Task Reused_refresh_token_is_refused_and_revokes_the_chain()
    {
        var app = await SeedApplicationAsync("webapp",
            allowedScopes: ["openid", "offline_access"], grantTypes: ["authorization_code", "refresh_token"]);
        var refresh = await ObtainRefreshTokenAsync(app);

        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = app.ClientId,
            ["client_secret"] = Secret,
            ["refresh_token"] = refresh,
        };

        var rotated = (await BodyAsync(await TokenAsync(form))).GetProperty("refresh_token").GetString()!;
        var reuse = await TokenAsync(form);

        reuse.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var stillWorks = await TokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = app.ClientId,
            ["client_secret"] = Secret,
            ["refresh_token"] = rotated,
        });

        stillWorks.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "the successor is revoked along with the rest of the chain — a reuse means the value leaked");
    }

    /// <summary>T-G3 / O8 — the refresh grant is gated like the others.</summary>
    [Fact]
    public async Task Refresh_grant_is_refused_for_a_client_not_registered_for_it()
    {
        var issuing = await SeedApplicationAsync("webapp",
            allowedScopes: ["openid", "offline_access"], grantTypes: ["authorization_code", "refresh_token"]);
        var refresh = await ObtainRefreshTokenAsync(issuing);

        using (var session = Store.OpenAsyncSession())
        {
            var app = await session.LoadAsync<OidcApplication>(issuing.Id!);
            app.AllowedGrantTypes = ["authorization_code"];
            await session.SaveChangesAsync();
        }

        var response = await TokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = issuing.ClientId,
            ["client_secret"] = Secret,
            ["refresh_token"] = refresh,
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await BodyAsync(response)).GetProperty("error").GetString().Should().Be("unauthorized_client");
    }

    // ---------- client_credentials ----------

    /// <summary>T-H3 — a machine token carries the application's claims and no subject.</summary>
    [Fact]
    public async Task Client_credentials_issues_an_access_token_only()
    {
        var app = await SeedApplicationAsync("machine",
            allowedScopes: ["api.read", "api.write"], grantTypes: ["client_credentials"]);

        var response = await TokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = app.ClientId,
            ["client_secret"] = Secret,
            ["scope"] = "api.read",
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await BodyAsync(response);
        body.GetProperty("access_token").GetString().Should().NotBeNullOrEmpty();
        body.TryGetProperty("id_token", out _).Should().BeFalse();
        body.TryGetProperty("refresh_token", out _).Should().BeFalse();
    }

    /// <summary>T-G5 / O14 — least privilege: no scope means no token, not every scope.</summary>
    [Fact]
    public async Task Client_credentials_requires_an_explicit_scope()
    {
        var app = await SeedApplicationAsync("machine",
            allowedScopes: ["api.read", "api.admin"], grantTypes: ["client_credentials"]);

        var response = await TokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = app.ClientId,
            ["client_secret"] = Secret,
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await BodyAsync(response)).GetProperty("error").GetString().Should().Be("invalid_scope");
    }

    /// <summary>T-G6 — a scope outside the allowed set fails the whole request.</summary>
    [Fact]
    public async Task Client_credentials_rejects_a_scope_outside_the_allowed_set()
    {
        var app = await SeedApplicationAsync("machine",
            allowedScopes: ["api.read"], grantTypes: ["client_credentials"]);

        var response = await TokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = app.ClientId,
            ["client_secret"] = Secret,
            ["scope"] = "api.read api.write",
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await BodyAsync(response)).GetProperty("error").GetString().Should().Be("invalid_scope");
    }

    /// <summary>T-G2 — grant gating on client_credentials.</summary>
    [Fact]
    public async Task Client_credentials_is_refused_for_a_client_not_registered_for_it()
    {
        var app = await SeedApplicationAsync("webapp", grantTypes: ["authorization_code"]);

        var response = await TokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = app.ClientId,
            ["client_secret"] = Secret,
            ["scope"] = "openid",
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await BodyAsync(response)).GetProperty("error").GetString().Should().Be("unauthorized_client");
    }

    /// <summary>T-A3 — an expired secret no longer authenticates.</summary>
    [Fact]
    public async Task An_expired_client_secret_is_refused()
    {
        var app = await SeedApplicationAsync("machine",
            allowedScopes: ["api.read"], grantTypes: ["client_credentials"]);

        using (var session = Store.OpenAsyncSession())
        {
            var stored = await session.LoadAsync<OidcApplication>(app.Id!);
            stored.Secrets[0].ExpiresAt = DateTime.UtcNow.AddDays(-1);
            await session.SaveChangesAsync();
        }

        var response = await TokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = app.ClientId,
            ["client_secret"] = Secret,
            ["scope"] = "api.read",
        });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    /// <summary>T-A4 — either secret works while both are live, so rotation has no outage.</summary>
    [Fact]
    public async Task Either_secret_authenticates_during_rotation()
    {
        var app = await SeedApplicationAsync("machine",
            allowedScopes: ["api.read"], grantTypes: ["client_credentials"]);

        using (var session = Store.OpenAsyncSession())
        {
            var stored = await session.LoadAsync<OidcApplication>(app.Id!);
            stored.Secrets.Add(new ClientSecret
            {
                Hash = MintPlayer.Spark.IdentityProvider.Services.ClientSecretHasher.Hash("the-new-secret"),
            });
            await session.SaveChangesAsync();
        }

        foreach (var secret in new[] { Secret, "the-new-secret" })
        {
            var response = await TokenAsync(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = app.ClientId,
                ["client_secret"] = secret,
                ["scope"] = "api.read",
            });

            response.StatusCode.Should().Be(HttpStatusCode.OK, $"secret '{secret}' should authenticate");
        }
    }
}
