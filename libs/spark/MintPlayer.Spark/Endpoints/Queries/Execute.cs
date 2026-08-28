using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Endpoints.Queries;

internal sealed partial class ExecuteQuery : IGetEndpoint, IMemberOf<QueriesGroup>
{
    public static string Path => "/{id}/execute";

    [Inject] private readonly IQueryLoader queryLoader;
    [Inject] private readonly IQueryExecutor queryExecutor;
    [Inject] private readonly IDatabaseAccess databaseAccess;
    [Inject] private readonly IModelLoader modelLoader;
    [Inject] private readonly IPermissionService permissionService;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        var id = httpContext.Request.RouteValues["id"]!.ToString()!;
        var query = queryLoader.ResolveQuery(id);

        // Authorize BEFORE anything else touches the request.
        //
        // Two reasons, and both were live holes. An unresolvable query used to 404 here
        // while an existing-but-denied one fell through to a 403 further down, so the
        // status told a caller which query ids are real. And the ?sortColumns= parse
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
            return Results.Json(new { error = $"Query '{id}' not found" }, statusCode: 404);
        }

        // Only when the query declares its entity type. A query that leaves it unset has its type
        // inferred downstream, and QueryExecutor authorizes there — refusing here would break
        // every such query rather than protect it. The catch below gives that path the same
        // uniform 404, so the oracle stays closed either way.
        if (query.EntityType is not null &&
            !await permissionService.IsAllowedAsync("Query", query.EntityType, httpContext.RequestAborted))
        {
            return Results.Json(new { error = $"Query '{id}' not found" }, statusCode: 404);
        }

        try
        {
            // Read optional sort overrides from query string
            var sortColumnsParam = httpContext.Request.Query["sortColumns"].FirstOrDefault();
            SortColumn[]? sortOverrides = null;
            if (!string.IsNullOrEmpty(sortColumnsParam))
            {
                sortOverrides = sortColumnsParam.Split(',')
                    .Select(part =>
                    {
                        var segments = part.Split(':');
                        return new SortColumn
                        {
                            Property = segments[0],
                            Direction = segments.Length > 1 ? segments[1] : "asc"
                        };
                    })
                    .ToArray();

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
                {
                    return Results.Json(
                        new { error = $"Unknown sort column(s): {string.Join(", ", invalid)}" },
                        statusCode: 400);
                }
            }

            // Read pagination parameters. R2-M2: clamp `take` so an authenticated
            // attacker can't request `?take=2147483647` and have us materialize
            // entire collections into memory before paging. 1000 is well above
            // any sane UI page size; apps that need streaming for batch use cases
            // should hit /spark/queries/{id}/stream instead.
            const int MaxTake = 1000;
            var skipParam = httpContext.Request.Query["skip"].FirstOrDefault();
            var takeParam = httpContext.Request.Query["take"].FirstOrDefault();
            var search = httpContext.Request.Query["search"].FirstOrDefault();
            int skip = int.TryParse(skipParam, out var s) ? Math.Max(0, s) : 0;
            int take = int.TryParse(takeParam, out var t) ? Math.Clamp(t, 1, MaxTake) : 50;

            // Read optional parent context for custom queries
            Abstractions.PersistentObject? parent = null;
            var parentId = httpContext.Request.Query["parentId"].FirstOrDefault();
            var parentType = httpContext.Request.Query["parentType"].FirstOrDefault();
            if (!string.IsNullOrEmpty(parentId) && !string.IsNullOrEmpty(parentType))
            {
                var parentEntityType = modelLoader.ResolveEntityType(parentType);
                if (parentEntityType != null)
                {
                    parent = await databaseAccess.GetPersistentObjectAsync(parentEntityType.Id, parentId);
                }
                // Parent was asked for but we couldn't resolve or couldn't authorize it.
                // Return 404 rather than silently running the query unscoped — that would
                // leak data the caller shouldn't see (H-3).
                if (parent is null)
                    return Results.Json(new { error = "Parent not found" }, statusCode: 404);
            }

            // Copy only when the request overrides the sort; the cached definition is shared.
            var effectiveQuery = sortOverrides is null ? query : query.WithSortColumns(sortOverrides);

            var results = await queryExecutor.ExecuteQueryAsync(effectiveQuery, parent, skip, take, search, httpContext.RequestAborted);
            return Results.Json(results);
        }
        catch (SparkAccessDeniedException)
        {
            // The same 404 as the gate above, for anonymous callers too. Splitting on
            // authentication HERE would undo the gate: an anonymous caller would get 404
            // for an unknown query and 401 for a real one, which is the existence oracle
            // this endpoint just closed.
            return Results.Json(new { error = $"Query '{id}' not found" }, statusCode: 404);
        }
    }
}
