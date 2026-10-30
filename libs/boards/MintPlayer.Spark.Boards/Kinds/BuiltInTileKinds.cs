using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Abstractions.Prerendering;
using MintPlayer.Spark.Prerendering;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Boards.Kinds;

public static class SparkTileKinds
{
    public const string Query = "query";
    public const string Count = "count";
    public const string PersistentObject = "persistentObject";
    public const string ProgramUnit = "programUnit";
}

/// <summary>
/// A query tile, gated by <c>Query/&lt;EntityType&gt;</c> like <c>/spark/aliases</c>: a query whose entity type is
/// unknown is denied. Its rows are fetched through Spark's own execute endpoint, so row-level security applies there.
/// </summary>
internal sealed partial class QueryTileKind : ITileKind
{
    [Inject] private readonly IQueryLoader queryLoader;
    [Inject] private readonly IPermissionService permissionService;

    public string Name => SparkTileKinds.Query;

    public string? Validate(IReadOnlyDictionary<string, string> target) => ValidateQuery(target);

    public Task<SparkTileAccess> CheckAccessAsync(IReadOnlyDictionary<string, string> target, CancellationToken cancellationToken)
        => CheckQueryAsync(queryLoader, permissionService, target, cancellationToken);

    /// <summary>spark-query-tile: a root <c>&lt;spark-query-grid&gt;</c> over the target query.</summary>
    public Task SupplyAsync(ISparkPrerenderContext context, IReadOnlyDictionary<string, string> target)
        => context.SupplyQueryGridAsync(target["query"]);

    internal static string? ValidateQuery(IReadOnlyDictionary<string, string> target)
        => target.TryGetValue("query", out var query) && !string.IsNullOrWhiteSpace(query) && query.Length <= 200
            ? null
            : "this tile needs target.query (a query id or alias)";

    internal static async Task<SparkTileAccess> CheckQueryAsync(IQueryLoader queryLoader, IPermissionService permissionService, IReadOnlyDictionary<string, string> target, CancellationToken cancellationToken)
    {
        if (!target.TryGetValue("query", out var idOrAlias) || queryLoader.ResolveQuery(idOrAlias) is not { } query)
            return SparkTileAccess.Denied;
        return await QueryAllowedAsync(permissionService, query, cancellationToken)
            ? SparkTileAccess.Allow(query.Label ?? TranslatedString.Create(query.Name))
            : SparkTileAccess.Denied;
    }

    internal static async Task<bool> QueryAllowedAsync(IPermissionService permissionService, SparkQuery query, CancellationToken cancellationToken)
        => query.EntityType is { Length: > 0 } entityType && await permissionService.IsAllowedAsync("Query", entityType, cancellationToken);
}

/// <summary>A single number, the query's total (D14). Same target and gate as <see cref="QueryTileKind"/>.</summary>
internal sealed partial class CountTileKind : ITileKind
{
    [Inject] private readonly IQueryLoader queryLoader;
    [Inject] private readonly IPermissionService permissionService;

    public string Name => SparkTileKinds.Count;

    public string? Validate(IReadOnlyDictionary<string, string> target) => QueryTileKind.ValidateQuery(target);

    public Task<SparkTileAccess> CheckAccessAsync(IReadOnlyDictionary<string, string> target, CancellationToken cancellationToken)
        => QueryTileKind.CheckQueryAsync(queryLoader, permissionService, target, cancellationToken);

    /// <summary>spark-count-tile: <c>executeQuery(query, { skip: 0, take: 1 })</c>.</summary>
    public Task SupplyAsync(ISparkPrerenderContext context, IReadOnlyDictionary<string, string> target)
        => context.PostAsync("/spark/queries/execute", new { queryId = target["query"], skip = 0, take = 1 });
}

/// <summary>
/// One object, read-only. Gated by <c>Read/&lt;EntityType&gt;</c> and then by loading the object through
/// <see cref="IDatabaseAccess"/>, which applies row-level security: an object the caller cannot see is denied, never
/// shown as an error tile (S5).
/// </summary>
internal sealed partial class PersistentObjectTileKind : ITileKind
{
    [Inject] private readonly IModelLoader modelLoader;
    [Inject] private readonly IPermissionService permissionService;
    [Inject] private readonly IDatabaseAccess databaseAccess;

    public string Name => SparkTileKinds.PersistentObject;

    public string? Validate(IReadOnlyDictionary<string, string> target)
        => target.TryGetValue("entityType", out var type) && !string.IsNullOrWhiteSpace(type)
            && target.TryGetValue("id", out var id) && !string.IsNullOrWhiteSpace(id) && id.Length <= 512
            ? null
            : "a persistentObject tile needs target.entityType and target.id";

    public async Task<SparkTileAccess> CheckAccessAsync(IReadOnlyDictionary<string, string> target, CancellationToken cancellationToken)
    {
        if (!target.TryGetValue("entityType", out var typeName) || !target.TryGetValue("id", out var id)
            || modelLoader.ResolveEntityType(typeName) is not { } entityType)
            return SparkTileAccess.Denied;
        if (!await permissionService.IsAllowedAsync("Read", entityType.Name, cancellationToken))
            return SparkTileAccess.Denied;
        try
        {
            var item = await databaseAccess.GetPersistentObjectAsync(entityType.Id, id);
            if (item is null)
                return SparkTileAccess.Denied;
            return SparkTileAccess.Allow(item.Breadcrumb is { Length: > 0 } breadcrumb ? TranslatedString.Create(breadcrumb) : entityType.Label);
        }
        catch (SparkAccessDeniedException)
        {
            return SparkTileAccess.Denied;
        }
    }

    /// <summary>spark-po-tile: a <c>&lt;spark-po-detail&gt;</c> over the target object.</summary>
    public Task SupplyAsync(ISparkPrerenderContext context, IReadOnlyDictionary<string, string> target)
        => context.SupplyPersistentObjectAsync(target["entityType"], target["id"]);
}

/// <summary>A launcher for a menu entry, visible exactly when the menu would show it (<c>GET /spark/program-units</c>).</summary>
internal sealed partial class ProgramUnitTileKind : ITileKind
{
    [Inject] private readonly IProgramUnitsLoader programUnitsLoader;
    [Inject] private readonly IModelLoader modelLoader;
    [Inject] private readonly IQueryLoader queryLoader;
    [Inject] private readonly IPermissionService permissionService;

    public string Name => SparkTileKinds.ProgramUnit;

    public string? Validate(IReadOnlyDictionary<string, string> target)
        => target.TryGetValue("programUnit", out var id) && Guid.TryParse(id, out _) ? null : "a programUnit tile needs target.programUnit (a program unit id)";

    public async Task<SparkTileAccess> CheckAccessAsync(IReadOnlyDictionary<string, string> target, CancellationToken cancellationToken)
    {
        if (!target.TryGetValue("programUnit", out var raw) || !Guid.TryParse(raw, out var id))
            return SparkTileAccess.Denied;
        var unit = programUnitsLoader.GetProgramUnits().ProgramUnitGroups
            .SelectMany(g => g.ProgramUnits)
            .FirstOrDefault(u => u.Id == id);
        if (unit is null)
            return SparkTileAccess.Denied;
        return await IsVisibleAsync(unit, cancellationToken) ? SparkTileAccess.Allow(unit.Name) : SparkTileAccess.Denied;
    }

    /// <summary>The menu's rule: a typed unit needs the right its click demands, an unresolved type is hidden, a url unit is visible.</summary>
    internal async Task<bool> IsVisibleAsync(ProgramUnit unit, CancellationToken cancellationToken)
    {
        if (unit.Type == "persistentObject")
            return unit.PersistentObjectId is { } typeId && modelLoader.GetEntityType(typeId) is { } type
                && await permissionService.IsAllowedAsync("Read", type.Name, cancellationToken);
        if (unit.Type == "query")
            return unit.QueryId is { } queryId && queryLoader.GetQuery(queryId) is { } query
                && await QueryTileKind.QueryAllowedAsync(permissionService, query, cancellationToken);
        return true;
    }
}
