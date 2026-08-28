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
                var (requiredAction, entityTypeName) = ResolveTarget(unit, contextPropertyMap);

                // Security sweep L2: fail CLOSED for typed units. A persistentObject/query unit
                // names an entity type, so showing it when we can't confirm the caller's rights
                // leaks that type's existence (and the menu label) to someone who can't reach it.
                // A url unit (requiredAction null) carries no entity name to leak, so it stays
                // visible. The action mirrors what clicking the unit will demand: a query unit
                // executes under "Query", a persistentObject unit's page loads under "Read" —
                // gating both on "Query" showed menu entries whose click 404s.
                bool show = requiredAction is null
                    || (entityTypeName is not null && await permissionService.IsAllowedAsync(requiredAction, entityTypeName));

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
    /// Resolves a program unit to the right it demands and the entity type name it demands it on.
    /// <para>
    /// <c>RequiredAction</c> is non-null for persistentObject/query units — they name an entity
    /// type, so an unresolvable one (<c>EntityTypeName == null</c>) must be hidden, not shown
    /// (security sweep L2). It is null for <c>url</c> units, which expose no entity and stay
    /// visible regardless. The loader has already canonicalized <see cref="ProgramUnit.Type"/> and
    /// validated the target fields, so the comparisons here are exact.
    /// </para>
    /// </summary>
    private (string? RequiredAction, string? EntityTypeName) ResolveTarget(ProgramUnit unit, Dictionary<string, string> contextPropertyMap)
    {
        if (unit.Type == ProgramUnitsLoader.TypePersistentObject && unit.PersistentObjectId.HasValue)
        {
            return ("Read", modelLoader.GetEntityType(unit.PersistentObjectId.Value)?.Name);
        }

        if (unit.Type == ProgramUnitsLoader.TypeQuery && unit.QueryId.HasValue)
        {
            var query = queryLoader.GetQuery(unit.QueryId.Value);
            if (query is null) return ("Query", null);

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
                    return ("Query", query.EntityType);
                return ("Query", null);
            }

            if (propertyName != null)
            {
                return ("Query", contextPropertyMap.TryGetValue(propertyName, out var clrType) ? clrType : null);
            }

            return ("Query", null);
        }

        // A url unit — nothing to leak, always visible.
        return (null, null);
    }
}
