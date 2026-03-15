using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Endpoints.LookupReferences;

[Register(ServiceLifetime.Scoped)]
public sealed partial class ListLookupReferences : IEndpoint
{
    public static void MapRoutes(IEndpointRouteBuilder routes)
    {
        routes.MapGet("/", async (HttpContext context, ListLookupReferences action) =>
            await action.HandleAsync(context));
    }

    [Inject] private readonly ILookupReferenceService lookupReferenceService;

    public async Task HandleAsync(HttpContext httpContext)
    {
        var lookupReferences = await lookupReferenceService.GetAllAsync();
        await httpContext.Response.WriteAsJsonAsync(lookupReferences);
    }
}
