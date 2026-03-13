using Microsoft.AspNetCore.Http;
using MintPlayer.Spark.Authorization.Services;

namespace MintPlayer.Spark.Authorization.Endpoints;

/// <summary>
/// GET /spark/auth/external-providers
/// Returns the list of configured external OIDC providers.
/// </summary>
internal static class ExternalProviders
{
    public static IResult Handle(OidcProviderRegistry registry)
    {
        var providers = registry.GetAll()
            .Select(p => new
            {
                scheme = p.Scheme,
                displayName = p.Options.DisplayName,
                icon = p.Options.Icon,
            })
            .ToArray();

        return Results.Ok(providers);
    }
}
