using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.Spark.Authorization.Configuration;
using MintPlayer.Spark.Authorization.Extensions;

namespace MintPlayer.Spark.Authorization.Endpoints;

/// <summary>
/// Tells the client which sign-in methods this application actually offers: how much of the
/// local-credential surface is mounted, and which external providers are registered.
/// </summary>
/// <remarks>
/// <para>
/// Anonymous by design — it describes exactly what an unauthenticated visitor is about to be shown
/// on a sign-in page, so it discloses nothing that page would not.
/// </para>
/// <para>
/// The local-credential mode is <em>derived from the route table</em> rather than read back from the
/// options object. Reporting the configured value would let this endpoint claim a surface that was
/// never mapped (or deny one that was); deriving it from the endpoints that exist means the answer
/// is true by construction, and stays true if the mapping is ever reached by some other path.
/// </para>
/// </remarks>
internal sealed class GetAuthCapabilities : IGetEndpoint, IMemberOf<SparkAuthGroup>
{
    public static string Path => "/capabilities";

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        var services = httpContext.RequestServices;
        var mapped = services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Select(endpoint => endpoint.RoutePattern.RawText)
            .Where(pattern => pattern is not null)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var localCredentials =
            !mapped.Contains("/spark/auth/login") ? SparkLocalCredentials.Disabled
            : !mapped.Contains("/spark/auth/register") ? SparkLocalCredentials.SignInOnly
            : SparkLocalCredentials.Full;

        var providers = await ExternalAuthenticationSchemes.GetInteractiveAsync(services);

        return Results.Ok(new
        {
            localCredentials = localCredentials.ToString(),
            externalProviders = providers
                .Select(scheme => new { scheme = scheme.Name, displayName = scheme.DisplayName })
                .ToArray(),
        });
    }
}
