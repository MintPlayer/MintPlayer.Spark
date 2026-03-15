using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Endpoints.LookupReferences;

internal sealed partial class GetLookupReference : IGetEndpoint, IMemberOf<LookupReferencesGroup>
{
    public static string Path => "/{name}";

    [Inject] private readonly ILookupReferenceService lookupReferenceService;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        var name = (string)httpContext.Request.RouteValues["name"]!;

        var lookupReference = await lookupReferenceService.GetAsync(name);

        if (lookupReference == null)
        {
            return Results.Json(new { error = $"LookupReference '{name}' not found" }, statusCode: 404);
        }

        return Results.Json(lookupReference);
    }
}
