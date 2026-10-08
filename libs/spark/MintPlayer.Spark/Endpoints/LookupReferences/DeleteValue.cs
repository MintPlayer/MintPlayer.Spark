using Microsoft.AspNetCore.Antiforgery;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Endpoints.LookupReferences;

[MemberOf<LookupReferencesGroup>]
internal sealed partial class DeleteLookupReferenceValue : IDeleteEndpoint
{
    public static string Path => "/{name}/{key}";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
    {
        builder.WithMetadata(new RequireAntiforgeryTokenAttribute(true));
    }

    [RouteParam] public string Name { get; set; } = "";
    [RouteParam] public string Key { get; set; } = "";

    [Inject] private readonly ILookupReferenceService lookupReferenceService;
    [Inject] private readonly IPermissionService permissionService;
    [Inject] private readonly ILogger<DeleteLookupReferenceValue> logger;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        try
        {
            await permissionService.EnsureAuthorizedAsync("Edit", "LookupReferences"); // R2-H4

            await lookupReferenceService.DeleteValueAsync(Name, Key);
            return Results.NoContent();
        }
        catch (SparkAccessDeniedException)
        {
            return SparkDenial.RefuseJson(httpContext);
        }
        catch (InvalidOperationException ex)
        {
            // R2-M1: don't leak Raven-internal strings — log server-side.
            logger.LogWarning(ex, "DeleteLookupReferenceValue failed");
            return Results.Json(new { error = "Operation failed" }, statusCode: 400);
        }
    }
}
