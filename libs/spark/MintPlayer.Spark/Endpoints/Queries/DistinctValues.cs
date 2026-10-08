using Microsoft.AspNetCore.Antiforgery;
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
[MemberOf<QueriesGroup>]
internal sealed partial class DistinctValues : IPostEndpoint<DistinctValuesRequest>
{
    public static string Path => "/distinct-values";

    // ⚠️ EXPLICITLY exempt, not merely unannotated. This is a read; a forged one changes nothing and
    // the attacker cannot see the response. It was exempt by absence until 11.0.0, when
    // SparkAntiforgeryOptions.RequireAntiforgery began defaulting to true and started gating any
    // mutating-verb request under /spark that carries an ambient credential — which swept these in
    // against the decision recorded above. Saying it out loud restores that decision and makes it
    // survive the next default change.
    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
    {
        builder.WithMetadata(new RequireAntiforgeryTokenAttribute(false));
    }

    [Inject] private readonly IQueryLoader queryLoader;
    [Inject] private readonly IQueryExecutor queryExecutor;
    [Inject] private readonly IDatabaseAccess databaseAccess;
    [Inject] private readonly IModelLoader modelLoader;
    [Inject] private readonly IPermissionService permissionService;
    [Inject] private readonly IRowPolicyRequestState rowPolicyRequestState;
    [Inject] private readonly IRowSecurity rowSecurity;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    /// <summary>A body that cannot be bound gets the refusal an unusable request gets below, never a parse error (PRD D3a).</summary>
    protected override ValueTask<IResult> OnBindFailedAsync(HttpContext context, EndpointBindingException? failure)
        => new(Results.Json(new { error = "Query not found" }, statusCode: 404));

    public override async Task<IResult> HandleAsync(DistinctValuesRequest request, CancellationToken cancellationToken)
    {
        var httpContext = httpContextAccessor.HttpContext!;
        var id = request.QueryId;

        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(request.Column))
        {
            // The same 404 a denied query gets, for the same reason.
            return Results.Json(new { error = "Query not found" }, statusCode: 404);
        }

        // T2 (#460): the same soft-deletion mode as the grid, so the panel offers what the grid can show.
        rowPolicyRequestState.Deleted = request.Deleted ?? SparkDeletedFilter.Exclude;

        var query = queryLoader.ResolveQuery(id);

        // Authorize BEFORE the column is looked at, exactly as Execute.cs does. Otherwise the column
        // check answers "is that a real attribute" for a caller with no Query right — the enumeration
        // oracle that endpoint was reordered to close.
        if (query is null)
        {
            return Results.Json(new { error = $"Query '{id}' not found" }, statusCode: 404);
        }

        // Scoped to the query's own type, as in Execute.cs.
        if (query.EntityType is not null)
            rowPolicyRequestState.DeletedScopeClrType = modelLoader.ResolveEntityType(query.EntityType)?.ClrType;

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
                query, request.Column, parent, request.Search, filters, httpContext.RequestAborted,
                querySearch: request.QuerySearch);

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
            : await SubQueryParent.ResolveAsync(databaseAccess, rowPolicyRequestState, rowSecurity,
                parentEntityType, request.ParentId, request.ParentDeleted);

        // Raised rather than returned, so the caller's catch turns it into the same 404 every other
        // refusal on this endpoint gives.
        return parent ?? throw new SparkAccessDeniedException("Parent not found");
    }
}
