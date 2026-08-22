using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Endpoints.LookupReferences;

internal sealed partial class ListLookupReferences : IGetEndpoint, IMemberOf<LookupReferencesGroup>
{
    public static string Path => "/";

    [Inject] private readonly ILookupReferenceService lookupReferenceService;
    [Inject] private readonly IPermissionService permissionService;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        try
        {
            // Security sweep M4: enumerating every lookup reference (names + value counts) was
            // unauthenticated. Gate behind Read/LookupReferences like GetLookupReference.
            await permissionService.EnsureAuthorizedAsync("Read", "LookupReferences");
        }
        catch (SparkAccessDeniedException)
        {
            return SparkDenial.RefuseJson(httpContext);
        }

        var lookupReferences = await lookupReferenceService.GetAllAsync();
        return Results.Json(lookupReferences);
    }
}
