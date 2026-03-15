using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Endpoints.PersistentObject;

[Register(ServiceLifetime.Scoped)]
public sealed partial class GetPersistentObject : IEndpoint
{
    public static void MapRoutes(IEndpointRouteBuilder routes)
    {
        routes.MapGet("/{objectTypeId}/{**id}", async (HttpContext context, string objectTypeId, string id, GetPersistentObject action) =>
            await action.HandleAsync(context, objectTypeId, id));
    }

    [Inject] private readonly IDatabaseAccess databaseAccess;
    [Inject] private readonly IModelLoader modelLoader;

    public async Task HandleAsync(HttpContext httpContext, string objectTypeId, string id)
    {
        var entityType = modelLoader.ResolveEntityType(objectTypeId);
        if (entityType is null)
        {
            httpContext.Response.StatusCode = StatusCodes.Status404NotFound;
            await httpContext.Response.WriteAsJsonAsync(new { error = $"Entity type '{objectTypeId}' not found" });
            return;
        }

        try
        {
            var decodedId = Uri.UnescapeDataString(id);
            var obj = await databaseAccess.GetPersistentObjectAsync(entityType.Id, decodedId);

            if (obj is null)
            {
                httpContext.Response.StatusCode = StatusCodes.Status404NotFound;
                await httpContext.Response.WriteAsJsonAsync(new { error = $"Object with ID {decodedId} not found" });
                return;
            }

            await httpContext.Response.WriteAsJsonAsync(obj);
        }
        catch (SparkAccessDeniedException)
        {
            if (httpContext.User.Identity?.IsAuthenticated != true)
            {
                httpContext.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await httpContext.Response.WriteAsJsonAsync(new { error = "Authentication required" });
            }
            else
            {
                httpContext.Response.StatusCode = StatusCodes.Status403Forbidden;
                await httpContext.Response.WriteAsJsonAsync(new { error = "Access denied" });
            }
        }
    }
}
