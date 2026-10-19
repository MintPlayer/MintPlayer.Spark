using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Endpoints.LookupReferences;

[MemberOf<LookupReferencesGroup>]
internal sealed partial class ListLookupReferences : IGetEndpoint
{
    public static string Path => "/";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.RefuseDeniedAsJson();

    [Inject] private readonly ILookupReferenceService lookupReferenceService;
    [Inject] private readonly IPermissionService permissionService;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        // Security sweep M4: enumerating every lookup reference (names + value counts) was
        // unauthenticated. Gate behind Read/LookupReferences like GetLookupReference; a denial is
        // answered by the endpoint filter (Configure).
        await permissionService.EnsureAuthorizedAsync("Read", "LookupReferences");

        var lookupReferences = await lookupReferenceService.GetAllAsync();
        return Results.Json(lookupReferences);
    }
}
