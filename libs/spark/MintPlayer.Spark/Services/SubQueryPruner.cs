using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;

namespace MintPlayer.Spark.Services;

/// <summary>
/// Removes the sub-queries a caller may not run from an entity type before it is serialized.
/// </summary>
/// <remarks>
/// A UX fix, not a security one, and worth being precise about which: <c>getQuery</c> already
/// refuses a denied query, so today's cost is a wasted round-trip and an empty gap on the detail
/// page rather than a disclosure. Pruning turns "a card that fails to load" into "no card".
/// <para>
/// ⚠️ <b>Never filter <see cref="EntityTypeDefinition.Queries"/> in place.</b> <c>ModelLoader</c>
/// is a singleton and hands every request references into one mutable graph, so an in-place filter
/// is a permanent, process-wide, first-caller-wins truncation — the first anonymous visitor would
/// delete the sub-queries for everyone until the process restarts. Worse, <c>ModelSynchronizer</c>
/// mutates a definition and writes it <em>to disk</em>; today it re-reads the directory itself, so
/// this is a near miss rather than a bug, but it is one refactor away from deleting sub-queries
/// from the model file permanently.
/// </para>
/// <para>
/// Hence: copy only when something is pruned, and return the very same reference otherwise. The
/// copy is shallow — it shares <c>Attributes</c>, <c>Tabs</c> and <c>Groups</c> with the singleton
/// — which is fine only for as long as nothing prunes those in place either. If you add a second
/// pruner, extend this helper rather than writing another one.
/// </para>
/// </remarks>
internal static class SubQueryPruner
{
    public static async Task<EntityTypeDefinition> PruneAsync(
        EntityTypeDefinition entityType,
        IQueryLoader queryLoader,
        IPermissionService permissionService,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        if (entityType.Queries.Length == 0)
            return entityType;

        var kept = new List<string>(entityType.Queries.Length);

        foreach (var alias in entityType.Queries)
        {
            if (await MayRunAsync(alias, queryLoader, permissionService, logger, cancellationToken))
                kept.Add(alias);
        }

        if (kept.Count == entityType.Queries.Length)
            return entityType;

        var copy = entityType.ShallowCopy();
        copy.Queries = [.. kept];
        return copy;
    }

    private static async Task<bool> MayRunAsync(
        string alias,
        IQueryLoader queryLoader,
        IPermissionService permissionService,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var query = queryLoader.ResolveQuery(alias);

        if (query is null)
        {
            // KEPT, and warned about. An alias that resolves to nothing is an authoring mistake —
            // a typo in `persistentObject.queries` — and pruning it would make the mistake
            // invisible instead of loud. It buys no security either: an alias naming nothing
            // discloses nothing.
            logger.LogWarning(
                "Entity type declares sub-query '{Alias}', which resolves to no query. Check "
                + "persistentObject.queries in the model file.", alias);
            return true;
        }

        // PRUNED, failing closed. Queries/Get.cs refuses a query with no entityType for exactly
        // this reason; keeping it here would render a card that then 404s, which is the bug being
        // fixed, preserved for the one case nobody tests.
        //
        // Note the divergence, recorded rather than reproduced: for Database.* queries the
        // executor authorizes the type resolved from the SparkContext property's generic argument,
        // not query.EntityType. Gate on query.EntityType anyway — it is what getQuery gates on,
        // and getQuery is the first call the sub-query component makes.
        if (query.EntityType is null)
            return false;

        return await permissionService.IsAllowedAsync("Query", query.EntityType, cancellationToken);
    }
}
