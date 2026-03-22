using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Services;
using Raven.Client.Documents;
using System.Reflection;

namespace MintPlayer.Spark.Endpoints.ProgramUnits;

internal sealed partial class GetProgramUnits : IGetEndpoint, IMemberOf<SparkGroup>
{
    public static string Path => "/program-units";

    [Inject] private readonly IProgramUnitsLoader programUnitsLoader;
    [Inject] private readonly IPermissionService permissionService;
    [Inject] private readonly IModelLoader modelLoader;
    [Inject] private readonly IQueryLoader queryLoader;
    [Inject] private readonly ISparkContextResolver sparkContextResolver;
    [Inject] private readonly IDocumentStore documentStore;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        var config = programUnitsLoader.GetProgramUnits();

        // Build a Database property name -> entity type name lookup from the SparkContext
        var contextPropertyMap = BuildContextPropertyMap();

        var filteredGroups = new List<ProgramUnitGroup>();
        foreach (var group in config.ProgramUnitGroups)
        {
            var filteredUnits = new List<ProgramUnit>();
            foreach (var unit in group.ProgramUnits)
            {
                var clrType = ResolveClrType(unit, contextPropertyMap);
                if (clrType is null || await permissionService.IsAllowedAsync("Query", clrType))
                {
                    filteredUnits.Add(unit);
                }
            }

            if (filteredUnits.Count > 0)
            {
                filteredGroups.Add(new ProgramUnitGroup
                {
                    Id = group.Id,
                    Name = group.Name,
                    Icon = group.Icon,
                    Order = group.Order,
                    ProgramUnits = filteredUnits.ToArray(),
                });
            }
        }

        var result = new ProgramUnitsConfiguration
        {
            ProgramUnitGroups = filteredGroups.ToArray(),
        };

        return Results.Json(result);
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

                var entityType = propertyType.GetGenericArguments().FirstOrDefault();
                if (entityType is null) continue;

                var entityTypeDef = modelLoader.GetEntityTypeByClrType(entityType.FullName ?? entityType.Name);
                if (entityTypeDef is not null)
                {
                    map[property.Name] = entityTypeDef.Name;
                }
            }
        }
        catch
        {
            // If SparkContext resolution fails, return empty map (fail-open: all items shown)
        }

        return map;
    }

    private string? ResolveClrType(ProgramUnit unit, Dictionary<string, string> contextPropertyMap)
    {
        if (string.Equals(unit.Type, "persistentObject", StringComparison.OrdinalIgnoreCase)
            && unit.PersistentObjectId.HasValue)
        {
            return modelLoader.GetEntityType(unit.PersistentObjectId.Value)?.Name;
        }

        if (string.Equals(unit.Type, "query", StringComparison.OrdinalIgnoreCase)
            && unit.QueryId.HasValue)
        {
            var query = queryLoader.GetQuery(unit.QueryId.Value);
            if (query is null) return null;

            // Extract the property name from the Source field
            var source = query.Source;
            string? propertyName = null;
            if (source.StartsWith("Database.", StringComparison.OrdinalIgnoreCase))
            {
                propertyName = source[9..];
            }
            else if (source.StartsWith("Custom.", StringComparison.OrdinalIgnoreCase))
            {
                // For custom queries, use EntityType directly if available
                if (!string.IsNullOrEmpty(query.EntityType))
                    return query.EntityType;
                return null;
            }

            if (propertyName != null)
            {
                return contextPropertyMap.TryGetValue(propertyName, out var clrType) ? clrType : null;
            }

            return null;
        }

        return null;
    }
}
