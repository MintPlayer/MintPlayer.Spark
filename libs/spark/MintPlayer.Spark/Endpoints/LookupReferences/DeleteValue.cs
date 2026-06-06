using Microsoft.AspNetCore.Antiforgery;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Endpoints.LookupReferences;

internal sealed partial class DeleteLookupReferenceValue : IDeleteEndpoint, IMemberOf<LookupReferencesGroup>
{
    public static string Path => "/{name}/{key}";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder)
    {
        builder.WithMetadata(new RequireAntiforgeryTokenAttribute(true));
    }

    [Inject] private readonly ILookupReferenceService lookupReferenceService;
    [Inject] private readonly IPermissionService permissionService;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        var name = (string)httpContext.Request.RouteValues["name"]!;
        var key = (string)httpContext.Request.RouteValues["key"]!;

        try
        {
            await permissionService.EnsureAuthorizedAsync("Edit", "LookupReferences"); // R2-H4

            await lookupReferenceService.DeleteValueAsync(name, key);
            return Results.NoContent();
        }
        catch (SparkAccessDeniedException)
        {
            var isAuthed = httpContext.User.Identity?.IsAuthenticated == true;
            return Results.Json(
                new { error = isAuthed ? "Access denied" : "Authentication required" },
                statusCode: isAuthed ? 403 : 401);
        }
        catch (InvalidOperationException ex)
        {
            // R2-M1: don't leak Raven-internal strings — log server-side.
            httpContext.RequestServices.GetService<ILoggerFactory>()
                ?.CreateLogger("SparkLookupReferences")
                ?.LogWarning(ex, "DeleteLookupReferenceValue failed");
            return Results.Json(new { error = "Operation failed" }, statusCode: 400);
        }
    }
}
