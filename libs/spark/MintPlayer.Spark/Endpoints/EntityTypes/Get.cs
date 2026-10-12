using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Endpoints.EntityTypes;

[MemberOf<EntityTypesGroup>]
internal sealed partial class GetEntityType : IGetEndpoint
{
    public static string Path => "/{id}";

    [RouteParam] public string Id { get; set; } = "";

    /// <summary><c>?for=new|edit|read</c>: which form the type is shaped for (#264).</summary>
    [QueryParam("for")] public string? Purpose { get; set; }

    // The check and the per-caller shaping live in the catalogue, shared with the MCP tools (MCP PRD D9).
    [Inject] private readonly ISparkCatalogue catalogue;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        var id = Id;

        // 404 rather than 403 for a type the caller may not query — so existence isn't leaked: the
        // catalogue answers both with null. `?for=new` shapes it for the create form (#264, G1/G2).
        var shaped = await catalogue.GetEntityTypeAsync(id, FormVerb(Purpose ?? string.Empty), httpContext.RequestAborted);
        return shaped is null
            ? Results.Json(new { error = $"Entity type '{id}' not found" }, statusCode: 404)
            : Results.Json(shaped);
    }

    /// <summary><c>new</c>, <c>edit</c> or <c>read</c>; anything else is the edit shape, the default.</summary>
    private static string FormVerb(string purpose) => purpose.ToLowerInvariant() switch
    {
        "new" => SparkCoreActions.New,
        "read" => SparkCoreActions.Read,
        _ => SparkCoreActions.Edit,
    };
}
