using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Endpoints.LookupReferences;

internal sealed partial class ListLookupReferences : IGetEndpoint, IMemberOf<LookupReferencesGroup>
{
    public static string Path => "/";

    [Inject] private readonly ILookupReferenceService lookupReferenceService;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        var lookupReferences = await lookupReferenceService.GetAllAsync();
        return Results.Json(lookupReferences);
    }
}
