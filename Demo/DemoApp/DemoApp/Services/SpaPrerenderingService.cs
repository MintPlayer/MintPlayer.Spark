using MintPlayer.AspNetCore.SpaServices.Prerendering.Services;
using MintPlayer.AspNetCore.SpaServices.Routing;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Services;

namespace DemoApp.Services;

public partial class SpaPrerenderingService : ISpaPrerenderingService
{
    [Inject] private readonly ISpaRouteService spaRouteService;
    [Inject] private readonly IDatabaseAccess databaseAccess;
    [Inject] private readonly IModelLoader modelLoader;
    [Inject] private readonly IProgramUnitsLoader programUnitsLoader;

    public Task BuildRoutes(ISpaRouteBuilder routeBuilder)
    {
        routeBuilder
            .Route("home", "home")
            .Group("po/{type}", "po", poRoutes => poRoutes
                .Route("", "list")
                .Route("{id}", "detail")
            );
        return Task.CompletedTask;
    }

    public async Task OnSupplyData(HttpContext context, IDictionary<string, object> data)
    {
        data["programUnits"] = programUnitsLoader.GetProgramUnits();

        var route = await spaRouteService.GetCurrentRoute(context);
        switch (route?.Name)
        {
            case "po-detail":
            {
                var type = route.Parameters["type"];
                var id = route.Parameters["id"];
                var entityType = modelLoader.ResolveEntityType(type);
                if (entityType is not null)
                {
                    data["entityTypes"] = modelLoader.GetEntityTypes();
                    data["entityType"] = entityType;

                    var obj = await databaseAccess.GetPersistentObjectAsync(entityType.Id, Uri.UnescapeDataString(id));
                    if (obj is not null)
                        data["persistentObject"] = obj;
                }
                break;
            }
        }
    }
}
