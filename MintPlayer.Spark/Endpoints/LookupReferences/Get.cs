using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Endpoints.LookupReferences;

[Register(ServiceLifetime.Scoped)]
public sealed partial class GetLookupReference : IEndpoint
{
    public static void MapRoutes(IEndpointRouteBuilder routes)
    {
        routes.MapGet("/{name}", async (HttpContext context, string name, GetLookupReference action) =>
            await action.HandleAsync(context, name));
    }

    [Inject] private readonly ILookupReferenceService lookupReferenceService;

    public async Task HandleAsync(HttpContext httpContext, string name)
    {
        var lookupReference = await lookupReferenceService.GetAsync(name);

        if (lookupReference == null)
        {
            httpContext.Response.StatusCode = 404;
            await httpContext.Response.WriteAsJsonAsync(new { error = $"LookupReference '{name}' not found" });
            return;
        }

        await httpContext.Response.WriteAsJsonAsync(lookupReference);
    }
}
