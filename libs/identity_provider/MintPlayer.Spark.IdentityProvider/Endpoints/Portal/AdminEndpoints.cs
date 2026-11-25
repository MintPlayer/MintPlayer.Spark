using Microsoft.AspNetCore.Antiforgery;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.IdentityProvider.Models;
using MintPlayer.Spark.IdentityProvider.Services;

namespace MintPlayer.Spark.IdentityProvider.Endpoints.Portal;

/// <summary>One signing key as the management page lists it: never its private material.</summary>
public sealed record OidcKeySummary(string Kid, string Algorithm, string State, DateTime CreatedAt, DateTime? ActivatedAt, DateTime? RetiredAt);

/// <summary>
/// The signing keys, for the management page (<c>docs/identity_provider_platform_PRD.md</c> D9). Identity-provider
/// administrators only (<c>ManageAll/IdentityProvider</c>).
/// </summary>
[MemberOf<IdentityProviderPortalGroup>]
internal sealed partial class ListSigningKeys : IGetEndpoint
{
    public static string Path => "/admin/keys";

    static bool IEndpointBase.IsEnabled(IServiceProvider services) => OidcSurface.Of(services).Administration;

    [Inject] private readonly OidcPortalAccess access;
    [Inject] private readonly ISparkSession session;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        if (!await access.IsAdministratorAsync())
            return TypedResults.Forbid();

        // A scan, sorted here (#536 D47): the ring is a handful of documents, and on RavenDB an ordered collection query
        // builds an auto index (D42).
        var keys = new List<OidcKeySummary>();
        await foreach (var row in session.StreamAsync(session.Query<OidcKey>(), httpContext.RequestAborted))
            if (row.Document is { } k)
                keys.Add(new OidcKeySummary(k.Kid, k.Algorithm, k.State, k.CreatedAt, k.ActivatedAt, k.RetiredAt));
        return TypedResults.Ok(keys.OrderByDescending(k => k.CreatedAt).ToList());
    }
}

/// <summary>
/// Rotates the signing keys now (D9): a new key signs at once and the active one retires, staying published for
/// <c>Keys:RetainRetiredDays</c> so tokens it signed keep validating. For a suspected key compromise, or to test
/// that relying parties follow the JWKS. Identity-provider administrators only.
/// </summary>
[MemberOf<IdentityProviderPortalGroup>]
internal sealed partial class RotateSigningKeys : IPostEndpoint
{
    public static string Path => "/admin/keys/rotate";

    static bool IEndpointBase.IsEnabled(IServiceProvider services) => OidcSurface.Of(services).Administration;

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.WithMetadata(new RequireAntiforgeryTokenAttribute(true));

    [Inject] private readonly OidcPortalAccess access;
    [Inject] private readonly OidcKeyRing keyRing;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        if (!await access.IsAdministratorAsync())
            return TypedResults.Forbid();

        var changes = await keyRing.RotateAsync(force: true, httpContext.RequestAborted);
        return TypedResults.Ok(new { changes });
    }
}
