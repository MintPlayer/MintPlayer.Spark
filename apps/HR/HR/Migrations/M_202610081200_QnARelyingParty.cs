using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.IdentityProvider.Models;
using MintPlayer.Spark.IdentityProvider.Services;
using MintPlayer.Spark.Migrations;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;

namespace HR.Migrations;

/// <summary>
/// Development only: seeds the standard <c>profile</c> and <c>email</c> scopes (and <c>openid</c>) and
/// registers QnA (<c>https://localhost:5009</c>) as a relying party, so QnA can sign in against HR
/// (#490 M6, D7). Outside Development nothing is written.
/// </summary>
/// <remarks>
/// <para>
/// The redirect URIs are exact matches of what the stock OpenIdConnect handler sends for Spark's
/// preset scheme <c>HR</c>: <c>CallbackPath = /signin-HR</c> and
/// <c>SignedOutCallbackPath = /signout-callback-HR</c>. The provider compares them ordinally, so the
/// casing of the scheme name matters.
/// </para>
/// <para>
/// ⚠️ <see cref="DevelopmentClientSecret"/> is a published, development-only value — QnA's
/// <c>appsettings.Development.json</c> carries the same string. It is stored hashed, as every client
/// secret is. A real deployment registers its own client with a generated secret.
/// </para>
/// <para>
/// A scope or client that already exists (by name or client id) is left alone, so an administrator's
/// edits survive.
/// </para>
/// </remarks>
public partial class M_202610081200_QnARelyingParty : ISparkMigration
{
    public static long Version => 202610081200;
    public static string? Description => "Development: seed openid/profile/email scopes and the QnA relying party";

    /// <summary>The development-only shared secret of the <c>qna</c> client. Not a credential.</summary>
    internal const string DevelopmentClientSecret = "dev-only-qna-secret-not-for-production";

    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly IHostEnvironment environment;

    public async Task UpAsync(CancellationToken cancellationToken)
    {
        if (!environment.IsDevelopment())
            return;

        await EnsureScopeAsync("openid", "Your identity", ["sub"], required: true, cancellationToken);
        await EnsureScopeAsync("profile", "Your profile", ["name", "preferred_username", "given_name", "family_name"], required: false, cancellationToken);
        await EnsureScopeAsync("email", "Your email address", ["email", "email_verified"], required: false, cancellationToken);

        var existing = await session.Query<OidcApplication>()
            .Where(a => a.ClientId == "qna", exact: true)
            .AnyAsync(cancellationToken);
        if (!existing)
        {
            await session.StoreAsync(new OidcApplication
            {
                ClientId = "qna",
                DisplayName = "Spark QnA",
                ClientType = "confidential",
                Enabled = true,
                Secrets =
                [
                    new ClientSecret
                    {
                        Hash = ClientSecretHasher.Hash(DevelopmentClientSecret),
                        Description = "Development only",
                        CreatedAt = DateTime.UtcNow,
                    },
                ],
                AllowedGrantTypes = ["authorization_code"],
                RedirectUris = ["https://localhost:5009/signin-HR"],
                PostLogoutRedirectUris = ["https://localhost:5009/signout-callback-HR"],
                AllowedScopes = ["openid", "profile", "email"],
                ConsentType = "implicit",
                RequirePkce = true,
            }, cancellationToken);
        }

        await session.SaveChangesAsync(cancellationToken);
    }

    private async Task EnsureScopeAsync(string name, string displayName, List<string> claimTypes, bool required, CancellationToken ct)
    {
        var exists = await session.Query<OidcScope>()
            .Where(s => s.Name == name, exact: true)
            .AnyAsync(ct);
        if (exists)
            return;

        await session.StoreAsync(new OidcScope
        {
            Name = name,
            DisplayName = displayName,
            ClaimTypes = claimTypes,
            Required = required,
            ShowInDiscoveryDocument = true,
            Enabled = true,
        }, ct);
    }
}
