using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Endpoints.EntityTypes;

[MemberOf<EntityTypesGroup>]
internal sealed partial class ListEntityTypes : IGetEndpoint
{
    public static string Path => "/";

    // The filtering and per-caller shaping live in the catalogue, shared with the MCP tools (MCP PRD D9).
    [Inject] private readonly ISparkCatalogue catalogue;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
        => Results.Json(await catalogue.ListEntityTypesAsync(httpContext.RequestAborted));
}
