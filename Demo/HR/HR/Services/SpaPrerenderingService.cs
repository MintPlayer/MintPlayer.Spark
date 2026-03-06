using System.Reflection;
using MintPlayer.AspNetCore.SpaServices.Prerendering.Services;
using MintPlayer.AspNetCore.SpaServices.Routing;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Services;
using Raven.Client.Documents;

namespace HR.Services;

public partial class SpaPrerenderingService : ISpaPrerenderingService
{
    [Inject] private readonly ISpaRouteService spaRouteService;
    [Inject] private readonly IDatabaseAccess databaseAccess;
    [Inject] private readonly IModelLoader modelLoader;
    [Inject] private readonly IProgramUnitsLoader programUnitsLoader;
    [Inject] private readonly IQueryLoader queryLoader;
    [Inject] private readonly IQueryExecutor queryExecutor;
    [Inject] private readonly ISparkContextResolver sparkContextResolver;
    [Inject] private readonly IDocumentStore documentStore;
    [Inject] private readonly IRequestCultureResolver requestCultureResolver;
    [Inject] private readonly IPermissionService permissionService;

    public Task BuildRoutes(ISpaRouteBuilder routeBuilder)
    {
        routeBuilder
            .Route("home", "home")
            .Route("query/{queryId}", "query-list")
            .Group("po/{type}", "po", poRoutes => poRoutes
                .Route("", "list")
                .Route("{id}/edit", "edit")
                .Route("{id}", "detail")
            );
        return Task.CompletedTask;
    }

    public async Task OnSupplyData(HttpContext context, IDictionary<string, object> data)
    {
        data["programUnits"] = programUnitsLoader.GetProgramUnits();
        data["language"] = requestCultureResolver.GetCurrentCulture();

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
                        await SupplyPermissionsAsync(data, entityType);
                    }
                }
                break;
            }
            case "po-edit":
            case "po-detail":
            {
                var type = route.Parameters["type"];
                var id = route.Parameters["id"];
                var entityType = modelLoader.ResolveEntityType(type);
                if (entityType is not null)
                {
                    data["entityTypes"] = modelLoader.GetEntityTypes();
                    data["entityType"] = entityType;
                    await SupplyPermissionsAsync(data, entityType);

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
        var entityTypes = modelLoader.GetEntityTypes().ToList();
        data["entityTypes"] = entityTypes;
        data["query"] = query;

        var entityType = ResolveEntityTypeForQuery(query, entityTypes);
        if (entityType is not null)
        {
            data["entityType"] = entityType;
            await SupplyPermissionsAsync(data, entityType);
        }

        var items = await queryExecutor.ExecuteQueryAsync(query);
        data["queryItems"] = items;
    }

    private async Task SupplyPermissionsAsync(IDictionary<string, object> data, EntityTypeDefinition entityType)
    {
        var target = entityType.Name;
        data["permissions"] = new
        {
            canRead = await permissionService.IsAllowedAsync("Read", target),
            canCreate = await permissionService.IsAllowedAsync("New", target),
            canEdit = await permissionService.IsAllowedAsync("Edit", target),
            canDelete = await permissionService.IsAllowedAsync("Delete", target),
        };
    }

    private EntityTypeDefinition? ResolveEntityTypeForQuery(SparkQuery query, List<EntityTypeDefinition> entityTypes)
    {
        if (!string.IsNullOrEmpty(query.EntityType))
        {
            return modelLoader.GetEntityTypeByName(query.EntityType)
                ?? modelLoader.ResolveEntityType(query.EntityType);
        }

        if (!query.Source.StartsWith("Database.", StringComparison.OrdinalIgnoreCase))
            return null;

        var propertyName = query.Source[9..];
        var contextPropertyMap = BuildContextPropertyMap();
        if (contextPropertyMap.TryGetValue(propertyName, out var entityTypeName))
        {
            return modelLoader.GetEntityTypeByName(entityTypeName);
        }

        return null;
    }

    private Dictionary<string, string> BuildContextPropertyMap()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var session = documentStore.OpenAsyncSession();
            var sparkContext = sparkContextResolver.ResolveContext(session);
            if (sparkContext is null) return map;

            foreach (var property in sparkContext.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                var propertyType = property.PropertyType;
                if (!propertyType.IsGenericType) continue;

                var entityClrType = propertyType.GetGenericArguments().FirstOrDefault();
                if (entityClrType is null) continue;

                var entityTypeDef = modelLoader.GetEntityTypeByClrType(entityClrType.FullName ?? entityClrType.Name);
                if (entityTypeDef is not null)
                {
                    map[property.Name] = entityTypeDef.Name;
                }
            }
        }
        catch
        {
        }
        return map;
    }
}
