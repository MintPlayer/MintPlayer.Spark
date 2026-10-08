using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.IdentityProvider.Models;
using MintPlayer.Spark.IdentityProvider.Services;
using MintPlayer.Spark.Migrations;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;

namespace SparkId.Migrations;

/// <summary>
/// Development only: seeds the standard <c>openid</c>, <c>profile</c> and <c>email</c> scopes and
/// registers the three demo apps as relying parties, so QnA, HR and Fleet sign in against SparkId
/// (#490 M6, <c>docs/identity_provider_platform_PRD.md</c> R1). Outside Development nothing is written.
/// </summary>
/// <remarks>
/// <para>
/// The redirect URIs are exact matches of what the stock OpenIdConnect handler sends for Spark's
/// preset scheme <c>SparkId</c>: <c>CallbackPath = /signin-SparkId</c> and
/// <c>SignedOutCallbackPath = /signout-callback-SparkId</c>. The provider compares them ordinally, so
/// the casing of the scheme name matters.
/// </para>
/// <para>
/// ⚠️ The secrets are published, development-only values. Each relying party's
/// <c>appsettings.Development.json</c> carries the same string. They are stored hashed, as every
/// client secret is. A real deployment registers its own clients with generated secrets.
/// </para>
/// <para>
/// A scope or client that already exists (by name or client id) is left alone, so an administrator's
/// edits survive.
/// </para>
/// </remarks>
public partial class M_202610081200_RelyingParties : ISparkMigration
{
    public static long Version => 202610081200;
    public static string? Description => "Development: seed openid/profile/email scopes and the QnA, HR and Fleet relying parties";

    /// <summary>The demo relying parties: client id, display name, https origin, development-only secret.</summary>
    internal static readonly (string ClientId, string DisplayName, string Origin, string Secret)[] RelyingParties =
    [
        ("qna", "Spark QnA", "https://localhost:5009", "dev-only-qna-secret-not-for-production"),
        ("hr", "Spark HR", "https://localhost:5005", "dev-only-hr-secret-not-for-production"),
        ("fleet", "Spark Fleet", "https://localhost:5003", "dev-only-fleet-secret-not-for-production"),
    ];

    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly IHostEnvironment environment;

    public async Task UpAsync(CancellationToken cancellationToken)
    {
        if (!environment.IsDevelopment())
            return;

        await EnsureScopeAsync("openid", TranslatedString.Create("Your identity", "Votre identité", "Je identiteit"), ["sub"], required: true, cancellationToken);
        await EnsureScopeAsync("profile", TranslatedString.Create("Your profile", "Votre profil", "Je profiel"), ["name", "preferred_username", "given_name", "family_name"], required: false, cancellationToken);
        await EnsureScopeAsync("email", TranslatedString.Create("Your email address", "Votre adresse e-mail", "Je e-mailadres"), ["email", "email_verified"], required: false, cancellationToken);

        foreach (var rp in RelyingParties)
            await EnsureRelyingPartyAsync(rp.ClientId, rp.DisplayName, rp.Origin, rp.Secret, cancellationToken);

        await session.SaveChangesAsync(cancellationToken);
    }

    private async Task EnsureRelyingPartyAsync(string clientId, string displayName, string origin, string secret, CancellationToken ct)
    {
        var exists = await session.Query<OidcApplication>()
            .Where(a => a.ClientId == clientId, exact: true)
            .AnyAsync(ct);
        if (exists)
            return;

        await session.StoreAsync(new OidcApplication
        {
            ClientId = clientId,
            DisplayName = displayName,
            ClientType = "confidential",
            Enabled = true,
            Secrets =
            [
                new ClientSecret
                {
                    Hash = ClientSecretHasher.Hash(secret),
                    Description = "Development only",
                    CreatedAt = DateTime.UtcNow,
                },
            ],
            AllowedGrantTypes = ["authorization_code"],
            RedirectUris = [$"{origin}/signin-SparkId"],
            PostLogoutRedirectUris = [$"{origin}/signout-callback-SparkId"],
            // D6: openid and profile are required, email is optional (the user may untick it).
            Scopes =
            [
                new OidcApplicationScope { Name = "openid", Required = true },
                new OidcApplicationScope { Name = "profile", Required = true },
                new OidcApplicationScope { Name = "email", Required = false },
            ],
            // The seeds are the demo's trusted first-party apps: Live, no members, consent implicit.
            Mode = OidcApplicationModes.Live,
            ConsentType = "implicit",
            RequirePkce = true,
        }, ct);
    }

    private async Task EnsureScopeAsync(string name, TranslatedString displayName, List<string> claimTypes, bool required, CancellationToken ct)
    {
        // Natural id (OidcScopeCatalog): a point-load decides, no index involved.
        var id = OidcScopeCatalog.ResourceId(name);
        if (await session.Advanced.ExistsAsync(id, ct))
            return;

        await session.StoreAsync(new OidcResource
        {
            Id = id,
            Kind = OidcResourceKinds.Identity,
            Name = name,
            DisplayName = displayName,
            ClaimTypes = claimTypes,
            Required = required,
            ShowInDiscoveryDocument = true,
            Enabled = true,
        }, ct);
    }
}
