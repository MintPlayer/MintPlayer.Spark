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
        // Gates on query.EntityType, which getQuery also gates on — and getQuery is the first call
        // the sub-query component makes. There used to be a divergence recorded here: for Database.*
        // queries the executor authorized the type resolved from the SparkContext property instead,
        // so the effective grant was the intersection of two names nobody had reconciled. The
        // executor now refuses a query whose declared entityType is not the type its source yields,
        // so the two names cannot disagree and there is nothing left to diverge from.
        if (query.EntityType is null)
            return false;

        return await permissionService.IsAllowedAsync("Query", query.EntityType, cancellationToken);
    }

    /// <summary>
    /// Attaches the definitions of this type's AsDetail row types, so the client can draw their
    /// columns without finding them in the <c>Query</c>-gated catalogue (#385).
    /// </summary>
    /// <remarks>
    /// Lives here rather than in a second helper because this file already owns the
    /// copy-only-when-changed discipline, and its own remarks ask the next pruner to extend it.
    /// The same rule applies: never mutate the singleton's graph, copy only when something is
    /// attached, and return the same reference otherwise.
    /// </remarks>
    public static EntityTypeDefinition EmbedDetailTypes(
        EntityTypeDefinition entityType,
        IModelLoader modelLoader)
    {
        var collected = new Dictionary<Guid, EntityTypeDefinition>();
        Collect(entityType, modelLoader, collected, depth: 0, visited: []);

        if (collected.Count == 0)
            return entityType;

        var copy = entityType.ShallowCopy();
        copy.DetailTypes = [.. collected.Values];
        return copy;
    }

    /// <summary>
    /// Walks AsDetail attributes breadth-first, collecting each row type once.
    /// </summary>
    /// <remarks>
    /// ⚠️ Both guards are load-bearing. AsDetail nests — <c>EntityMapper</c> recurses into a nested
    /// AsDetail child — so a type reachable from itself (directly, or through a cycle of row types)
    /// would otherwise recurse until the stack ran out. <paramref name="visited"/> stops a cycle;
    /// <paramref name="depth"/> stops a legal-but-absurd nesting from producing a payload nobody
    /// asked for.
    /// </remarks>
    private static void Collect(
        EntityTypeDefinition entityType,
        IModelLoader modelLoader,
        Dictionary<Guid, EntityTypeDefinition> collected,
        int depth,
        HashSet<Guid> visited)
    {
        const int MaxDepth = 4;
        if (depth > MaxDepth || !visited.Add(entityType.Id))
            return;

        foreach (var attribute in entityType.Attributes)
        {
            if (attribute.DataType != "AsDetail" || attribute.AsDetailType is null)
                continue;

            var rowType = modelLoader.GetEntityTypeByClrType(attribute.AsDetailType);
            if (rowType is null || collected.ContainsKey(rowType.Id))
                continue;

            collected[rowType.Id] = Prune(rowType);
            Collect(rowType, modelLoader, collected, depth + 1, visited);
        }
    }

    /// <summary>
    /// The embedded copy, minus the fields a caller with no right on the row type has no business
    /// receiving and no client needs: the projection and query surface.
    /// </summary>
    /// <remarks>
    /// This is what keeps the disclosure smaller than what <c>EntityMapper.ScaffoldFrom</c> already
    /// ships for a non-empty collection. <c>QueryType</c> and <c>IndexName</c> name the RavenDB
    /// projection and index; <c>Queries</c> names runnable sub-queries; <c>Alias</c> is a routable
    /// identifier. A detail table reads <c>Attributes</c> and <c>Id</c> and nothing else.
    /// </remarks>
    private static EntityTypeDefinition Prune(EntityTypeDefinition rowType)
    {
        var copy = rowType.ShallowCopy();
        copy.QueryType = null;
        copy.IndexName = null;
        copy.Queries = [];
        copy.Alias = null;
        copy.DetailTypes = null;   // flattened onto the root; never nested inside an embedded copy
        return copy;
    }
}
