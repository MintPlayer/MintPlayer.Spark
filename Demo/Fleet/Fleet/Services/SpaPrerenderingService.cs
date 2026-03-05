using MintPlayer.AspNetCore.SpaServices.Prerendering.Services;
using MintPlayer.AspNetCore.SpaServices.Routing;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Services;

namespace Fleet.Services;

public partial class SpaPrerenderingService : ISpaPrerenderingService
{
    [Inject] private readonly ISpaRouteService spaRouteService;
    [Inject] private readonly IDatabaseAccess databaseAccess;
    [Inject] private readonly IModelLoader modelLoader;
    [Inject] private readonly IProgramUnitsLoader programUnitsLoader;
    [Inject] private readonly IQueryLoader queryLoader;
    [Inject] private readonly IQueryExecutor queryExecutor;

    public Task BuildRoutes(ISpaRouteBuilder routeBuilder)
    {
        routeBuilder
            .Route("home", "home")
            .Route("query/{queryId}", "query-list")
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
            case "query-list":
            {
                var queryIdOrAlias = route.Parameters["queryId"];
                var query = queryLoader.ResolveQuery(queryIdOrAlias);
                if (query is not null)
                {
                    await SupplyQueryListData(data, query);
                }
                break;
            }
            case "po-list":
            {
                var type = route.Parameters["type"];
                var entityType = modelLoader.ResolveEntityType(type);
                if (entityType is not null)
                {
                    var query = queryLoader.GetQueries().FirstOrDefault(q =>
                        q.EntityType == entityType.Name ||
                        q.Source.EndsWith("." + entityType.Name, StringComparison.OrdinalIgnoreCase) ||
                        q.Source.EndsWith("." + entityType.Name + "s", StringComparison.OrdinalIgnoreCase));
                    if (query is not null)
                    {
                        await SupplyQueryListData(data, query);
                    }
                    else
                    {
                        data["entityTypes"] = modelLoader.GetEntityTypes();
                        data["entityType"] = entityType;
                    }
                }
                break;
            }
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

    private async Task SupplyQueryListData(IDictionary<string, object> data, SparkQuery query)
    {
        var entityTypes = modelLoader.GetEntityTypes();
        data["entityTypes"] = entityTypes;
        data["query"] = query;

        var entityTypeName = query.EntityType;
        if (string.IsNullOrEmpty(entityTypeName) && query.Source.Contains('.'))
        {
            entityTypeName = query.Source[(query.Source.IndexOf('.') + 1)..];
        }
        if (!string.IsNullOrEmpty(entityTypeName))
        {
            var entityType = modelLoader.ResolveEntityType(entityTypeName);
            if (entityType is not null)
            {
                data["entityType"] = entityType;
            }
        }

        var items = await queryExecutor.ExecuteQueryAsync(query);
        data["queryItems"] = items;
    }
}
