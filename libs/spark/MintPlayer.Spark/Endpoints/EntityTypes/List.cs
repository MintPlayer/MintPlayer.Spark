using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Abstractions.Prerendering;

namespace MintPlayer.Spark.Endpoints.EntityTypes;

[MemberOf<EntityTypesGroup>]
internal sealed partial class ListEntityTypes : IGetEndpoint
{
    public static string Path => "/";

    // A read: server-side rendering may supply its response (SparkPrerenderableAttribute).
    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.WithMetadata(SparkPrerenderableAttribute.Instance);

    // The filtering and per-caller shaping live in the catalogue, shared with the MCP tools (MCP PRD D9).
    [Inject] private readonly ISparkCatalogue catalogue;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
        => Results.Json(await catalogue.ListEntityTypesAsync(httpContext.RequestAborted));
}
