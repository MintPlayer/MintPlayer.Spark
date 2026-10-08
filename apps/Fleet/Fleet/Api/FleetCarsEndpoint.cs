using Fleet.Entities;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Authorization.ResourceServer;
using Raven.Client.Documents;

namespace Fleet.Api;

/// <summary>
/// <c>GET /api/fleet/cars</c>: the resource-server demo (<c>docs/identity_provider_platform_PRD.md</c> I12). A plain
/// API outside Spark's PersistentObject endpoints, answering only a SparkId access token for this audience that
/// carries <c>fleet.read</c>. Mapped only when Fleet trusts an issuer (<c>Spark:JwtBearer:Authority</c>).
/// </summary>
/// <remarks>
/// The scope is the whole authorization here: the raw session reads past <c>security.json</c>, so it returns only
/// non-personal fields.
/// </remarks>
internal sealed partial class FleetCarsEndpoint : IGetEndpoint
{
    public static string Path => "/api/fleet/cars";

    static bool IEndpointBase.IsEnabled(IServiceProvider services)
        => !string.IsNullOrWhiteSpace(services.GetRequiredService<IConfiguration>()["Spark:JwtBearer:Authority"]);

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.RequireScope("fleet.read");

    [Inject] private readonly IDocumentStore store;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        using var session = store.OpenAsyncSession();
        var cars = await session.Query<Car>()
            .Select(c => new { c.LicensePlate, c.Model, c.Year })
            .Take(100)
            .ToListAsync(httpContext.RequestAborted);
        return Results.Ok(cars);
    }
}
