using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Abstractions.Prerendering;

namespace MintPlayer.Spark.Endpoints.LookupReferences;

[MemberOf<LookupReferencesGroup>]
internal sealed partial class ListLookupReferences : IGetEndpoint
{
    public static string Path => "/";

    // A read: server-side rendering may supply its response (SparkPrerenderableAttribute).
    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.WithMetadata(SparkPrerenderableAttribute.Instance);

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
