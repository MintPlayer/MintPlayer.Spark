using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.IdentityModel.JsonWebTokens;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.IdentityProvider.Models;
using MintPlayer.Spark.IdentityProvider.Services;

namespace MintPlayer.Spark.Tests.IdentityProvider;

/// <summary>
/// The claim builders behind the id_token and <c>/connect/userinfo</c>, and the parts of access
/// token minting the endpoint tests never reach (client claims, several audiences, a nonce).
/// <para>
/// No endpoint test grants a scope carrying <c>email</c>, <c>role</c> or <c>preferred_username</c>,
/// so every arm of both claim switches was untested — including the one that decides whether an
/// unconfirmed address is reported as verified.
/// </para>
/// </summary>
public class OidcTokenGeneratorTests : IDisposable
{
    private readonly string _keyPath = Path.Combine(Path.GetTempPath(), $"spark-oidc-gen-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_keyPath))
            File.Delete(_keyPath);
    }

    private OidcTokenGenerator NewGenerator()
        => new(OidcKeyRing.InMemory(new OidcSigningKeyService(
            new HostingEnvironment { EnvironmentName = Environments.Development, ContentRootPath = Path.GetTempPath() },
            _keyPath).GetSigningKey()));

    private static SparkUser User(string? email = "ada@example.test", bool confirmed = true, params string[] roles)
        => new()
        {
            Id = "users/1",
            UserName = "ada",
            Email = email,
            EmailConfirmed = confirmed,
            Roles = [.. roles],
        };

    private static OidcScopeDefinition Scope(string name, params string[] claimTypes)
        => new(name, name, OidcResourceKinds.Identity, null, null, [.. claimTypes], false, false, true);

    /// <summary>An API scope: its audience is the name of the API resource it belongs to.</summary>
    private static OidcScopeDefinition ApiScope(string resourceName, string name)
        => new(name, resourceName, OidcResourceKinds.Api, null, null, [], false, false, true);

    private static readonly OidcScopeDefinition[] EverythingScopes =
    [
        Scope("profile", "name", "preferred_username", "family_name"),
        // Mixed case and a duplicate on purpose: the lookup is case-insensitive and de-duplicated.
        Scope("email", "EMAIL", "email_verified", "email"),
        Scope("roles", "role"),
        // "sub" is emitted by the caller; a scope listing it must not produce a second one.
        Scope("openid", "sub"),
    ];

    [Fact]
    public void Id_token_claims_map_every_known_claim_type()
    {
        var claims = OidcTokenGenerator.ResolveUserClaims(User(roles: ["admin", "ops"]), EverythingScopes);

        claims.Select(c => (c.Type, c.Value)).Should().BeEquivalentTo(new[]
        {
            ("name", "ada"),
            ("preferred_username", "ada"),
            (JwtRegisteredClaimNames.Email, "ada@example.test"),
            ("email_verified", "true"),
            ("role", "admin"),
            ("role", "ops"),
        });
    }

    [Fact]
    public void Id_token_claims_omit_values_the_user_does_not_have()
    {
        var user = new SparkUser { Id = "users/2" };

        var claims = OidcTokenGenerator.ResolveUserClaims(user, EverythingScopes);

        claims.Should().BeEmpty("an absent value is omitted, never emitted as an empty claim");
    }

    [Fact]
    public void Id_token_reports_an_unconfirmed_address_as_unverified()
    {
        var claims = OidcTokenGenerator.ResolveUserClaims(User(confirmed: false), [Scope("email", "email_verified")]);

        claims.Should().ContainSingle();
        claims[0].Value.Should().Be("false");
    }

    [Fact]
    public void Userinfo_claims_map_every_known_claim_type()
    {
        var claims = OidcTokenGenerator.ResolveUserInfoClaims(User(roles: ["admin"]), EverythingScopes);

        claims["sub"].Should().Be("users/1");
        claims["name"].Should().Be("ada");
        claims["preferred_username"].Should().Be("ada");
        claims["email"].Should().Be("ada@example.test");
        claims["email_verified"].Should().Be(true);
        // "role", as in the id_token (#490 M6); it was "roles" here.
        ((List<string>)claims["role"]).Should().Equal("admin");
        claims.Should().NotContainKey("roles");
        claims.Should().NotContainKey("family_name");
    }

    [Fact]
    public void Name_parts_come_from_the_users_stored_claims_when_present()
    {
        var user = User();
        user.Claims.Add(new SparkUserClaim { ClaimType = "given_name", ClaimValue = "Ada" });
        user.Claims.Add(new SparkUserClaim { ClaimType = System.Security.Claims.ClaimTypes.Surname, ClaimValue = "Lovelace" });
        OidcScopeDefinition[] profile =[Scope("profile", "given_name", "family_name")];

        OidcTokenGenerator.ResolveUserClaims(user, profile).Select(c => (c.Type, c.Value))
            .Should().BeEquivalentTo(new[] { ("given_name", "Ada"), ("family_name", "Lovelace") });

        var info = OidcTokenGenerator.ResolveUserInfoClaims(user, profile);
        info["given_name"].Should().Be("Ada");
        info["family_name"].Should().Be("Lovelace");

        OidcTokenGenerator.ResolveUserClaims(User(), profile).Should().BeEmpty("a user without them gets no empty claim");
    }

    [Fact]
    public void An_id_token_carries_email_verified_as_a_json_boolean_and_role_as_role()
    {
        var token = NewGenerator().GenerateIdToken(
            User(roles: ["admin", "ops"]), new OidcApplication { ClientId = "webapp" }, "https://idp.test",
            [Scope("email", "email", "email_verified"), Scope("roles", "role")], nonce: null);

        var payload = PayloadOf(token);
        payload.GetProperty("email_verified").ValueKind.Should().Be(System.Text.Json.JsonValueKind.True);
        payload.GetProperty("role").EnumerateArray().Select(e => e.GetString()).Should().Equal("admin", "ops");
        payload.TryGetProperty("roles", out _).Should().BeFalse();
    }

    [Fact]
    public void An_id_token_carries_at_hash_and_auth_time_when_given()
    {
        var authTime = new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);
        const string accessToken = "header.payload.signature";

        var payload = PayloadOf(NewGenerator().GenerateIdToken(
            User(), new OidcApplication { ClientId = "webapp" }, "https://idp.test", [], nonce: null,
            accessToken: accessToken, authTime: authTime));

        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(accessToken));
        payload.GetProperty("at_hash").GetString().Should().Be(
            Microsoft.IdentityModel.Tokens.Base64UrlEncoder.Encode(hash[..16]));
        payload.GetProperty("auth_time").GetInt64().Should().Be(authTime.ToUnixTimeSeconds());
    }

    [Fact]
    public void An_id_token_without_an_access_token_or_sign_in_time_omits_both()
    {
        var payload = PayloadOf(NewGenerator().GenerateIdToken(
            User(), new OidcApplication { ClientId = "webapp" }, "https://idp.test", [], nonce: null));

        payload.TryGetProperty("at_hash", out _).Should().BeFalse();
        payload.TryGetProperty("auth_time", out _).Should().BeFalse();
    }

    [Fact]
    public void An_id_token_lives_for_its_own_lifetime_which_defaults_to_five_minutes()
    {
        new OidcApplication().IdTokenLifetimeMinutes.Should().Be(5);

        var payload = PayloadOf(NewGenerator().GenerateIdToken(
            User(), new OidcApplication { ClientId = "webapp" }, "https://idp.test", [], nonce: null, lifetimeMinutes: 7));

        (payload.GetProperty("exp").GetInt64() - payload.GetProperty("iat").GetInt64()).Should().Be(7 * 60);
    }

    private static System.Text.Json.JsonElement PayloadOf(string token)
        => System.Text.Json.JsonDocument.Parse(
            Microsoft.IdentityModel.Tokens.Base64UrlEncoder.Decode(token.Split('.')[1])).RootElement;

    [Fact]
    public void Userinfo_claims_carry_only_the_subject_for_an_empty_user()
    {
        var claims = OidcTokenGenerator.ResolveUserInfoClaims(new SparkUser { Id = "users/2" }, EverythingScopes);

        claims.Keys.Should().Equal("sub");
    }

    [Fact]
    public void An_id_token_carries_the_nonce_when_one_was_sent()
    {
        var token = NewGenerator().GenerateIdToken(
            User(), new OidcApplication { ClientId = "webapp" }, "https://idp.test", [], nonce: "n-0S6_WzA2Mj");

        var jwt = new JsonWebToken(token);
        jwt.GetClaim(JwtRegisteredClaimNames.Nonce).Value.Should().Be("n-0S6_WzA2Mj");
        jwt.Audiences.Should().Equal("webapp");
    }

    [Fact]
    public void A_machine_token_carries_the_client_claims_and_every_audience()
    {
        var app = new OidcApplication
        {
            ClientId = "machine",
            Claims = [new ClientClaim { Type = "group", Value = "Integrations" }],
        };
        // One audience per API resource now (its name), so several audiences take several APIs;
        // "API-A" is api-a spelled differently, to prove the merge is case-insensitive.
        var scopes = new[]
        {
            ApiScope("api-a", "api-a.read"),
            ApiScope("api-b", "api-b.read"),
            ApiScope("API-A", "API-A.write"),
            ApiScope("api-c", "api-c.write"),
        };

        var (token, jti) = NewGenerator().GenerateAccessToken(user: null, app, "https://idp.test", scopes);

        var jwt = new JsonWebToken(token);
        jwt.Id.Should().Be(jti);
        jwt.GetClaim("group").Value.Should().Be("Integrations");
        jwt.GetClaim("scope").Value.Should().Be("api-a.read api-b.read API-A.write api-c.write");
        // Merged case-insensitively, and none after the first may be dropped.
        jwt.Audiences.Order(StringComparer.Ordinal).Should().Equal("api-a", "api-b", "api-c");
        jwt.TryGetClaim(JwtRegisteredClaimNames.Sub, out _).Should().BeFalse("no user stands behind a machine token");
    }

    [Fact]
    public void A_delegated_token_never_carries_the_client_claims()
    {
        var app = new OidcApplication
        {
            ClientId = "webapp",
            Claims = [new ClientClaim { Type = "group", Value = "Administrators" }],
        };

        var (token, _) = NewGenerator().GenerateAccessToken(User(), app, "https://idp.test", []);

        var jwt = new JsonWebToken(token);
        jwt.TryGetClaim("group", out _).Should().BeFalse(
            "merging the client's groups into a user's token would promote every user who signs in through it");
        jwt.Subject.Should().Be("users/1");
    }
}
