using Microsoft.AspNetCore.Antiforgery;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Endpoints.LookupReferences;

[Register(ServiceLifetime.Scoped)]
public sealed partial class DeleteLookupReferenceValue : IEndpoint
{
    public static void MapRoutes(IEndpointRouteBuilder routes)
    {
        routes.MapDelete("/{name}/{key}", async (HttpContext context, string name, string key, DeleteLookupReferenceValue action) =>
            await action.HandleAsync(context, name, key))
            .WithMetadata(new RequireAntiforgeryTokenAttribute(true));
    }

    [Inject] private readonly ILookupReferenceService lookupReferenceService;

    public async Task HandleAsync(HttpContext httpContext, string name, string key)
    {
        try
        {
            await lookupReferenceService.DeleteValueAsync(name, key);
            httpContext.Response.StatusCode = 204;
        }
        catch (InvalidOperationException ex)
        {
            httpContext.Response.StatusCode = 400;
            await httpContext.Response.WriteAsJsonAsync(new { error = ex.Message });
        }
    }
}
