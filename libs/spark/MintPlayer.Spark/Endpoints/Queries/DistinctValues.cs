using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Exceptions;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Endpoints.Queries;

/// <summary>
/// <c>POST /spark/queries/distinct-values</c> — the value list behind one column's filter panel (#431).
/// </summary>
/// <remarks>
/// A literal route with a JSON body, like every other query endpoint: the parameters (a column, a
/// search term, the other columns' current filters, and a parent for a sub-query) do not fit a query
/// string, which is the same reason the reads moved to POST in the first place.
/// <para>
/// ⚠️ <b>This is a disclosure surface, not a convenience.</b> Listing a column's values is strictly
/// stronger than filtering by a value already known, which is why <c>canListDistincts</c> exists
/// separately from <c>canFilter</c> and why the values are computed over rows the row-security gate
/// has already filtered rather than aggregated in the database.
/// </para>
/// </remarks>
internal sealed partial class DistinctValues : IPostEndpoint, IMemberOf<QueriesGroup>
{
    public static string Path => "/distinct-values";

    [Inject] private readonly IQueryLoader queryLoader;
    [Inject] private readonly IQueryExecutor queryExecutor;
    [Inject] private readonly IDatabaseAccess databaseAccess;
    [Inject] private readonly IModelLoader modelLoader;
    [Inject] private readonly IPermissionService permissionService;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        var request = await SparkRequestBody.ReadAsync<DistinctValuesRequest>(httpContext);
        var id = request?.QueryId;

        if (request is null || string.IsNullOrEmpty(id) || string.IsNullOrEmpty(request.Column))
        {
            // The same 404 a denied query gets, for the same reason.
            return Results.Json(new { error = "Query not found" }, statusCode: 404);
        }

        var query = queryLoader.ResolveQuery(id);

        // Authorize BEFORE the column is looked at, exactly as Execute.cs does. Otherwise the column
        // check answers "is that a real attribute" for a caller with no Query right — the enumeration
        // oracle that endpoint was reordered to close.
        if (query is null)
        {
            return Results.Json(new { error = $"Query '{id}' not found" }, statusCode: 404);
        }

        if (query.EntityType is not null &&
            !await permissionService.IsAllowedAsync("Query", query.EntityType, httpContext.RequestAborted))
        {
            return Results.Json(new { error = $"Query '{id}' not found" }, statusCode: 404);
        }

        try
        {
            var parent = await ResolveParentAsync(request);

            var filters = request.Columns is { Length: > 0 }
                ? request.Columns.Where(c => !c.IsEmpty).ToArray()
                : null;

            var values = await queryExecutor.GetDistinctValuesAsync(
                query, request.Column, parent, request.Search, filters, httpContext.RequestAborted);

            return Results.Ok(values);
        }
        catch (SparkAccessDeniedException)
        {
            return Results.Json(new { error = $"Query '{id}' not found" }, statusCode: 404);
        }
    }

    /// <summary>
    /// The object a sub-query's panel was opened on, when there is one.
    /// </summary>
    /// <remarks>
    /// A detail grid's distinct values depend on its parent — the rows are the parent's, so the
    /// values are too. An unresolvable parent yields the same 404 rather than silently listing the
    /// unscoped collection's values, which would be a disclosure dressed as a fallback.
    /// </remarks>
    private async Task<Abstractions.PersistentObject?> ResolveParentAsync(DistinctValuesRequest request)
    {
        if (string.IsNullOrEmpty(request.ParentId) || string.IsNullOrEmpty(request.ParentType))
            return null;

        var parentEntityType = modelLoader.ResolveEntityType(request.ParentType);
        var parent = parentEntityType is null
            ? null
            : await databaseAccess.GetPersistentObjectAsync(parentEntityType.Id, request.ParentId);

        // Raised rather than returned, so the caller's catch turns it into the same 404 every other
        // refusal on this endpoint gives.
        return parent ?? throw new SparkAccessDeniedException("Parent not found");
    }
}
