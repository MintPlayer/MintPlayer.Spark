using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Endpoints.LookupReferences;

internal sealed partial class GetLookupReference : IGetEndpoint, IMemberOf<LookupReferencesGroup>
{
    public static string Path => "/{name}";

    [Inject] private readonly ILookupReferenceService lookupReferenceService;
    [Inject] private readonly IPermissionService permissionService;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        var name = (string)httpContext.Request.RouteValues["name"]!;

        try
        {
            // Security sweep M4: this endpoint dumps every value of a lookup reference — for
            // transient lookups, every public property an app hung off its lookup class. It was
            // unauthenticated (Spark endpoints are anonymous at the ASP.NET layer and gate inside
            // the handler; this one didn't). Gate reads behind Read/LookupReferences, the read
            // counterpart to the Edit/LookupReferences the mutating siblings already require.
            await permissionService.EnsureAuthorizedAsync("Read", "LookupReferences");
        }
        catch (SparkAccessDeniedException)
        {
            var isAuthed = httpContext.User.Identity?.IsAuthenticated == true;
            return Results.Json(
                new { error = isAuthed ? "Access denied" : "Authentication required" },
                statusCode: isAuthed ? 403 : 401);
        }

        var lookupReference = await lookupReferenceService.GetAsync(name);

        if (lookupReference == null)
        {
            return Results.Json(new { error = $"LookupReference '{name}' not found" }, statusCode: 404);
        }

        return Results.Json(lookupReference);
    }
}
