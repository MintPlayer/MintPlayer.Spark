using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Endpoints.LookupReferences;

[MemberOf<LookupReferencesGroup>]
internal sealed partial class GetLookupReference : IGetEndpoint
{
    public static string Path => "/{name}";

    [Inject] private readonly ILookupReferenceService lookupReferenceService;
    [Inject] private readonly IPermissionService permissionService;
    [Inject] private readonly IModelLoader modelLoader;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        var name = (string)httpContext.Request.RouteValues["name"]!;

        // Security sweep M4: this endpoint dumps every value of a lookup reference — for
        // transient lookups, every public property an app hung off its lookup class. It was
        // unauthenticated (Spark endpoints are anonymous at the ASP.NET layer and gate inside
        // the handler; this one didn't). Gate reads behind Read/LookupReferences, the read
        // counterpart to the Edit/LookupReferences the mutating siblings already require —
        // or behind Read on a type that binds this lookup (#453).
        if (!await permissionService.IsAllowedAsync("Read", "LookupReferences")
            && !await IsBoundToReadableTypeAsync(name))
        {
            return SparkDenial.RefuseJson(httpContext);
        }

        var lookupReference = await lookupReferenceService.GetAsync(name);

        if (lookupReference == null)
        {
            return Results.Json(new { error = $"LookupReference '{name}' not found" }, statusCode: 404);
        }

        return Results.Json(lookupReference);
    }

    /// <summary>
    /// Whether the caller may Read some entity type with an attribute bound to this lookup (#453).
    /// </summary>
    /// <remarks>
    /// A persistent object the caller may read has to be renderable, and its lookup-bound
    /// attributes need their labels: without this, an anonymous visitor on a public page got a 401
    /// from the lookup alone and was bounced to sign-in. Scoped to the lookups the readable model
    /// actually uses, so it is <em>not</em> a wholesale anonymous <c>Read/LookupReferences</c> —
    /// a lookup no readable type binds (e.g. a future dynamic one) stays behind the right.
    /// </remarks>
    private async Task<bool> IsBoundToReadableTypeAsync(string name)
    {
        foreach (var entityType in modelLoader.GetEntityTypes())
        {
            if (!entityType.Attributes.Any(a => string.Equals(a.LookupReferenceType, name, StringComparison.OrdinalIgnoreCase)))
                continue;

            if (await permissionService.IsAllowedAsync("Read", entityType.Name))
                return true;
        }

        return false;
    }
}
