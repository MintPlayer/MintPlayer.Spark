using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Abstractions.Reflection;
using MintPlayer.Spark.Services;
using Raven.Client.Documents;

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
    [Inject] private readonly ILogger<GetProgramUnits> logger;

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
                var (isTyped, clrType) = ResolveClrType(unit, contextPropertyMap);

                // Security sweep L2: fail CLOSED for typed units. A persistentObject/query unit
                // names an entity type, so showing it when we can't confirm the caller's rights
                // leaks that type's existence (and the menu label) to someone who can't reach it.
                // A unit that is NOT typed (home link, dashboard, external URL — clrType null and
                // isTyped false) carries no entity name to leak, so it stays visible.
                bool show = isTyped
                    ? clrType is not null && await permissionService.IsAllowedAsync("Query", clrType)
                    : true;

                if (show)
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

            foreach (var property in sparkContext.GetType().GetCachedProperties())
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
        catch (Exception ex)
        {
            // Security sweep L2: a transient SparkContext/DB failure must not blank the map and
            // thereby show every typed query unit (fail-open). The empty map propagates as
            // "clrType null" for typed query units, which the caller now HIDES (fail-closed).
            // Log rather than swallow silently.
            logger.LogWarning(ex, "Failed to build SparkContext property map for program-unit filtering; typed query units will be hidden.");
        }

        return map;
    }

    /// <summary>
    /// Resolves a program unit to the CLR entity type name it exposes.
    /// <para>
    /// <c>IsTyped</c> is true for persistentObject/query units — they name an entity type, so an
    /// unresolvable one (<c>ClrType == null</c>) must be hidden, not shown (security sweep L2).
    /// <c>IsTyped</c> is false for units that expose no entity (home links, dashboards, external
    /// URLs); those carry nothing to leak and stay visible regardless.
    /// </para>
    /// </summary>
    private (bool IsTyped, string? ClrType) ResolveClrType(ProgramUnit unit, Dictionary<string, string> contextPropertyMap)
    {
        if (string.Equals(unit.Type, "persistentObject", StringComparison.OrdinalIgnoreCase)
            && unit.PersistentObjectId.HasValue)
        {
            return (true, modelLoader.GetEntityType(unit.PersistentObjectId.Value)?.Name);
        }

        if (string.Equals(unit.Type, "query", StringComparison.OrdinalIgnoreCase)
            && unit.QueryId.HasValue)
        {
            var query = queryLoader.GetQuery(unit.QueryId.Value);
            if (query is null) return (true, null);

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
                    return (true, query.EntityType);
                return (true, null);
            }

            if (propertyName != null)
            {
                return (true, contextPropertyMap.TryGetValue(propertyName, out var clrType) ? clrType : null);
            }

            return (true, null);
        }

        // Not a typed unit — nothing to leak, always visible.
        return (false, null);
    }
}
