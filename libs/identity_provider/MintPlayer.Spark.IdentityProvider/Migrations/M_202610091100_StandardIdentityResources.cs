using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.IdentityProvider.Models;
using MintPlayer.Spark.IdentityProvider.Services;
using MintPlayer.Spark.Migrations;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.IdentityProvider.Migrations;

/// <summary>
/// The standard identity resources every OpenID provider needs: <c>openid</c>, <c>profile</c>, <c>email</c>
/// (<c>docs/identity_provider_platform_PRD.md</c> D1). In every environment: without <c>openid</c> no sign-in
/// works ("Scope 'openid' is not available"), and they used to exist only where an application's own
/// Development seed created them (found by the I13 E2E journey).
/// </summary>
/// <remarks>
/// A resource that already exists (natural id <c>OidcResources/&lt;name&gt;</c>) is left alone, so an
/// administrator's edits, and an application's own seed, survive.
/// </remarks>
public partial class M_202610091100_StandardIdentityResources : ISparkMigration
{
    public static long Version => 202610091100;
    public static string? Description => "Identity provider: ensure the standard openid, profile and email identity resources";

    [Inject] private readonly IAsyncDocumentSession session;

    public async Task UpAsync(CancellationToken cancellationToken)
    {
        await EnsureAsync("openid", TranslatedString.Create("Your identity", "Votre identité", "Je identiteit"), ["sub"], required: true, cancellationToken);
        await EnsureAsync("profile", TranslatedString.Create("Your profile", "Votre profil", "Je profiel"), ["name", "preferred_username", "given_name", "family_name"], required: false, cancellationToken);
        await EnsureAsync("email", TranslatedString.Create("Your email address", "Votre adresse e-mail", "Je e-mailadres"), ["email", "email_verified"], required: false, cancellationToken);
        await session.SaveChangesAsync(cancellationToken);
    }

    private async Task EnsureAsync(string name, TranslatedString displayName, List<string> claimTypes, bool required, CancellationToken ct)
    {
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
