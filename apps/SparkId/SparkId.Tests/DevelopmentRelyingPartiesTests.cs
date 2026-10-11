using MintPlayer.Spark.IdentityProvider.Models;
using MintPlayer.Spark.IdentityProvider.Services;
using MintPlayer.Spark.Testing;
using Raven.Client.Documents;
using SparkId.Migrations;

namespace SparkId.Tests;

/// <summary>
/// The development relying parties (QnA, HR, Fleet, and SeoTester since #268 M7), seeded through one helper. The
/// shape is what Spark's <c>SparkId</c> OpenID Connect preset sends, compared ordinally by the provider, so a drift
/// breaks every demo's local sign-in.
/// </summary>
public class DevelopmentRelyingPartiesTests : SparkTestDriver
{
    [Fact]
    public async Task A_relying_party_is_a_confidential_PKCE_client_with_the_presets_exact_redirects_and_a_hashed_secret()
    {
        using (var session = Store.OpenAsyncSession())
        {
            await DevelopmentRelyingParties.EnsureAsync(session, M_202610101200_SeoTesterRelyingParty.ClientId, "SeoTester",
                M_202610101200_SeoTesterRelyingParty.Origin, M_202610101200_SeoTesterRelyingParty.Secret, default);
            await session.SaveChangesAsync();
        }

        var app = await LoadAsync("seotester");
        app.DisplayName.Should().Be("SeoTester");
        app.ClientType.Should().Be("confidential");
        app.Enabled.Should().BeTrue();
        app.RedirectUris.Should().Equal("https://localhost:5013/signin-SparkId");
        app.PostLogoutRedirectUris.Should().Equal("https://localhost:5013/signout-callback-SparkId");
        app.AllowedGrantTypes.Should().Equal("authorization_code");
        app.RequirePkce.Should().BeTrue();
        app.ConsentType.Should().Be("implicit");
        app.Mode.Should().Be(OidcApplicationModes.Live);
        app.Scopes.Select(s => (s.Name, s.Required)).Should().Equal(("openid", true), ("profile", true), ("email", false));
        app.Secrets.Should().ContainSingle();
        app.Secrets[0].Hash.Should().NotBe(M_202610101200_SeoTesterRelyingParty.Secret, "secrets are stored hashed");
        ClientSecretHasher.Verify(M_202610101200_SeoTesterRelyingParty.Secret, app.Secrets[0].Hash!).Should().BeTrue();
    }

    [Fact]
    public async Task An_existing_client_is_left_as_its_administrator_edited_it()
    {
        foreach (var rp in M_202610081200_RelyingParties.RelyingParties)
        {
            using var session = Store.OpenAsyncSession();
            await DevelopmentRelyingParties.EnsureAsync(session, rp.ClientId, rp.DisplayName, rp.Origin, rp.Secret, default);
            await session.SaveChangesAsync();
        }
        using (var session = Store.OpenAsyncSession())
        {
            var qna = await session.Query<OidcApplication>().Customize(c => c.WaitForNonStaleResults()).Where(a => a.ClientId == "qna").SingleAsync();
            qna.DisplayName = "Edited by an administrator";
            await session.SaveChangesAsync();
        }

        using (var session = Store.OpenAsyncSession())
        {
            await DevelopmentRelyingParties.EnsureAsync(session, "qna", "Spark QnA", "https://localhost:5009", "dev-only-qna-secret-not-for-production", default);
            await session.SaveChangesAsync();
        }

        using var check = Store.OpenAsyncSession();
        var all = await check.Query<OidcApplication>().Customize(c => c.WaitForNonStaleResults()).ToListAsync();
        all.Select(a => a.ClientId).OrderBy(c => c).Should().Equal("fleet", "hr", "qna");
        all.Single(a => a.ClientId == "qna").DisplayName.Should().Be("Edited by an administrator");
        all.Single(a => a.ClientId == "hr").RedirectUris.Should().Equal("https://localhost:5005/signin-SparkId");
    }

    private async Task<OidcApplication> LoadAsync(string clientId)
    {
        using var session = Store.OpenAsyncSession();
        return await session.Query<OidcApplication>().Customize(c => c.WaitForNonStaleResults()).Where(a => a.ClientId == clientId).SingleAsync();
    }
}
