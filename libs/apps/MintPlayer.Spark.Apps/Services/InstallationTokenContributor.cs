using Microsoft.Extensions.Options;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Apps.Models;
using MintPlayer.Spark.IdentityProvider.Models;
using MintPlayer.Spark.IdentityProvider.Services;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Apps.Services;

/// <summary>
/// The installation token (D16, §4.3 flow 7): an App posts <c>grant_type=client_credentials&amp;installation_id=in_…</c>
/// with a <c>private_key_jwt</c> assertion, and gets a token whose scopes are what that installation granted. Requests
/// without <c>installation_id</c> are not this contributor's.
/// </summary>
internal sealed partial class InstallationTokenContributor : IOidcClientCredentialsContributor
{
    public const string InstallationIdParameter = "installation_id";
    public const string InstallationIdClaim = "installation_id";
    public const string TargetKindClaim = "installation_target_kind";
    public const string TargetClaim = "installation_target";
    public const string AppClaim = "spark_app";

    [Inject] private readonly IDocumentStore store;
    [Inject] private readonly IOptions<SparkAppsOptions> options;

    public async Task<OidcClientCredentialsContribution?> ContributeAsync(OidcClientCredentialsRequest request, CancellationToken cancellationToken)
    {
        if (!request.Parameters.TryGetValue(InstallationIdParameter, out var id))
            return null;
        if (request.AuthenticationMethod != OidcClientAuthMethods.PrivateKeyJwt)
            return OidcClientCredentialsContribution.Refuse("unauthorized_client", "An installation token needs a private_key_jwt client assertion.");

        // Every refusal of the installation itself reads the same: an App learns nothing about installations not its own.
        var unknown = OidcClientCredentialsContribution.Refuse("invalid_grant", "Unknown, suspended or removed installation.");
        if (!AppInstallationManager.IsInstallationId(id))
            return unknown;
        using var session = store.OpenAsyncSession();
        var installation = await session.LoadAsync<AppInstallation>(AppInstallation.IdFor(id).Value, cancellationToken);
        if (installation is null || installation.Suspended)
            return unknown;
        var app = await session.LoadAsync<SparkApp>(SparkApp.IdFor(installation.AppSlug).Value, cancellationToken);
        // Ignoring case: the stored id keeps the casing it was written in, "OidcApplications/…" before #536 (D7).
        if (app is null || !string.Equals(app.OidcApplicationId, request.Application.Id.Value, StringComparison.OrdinalIgnoreCase))
            return unknown;

        // The requested scopes narrow the token; asking for one the installation did not grant is refused, not dropped.
        var granted = installation.Permissions.Where(p => app.Permissions.Contains(p, StringComparer.Ordinal)).ToList();
        if (request.RequestedScopes.FirstOrDefault(s => !SparkAppManager.Covers(granted, s)) is { } refused)
            return OidcClientCredentialsContribution.Refuse("invalid_scope", $"The installation did not grant '{refused}'.");
        var scopes = request.RequestedScopes.Count > 0 ? request.RequestedScopes.Distinct(StringComparer.Ordinal).ToList() : granted;

        return new OidcClientCredentialsContribution
        {
            Scopes = scopes,
            Audience = options.Value.InstallationTokenAudience,
            LifetimeMinutes = options.Value.InstallationTokenLifetimeMinutes,
            AuthorizationId = installation.GrantId,
            Subject = AppInstallationManager.Subject(installation),
            Claims = new Dictionary<string, string>
            {
                [InstallationIdClaim] = installation.PublicId,
                [TargetKindClaim] = installation.TargetKind,
                [TargetClaim] = installation.TargetKey,
                [AppClaim] = app.Slug,
            },
        };
    }
}
