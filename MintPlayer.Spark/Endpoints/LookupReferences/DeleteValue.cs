using Microsoft.AspNetCore.Antiforgery;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
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

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        var name = (string)httpContext.Request.RouteValues["name"]!;
        var key = (string)httpContext.Request.RouteValues["key"]!;

        try
        {
            await lookupReferenceService.DeleteValueAsync(name, key);
            return Results.NoContent();
        }
        catch (InvalidOperationException ex)
        {
            return Results.Json(new { error = ex.Message }, statusCode: 400);
        }
    }
}
