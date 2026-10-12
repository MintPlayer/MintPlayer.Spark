using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Endpoints.Queries;

[MemberOf<QueriesGroup>]
internal sealed partial class ListQueries : IGetEndpoint
{
    public static string Path => "/";

    // The filtering lives in the catalogue, shared with the MCP tools (MCP PRD D9).
    [Inject] private readonly ISparkCatalogue catalogue;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
        => Results.Json(await catalogue.ListQueriesAsync(httpContext.RequestAborted));
}
