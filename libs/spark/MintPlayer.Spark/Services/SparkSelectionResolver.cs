using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using Po = MintPlayer.Spark.Abstractions.PersistentObject;

namespace MintPlayer.Spark.Services;

/// <summary>
/// Resolves the rows a bulk call names — delete-many, or a custom action run on a selection — on the
/// server, all or nothing (#467, D11/D12).
/// </summary>
/// <remarks>
/// <para>
/// <b>Through the query the rows came from (D12).</b> The query is re-run narrowed to the ids, so it
/// applies its own right, filter, row filter and parent; a row that query does not return is
/// missing. The query must produce rows of the call's type. A query that cannot be re-run (one that
/// owns its paging, a streaming query) falls back to the row-gated document load.
/// </para>
/// <para>
/// <b>Readable (D11).</b> Every row must also pass the type's <c>Read</c> right and <c>Read</c> row
/// rule, on top of whatever the action itself checks afterwards. Without this a Delete rule wider than
/// the Read rule let delete-many remove rows the caller could not see, and the action's own gates —
/// <c>OnDisableActionsAsync</c> answering 403 — turned an unreadable row into an existence oracle.
/// </para>
/// <para>
/// A row failing any check counts as missing: the whole call is refused, the same as for an id that
/// names nothing (M-3). Never a silently smaller set.
/// </para>
/// </remarks>
public interface ISparkSelectionResolver
{
    /// <returns>The rows, in the query's shape; <see langword="null"/> means refuse with a 404.</returns>
    Task<IReadOnlyList<QueryResultItem>?> ResolveAsync(
        EntityTypeDefinition entityType,
        SparkQuery query,
        Po? queryParent,
        IReadOnlyList<string> ids,
        CancellationToken cancellationToken = default);
}

[Register(typeof(ISparkSelectionResolver), ServiceLifetime.Scoped)]
internal sealed partial class SparkSelectionResolver : ISparkSelectionResolver
{
    [Inject] private readonly IQueryExecutor queryExecutor;
    [Inject] private readonly IDatabaseAccess databaseAccess;
    [Inject] private readonly IPermissionService permissionService;
    [Inject] private readonly IRowSecurity rowSecurity;
    [Inject] private readonly ISparkTypeResolver typeResolver;
    [Inject] private readonly Raven.Client.Documents.Session.IAsyncDocumentSession session;
    [Inject] private readonly ILogger<SparkSelectionResolver> logger;
    [Inject] private readonly IAttributeRightsEnforcement attributeRights;

    public async Task<IReadOnlyList<QueryResultItem>?> ResolveAsync(
        EntityTypeDefinition entityType,
        SparkQuery query,
        Po? queryParent,
        IReadOnlyList<string> ids,
        CancellationToken cancellationToken = default)
    {
        if (ids.Count == 0 || ids.Any(string.IsNullOrEmpty))
            return null;

        // The query must produce rows of the type the call is authorized on, or the client could name
        // a query over another type and have its rows handed to a gate on a different grant.
        if (!string.Equals(query.EntityType, entityType.Name, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning(
                "Bulk call refused: query '{Query}' returns rows of '{QueryType}', but the call is on '{Type}'.",
                query.Name, query.EntityType, entityType.Name);
            return null;
        }

        // D11: the Read right first — a caller who may not read the type may not act on its rows.
        if (!await permissionService.IsAllowedAsync("Read", entityType.Name))
            return null;

        IReadOnlyList<QueryResultItem> rows;
        if (!query.IsStreamingQuery && !queryExecutor.OwnsItsOwnPaging(query))
        {
            var result = await queryExecutor.ExecuteQueryAsync(
                query,
                // The QUERY's parent: the sub-query's container, which filters its rows.
                parent: queryParent,
                skip: 0,
                take: ids.Count,
                search: null,
                restrictToIds: ids,
                cancellationToken: cancellationToken);
            rows = result.Items;
        }
        else
        {
            // Row-gated by the batched load itself (collection guard, Read row rule, redaction).
            var loaded = await databaseAccess.GetPersistentObjectsByIdAsync(entityType.Id, ids);
            // The caller's query surface, as a re-run query would have used (#264, G4): a Query-denied
            // attribute is neither a column nor a value in these rows.
            var surface = await attributeRights.ForQueryAsync(entityType, cancellationToken);
            var columns = QueryResultProjector.BuildColumns(surface);
            rows = QueryResultProjector.ToItems(
                RowSecurityGate.SecuredRows.FromRowGatedLoad([.. loaded]), columns, $"Selection '{entityType.Name}'");
        }

        // Never shrink silently: compared with what the source yielded, distinct because selecting a
        // row twice is not an error.
        var requested = ids.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (rows.Count != requested.Length
            || !requested.All(id => rows.Any(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase))))
            return null;

        // D11: the Read row rule, asked explicitly — the query applied its own (Query) rule, which
        // need not be the Read rule.
        if (!string.IsNullOrEmpty(entityType.ClrType))
        {
            var clrType = typeResolver.Resolve(entityType.ClrType)
                ?? throw new InvalidOperationException(
                    $"'{entityType.Name}' declares clrType '{entityType.ClrType}', which no loaded assembly declares. " +
                    "Rows cannot be authorized without it.");
            if (!await rowSecurity.AreAllowedAsync(session, clrType, "Read", requested))
                return null;
        }

        return rows;
    }
}
