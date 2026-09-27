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
        => new(new OidcSigningKeyService(
            new HostingEnvironment { EnvironmentName = Environments.Development, ContentRootPath = Path.GetTempPath() },
            _keyPath));

    private static SparkUser User(string? email = "ada@example.test", bool confirmed = true, params string[] roles)
        => new()
        {
            Id = "users/1",
            UserName = "ada",
            Email = email,
            EmailConfirmed = confirmed,
            Roles = [.. roles],
        };

    private static OidcScope Scope(string name, params string[] claimTypes)
        => new() { Name = name, ClaimTypes = [.. claimTypes] };

    private static readonly OidcScope[] EverythingScopes =
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
        ((List<string>)claims["roles"]).Should().Equal("admin");
        claims.Should().NotContainKey("family_name");
    }

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
        var scopes = new[]
        {
            new OidcScope { Name = "api.read", Audiences = ["api-a", "api-b"] },
            new OidcScope { Name = "api.write", Audiences = ["API-A", "api-c"] },
        };

        var (token, jti) = NewGenerator().GenerateAccessToken(user: null, app, "https://idp.test", scopes);

        var jwt = new JsonWebToken(token);
        jwt.Id.Should().Be(jti);
        jwt.GetClaim("group").Value.Should().Be("Integrations");
        jwt.GetClaim("scope").Value.Should().Be("api.read api.write");
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
