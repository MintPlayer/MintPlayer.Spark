using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Abstractions.Retry;
using MintPlayer.Spark.Endpoints.Queries;

namespace MintPlayer.Spark.Services;

/// <summary>What one query read answered: a page, or a refusal with its status and message.</summary>
internal sealed record QueryReadOutcome(QueryResult? Result, int StatusCode, string? Error)
{
    public static QueryReadOutcome Ok(QueryResult result) => new(result, StatusCodes.Status200OK, null);
    public static QueryReadOutcome NotFound(string error) => new(null, StatusCodes.Status404NotFound, error);
    public static QueryReadOutcome BadRequest(string error) => new(null, StatusCodes.Status400BadRequest, error);
}

/// <summary>
/// The body of <c>POST /spark/queries/execute</c>, shared with the live socket (X4) so a pushed page and a
/// fetched page are one read path: the same authorization order, sort and filter allow-lists, parent
/// resolution, row security and projection. A fourth copy is how the known read-path leaks multiplied.
/// </summary>
internal interface IQueryReadPath
{
    Task<QueryReadOutcome> ExecuteAsync(ExecuteQueryRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// The parent a sub-query was rendered under, resolved and authorized as <see cref="ExecuteAsync"/> does:
    /// null when none was asked for, <c>Found = false</c> when one was asked for and is missing or unreadable.
    /// </summary>
    Task<(bool Found, Abstractions.PersistentObject? Value)?> ResolveParentAsync(ExecuteQueryRequest request);
}

[Register(typeof(IQueryReadPath), ServiceLifetime.Scoped)]
internal sealed partial class QueryReadPath : IQueryReadPath
{
    // R2-M2: clamp `take` so an authenticated attacker can't request `take=2147483647` and have us
    // materialize entire collections into memory before paging. 1000 is well above any sane UI page size.
    internal const int MaxTake = 1000;

    [Inject] private readonly IQueryLoader queryLoader;
    [Inject] private readonly IQueryExecutor queryExecutor;
    [Inject] private readonly IDatabaseAccess databaseAccess;
    [Inject] private readonly IModelLoader modelLoader;
    [Inject] private readonly IPermissionService permissionService;
    [Inject] private readonly IRetryAccessor retryAccessor;
    [Inject] private readonly IRowPolicyRequestState rowPolicyRequestState;
    [Inject] private readonly IRowSecurity rowSecurity;

    public async Task<QueryReadOutcome> ExecuteAsync(ExecuteQueryRequest request, CancellationToken cancellationToken)
    {
        var id = request.QueryId;

        if (string.IsNullOrEmpty(id))
        {
            // The same 404 the gate below gives, for the same reason.
            return QueryReadOutcome.NotFound("Query not found");
        }

        // A query hook can prompt now, which is what having a body buys.
        RetryScope.Accept(retryAccessor, request);

        // T2 (#460): the soft-deletion mode reaches row policies through RowPolicyContext. Set before
        // anything asks row security, because row filters are memoized per request.
        rowPolicyRequestState.Deleted = request.Deleted ?? SparkDeletedFilter.Exclude;
        rowPolicyRequestState.IsSubQuery = !string.IsNullOrEmpty(request.ParentId);

        var query = queryLoader.ResolveQuery(id);

        // Authorize BEFORE anything else touches the request.
        //
        // Two reasons, and both were live holes. An unresolvable query used to 404 here
        // while an existing-but-denied one fell through to a 403 further down, so the
        // status told a caller which query ids are real. And the sortColumns check
        // below rejects an unknown column with 400, so an unauthorized caller could
        // enumerate the entity's attribute names by watching 400-vs-403 -- authorization
        // has to come first, or the parser answers questions on its behalf.
        //
        // Deliberate deviation from the audit's literal "keep 401 for unauthenticated":
        // this follows its metadata sibling Queries/Get.cs, which already answers 404 to
        // anonymous callers for the same id. The grid fetches metadata first, so the
        // login redirect was never reachable on this path anyway.
        if (query is null)
        {
            return QueryReadOutcome.NotFound($"Query '{id}' not found");
        }

        // The deleted mode is for the query's own rows; their references (the live question of a
        // deleted answer) are resolved as live. A query without a declared type keeps the old,
        // request-wide scope.
        if (query.EntityType is not null)
            rowPolicyRequestState.DeletedScopeClrType = modelLoader.ResolveEntityType(query.EntityType)?.ClrType;

        // Only when the query declares its entity type. A query that leaves it unset has its type
        // inferred downstream, and QueryExecutor authorizes there — refusing here would break
        // every such query rather than protect it. The catch below gives that path the same
        // uniform 404, so the oracle stays closed either way.
        if (query.EntityType is not null &&
            !await permissionService.IsAllowedAsync("Query", query.EntityType, cancellationToken))
        {
            return QueryReadOutcome.NotFound($"Query '{id}' not found");
        }

        try
        {
            // Sort overrides arrive as a typed array, not as `prop:asc,other:desc` in the query
            // string. The allow-list below is unchanged and is the part that matters.
            var sortOverrides = request.SortColumns is { Length: > 0 } ? request.SortColumns : null;
            if (sortOverrides is not null)
            {
                // Allow-list sort columns against the query's declared attribute set. Without
                // this check, a caller could sort by any public property on the projection
                // type via reflection (including fields the developer didn't expose as an
                // attribute), leaking ordering as a side channel. The query's own declared
                // sort columns are always allowed, so a query can opt in to otherwise-private
                // sort keys by declaring them up-front.
                var allowedProperties = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (query.EntityType is not null)
                {
                    var entityType = modelLoader.ResolveEntityType(query.EntityType);
                    if (entityType is not null)
                    {
                        foreach (var attr in entityType.Attributes)
                            allowedProperties.Add(attr.Name);
                    }
                }
                if (query.SortColumns is not null)
                {
                    foreach (var declared in query.SortColumns)
                        allowedProperties.Add(declared.Property);
                }

                var invalid = sortOverrides
                    .Where(c => !allowedProperties.Contains(c.Property))
                    .Select(c => c.Property)
                    .ToArray();
                if (invalid.Length > 0)
                    return QueryReadOutcome.BadRequest($"Unknown sort column(s): {string.Join(", ", invalid)}");

                // A misspelled direction used to sort ascending silently (PRD D5).
                var badDirection = sortOverrides.FirstOrDefault(c => !SortColumn.IsValidDirection(c.Direction));
                if (badDirection is not null)
                    return QueryReadOutcome.BadRequest($"Unknown sort direction '{badDirection.Direction}' for {badDirection.Property}; expected 'asc' or 'desc'.");
            }

            // Column filters (#431) are validated the same way and for the same reason: an unknown
            // name is a 400 here, while a known name the caller may not filter on is refused
            // silently in the executor. The split is deliberate and mirrors sorting — this layer
            // answers "is that a column at all", which the caller already knows from the query
            // definition it was served, and the capability answer stays where it cannot be probed.
            var columnFilters = request.Columns is { Length: > 0 }
                ? request.Columns.Where(c => !c.IsEmpty).ToArray()
                : null;
            if (columnFilters is { Length: > 0 })
            {
                var filterable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (query.EntityType is not null)
                {
                    var entityType = modelLoader.ResolveEntityType(query.EntityType);
                    if (entityType is not null)
                    {
                        foreach (var attr in entityType.Attributes)
                            filterable.Add(attr.Name);
                    }
                }

                var unknown = columnFilters
                    .Where(c => !filterable.Contains(c.Name))
                    .Select(c => c.Name)
                    .ToArray();
                if (unknown.Length > 0)
                    return QueryReadOutcome.BadRequest($"Unknown filter column(s): {string.Join(", ", unknown)}");
            }

            var search = request.Search;
            int skip = request.Skip is { } s ? Math.Max(0, s) : 0;
            int take = request.Take is { } t ? Math.Clamp(t, 1, MaxTake) : 50;

            var parent = await ResolveParentAsync(request);
            // Parent was asked for but we couldn't resolve or couldn't authorize it.
            // Return 404 rather than silently running the query unscoped — that would
            // leak data the caller shouldn't see (H-3). A deleted parent asked for without
            // ViewDeleted on its type lands here too, with the same body (#453).
            if (parent is { Found: false })
                return QueryReadOutcome.NotFound("Parent not found");

            // Copy only when the request overrides the sort; the cached definition is shared.
            var effectiveQuery = sortOverrides is null ? query : query.WithSortColumns(sortOverrides);

            var results = await queryExecutor.ExecuteQueryAsync(effectiveQuery, parent?.Value, skip, take, search,
                columnFilters: columnFilters, cancellationToken: cancellationToken);
            return QueryReadOutcome.Ok(results);
        }
        catch (SparkAccessDeniedException)
        {
            // The same 404 as the gate above, for anonymous callers too. Splitting on
            // authentication HERE would undo the gate: an anonymous caller would get 404
            // for an unknown query and 401 for a real one, which is the existence oracle
            // this endpoint just closed.
            return QueryReadOutcome.NotFound($"Query '{id}' not found");
        }
    }

    public async Task<(bool Found, Abstractions.PersistentObject? Value)?> ResolveParentAsync(ExecuteQueryRequest request)
    {
        var parentId = request.ParentId;
        var parentType = request.ParentType;
        if (string.IsNullOrEmpty(parentId) || string.IsNullOrEmpty(parentType))
            return null;

        Abstractions.PersistentObject? parent = null;
        var parentEntityType = modelLoader.ResolveEntityType(parentType);
        if (parentEntityType != null)
        {
            // Under the parent's own deleted mode (parentDeleted), not the rows' (deleted).
            parent = await SubQueryParent.ResolveAsync(databaseAccess, rowPolicyRequestState, rowSecurity,
                parentEntityType, parentId, request.ParentDeleted);
        }
        return (parent is not null, parent);
    }
}
