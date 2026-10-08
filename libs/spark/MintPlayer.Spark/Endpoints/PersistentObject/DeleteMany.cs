using Microsoft.AspNetCore.Antiforgery;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Abstractions.ClientOperations;
using MintPlayer.Spark.Abstractions.Requests;
using MintPlayer.Spark.Abstractions.Retry;
using MintPlayer.Spark.Exceptions;
using MintPlayer.Spark.Services;
using Po = MintPlayer.Spark.Abstractions.PersistentObject;

namespace MintPlayer.Spark.Endpoints.PersistentObject;

/// <summary>
/// <c>POST /spark/po/delete-many</c> — the default <c>Delete</c> action on a query's selection
/// (#460, D18): one request, every row through the normal delete pipeline, one <c>SaveChanges</c>.
/// </summary>
/// <remarks>
/// <para>
/// The same checks, in the same order, as a custom action on a selection: the 200-row cap and the
/// <c>Delete</c> entry's selection rule and the required <c>queryId</c> first (400, no database work),
/// then the sub-query's container through its own gated read, then the rows fetched THROUGH that
/// query and checked readable (#467 D11/D12, <see cref="ISparkSelectionResolver"/>), and then — in
/// <see cref="IDatabaseAccess.DeletePersistentObjectsAsync"/> — the collection guard, the row gate,
/// the disabled-action hook and the interceptors. A row that is missing or denied refuses the lot
/// with the same answer as a missing row (M-3); a row whose hook withholds Delete refuses it with 403.
/// </para>
/// <para>
/// Separate from <c>/po/delete</c>, which stays the detail page's single-row delete. Both answer an
/// already-gone row with 404; a bulk delete never shrinks silently to the rows that remain.
/// </para>
/// </remarks>
[MemberOf<PersistentObjectGroup>]
internal sealed partial class DeleteManyPersistentObjects : IPostEndpoint
{
    public static string Path => "/delete-many";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
    {
        builder.WithMetadata(new RequireAntiforgeryTokenAttribute(true));
    }

    [Inject] private readonly IDatabaseAccess databaseAccess;
    [Inject] private readonly IModelLoader modelLoader;
    [Inject] private readonly IQueryLoader queryLoader;
    [Inject] private readonly ISparkSelectionResolver selectionResolver;
    [Inject] private readonly IActionsCatalogueLoader catalogueLoader;
    [Inject] private readonly IRetryAccessor retryAccessor;
    [Inject] private readonly IClientAccessor clientAccessor;
    [Inject] private readonly ILogger<DeleteManyPersistentObjects> logger;
    [Inject] private readonly Raven.Client.Documents.Session.IAsyncDocumentSession session;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        var (request, entityType) = await SparkRequestType.ReadAsync<DeleteManyRequest>(httpContext, modelLoader);
        if (request is null || entityType is null)
            return ClientResult.EnvelopeRefusal(clientAccessor, httpContext);

        var items = request.Items ?? [];
        var ids = items.Select(item => item.Id ?? string.Empty).ToArray();

        // Input validation first, as ExecuteCustomAction does: a violating request costs nothing.
        // Every row says which version it removes (#467, D14): a row edited since the list loaded is
        // a 409, not lost.
        if (items.Any(item => string.IsNullOrEmpty(item.Etag)))
        {
            return ClientResult.Envelope(clientAccessor,
                new { error = "Every row of a bulk delete must carry the etag of the version it removes." },
                StatusCodes.Status400BadRequest);
        }

        if (ids.Length > SparkDefaultActions.MaxSelectedItems)
        {
            return ClientResult.Envelope(clientAccessor,
                new { error = $"At most {SparkDefaultActions.MaxSelectedItems} items can be selected; {ids.Length} were submitted." },
                StatusCodes.Status400BadRequest);
        }

        // The composed Delete (#467, D7): removed ("Delete": null) or not on queries, bulk delete is
        // not offered, so it is not accepted either.
        var definition = catalogueLoader.GetCatalogue().Find(SparkDefaultActions.Delete);
        if (definition is not { IsShowedOnQuery: true })
        {
            return ClientResult.Envelope(clientAccessor,
                new { error = "Delete is not offered on queries of this application." },
                StatusCodes.Status400BadRequest);
        }

        if (!SelectionRuleParser.Parse(definition.SelectionRule)(ids.Length))
        {
            return ClientResult.Envelope(clientAccessor,
                new { error = $"Action 'Delete' requires a selection of '{definition.SelectionRule}'; {ids.Length} items were submitted." },
                StatusCodes.Status400BadRequest);
        }

        // D12: the rows are fetched through the query they were selected in, so that query's own
        // OnDisableActionsAsync decision, filter and row filter always apply. Without a query there is no
        // such decision to consult, and omitting it used to skip the query-level gate entirely.
        if (string.IsNullOrEmpty(request.QueryId))
        {
            return ClientResult.Envelope(clientAccessor,
                new { error = "A bulk delete must name the query its rows were selected in (queryId)." },
                StatusCodes.Status400BadRequest);
        }

        RetryScope.Accept(retryAccessor, request);

        try
        {
            // Rows, their hooks and every interceptor may each read; the batch itself is one load and
            // one save. A small constant plus a per-row allowance, named so an overrun says what it was.
            using var _ = session.IgnoreMaxRequests(
                30 + (4 * ids.Length), logger, $"bulk delete of {ids.Length} '{entityType.Name}' rows");

            // The sub-query's container, under ITS OWN type and through the gated read — a container
            // the caller may not see refuses the request rather than arriving as a fact.
            Po? parent = null;
            string? parentTypeName = null;
            if (!string.IsNullOrEmpty(request.ParentId) && !string.IsNullOrEmpty(request.ParentType))
            {
                var parentType = modelLoader.ResolveEntityType(request.ParentType);
                if (parentType is null)
                    return ClientResult.EnvelopeRefusal(clientAccessor, httpContext);

                // Live rows only, deliberately (#460): no `parentDeleted` here, unlike the query
                // reads. The recycle bin offers no delete on a deleted object's sub-queries.
                parent = await databaseAccess.GetPersistentObjectAsync(parentType.Id, request.ParentId);
                if (parent is null)
                    return ClientResult.EnvelopeRefusal(clientAccessor, httpContext);

                parentTypeName = parentType.Name;
            }

            var query = queryLoader.ResolveQuery(request.QueryId);
            if (query is null)
                return ClientResult.EnvelopeRefusal(clientAccessor, httpContext);

            // D11/D12: through the named query, readable, all or nothing — a row the query does not
            // return, or the caller cannot read, is missing, exactly like an id that names nothing.
            var rows = await selectionResolver.ResolveAsync(entityType, query, parent, ids, httpContext.RequestAborted);
            if (rows is null)
                return ClientResult.EnvelopeRefusal(clientAccessor, httpContext);

            await databaseAccess.DeletePersistentObjectsAsync(entityType.Id, ids, new SparkBulkDeleteContext
            {
                Query = query,
                Parent = parent,
                ParentType = parentTypeName,
                Reason = request.Reason,
                // An id named twice keeps its first etag; the rows collapse the same way.
                Etags = items
                    .Where(item => !string.IsNullOrEmpty(item.Id))
                    .DistinctBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(item => item.Id!, item => item.Etag!, StringComparer.OrdinalIgnoreCase),
            });
            return ClientResult.Envelope(clientAccessor, null, StatusCodes.Status204NoContent);
        }
        catch (SparkCancelException)
        {
            // An interceptor cancelled one row's delete (#482), which cancels the batch: nothing was deleted.
            return ClientResult.Envelope(clientAccessor, null, StatusCodes.Status204NoContent);
        }
        catch (SparkConcurrencyException ex)
        {
            // A row changed since the list loaded (#467, D14): named in the message (D18). Or a write in
            // the batch met a concurrent edit; the batch is atomic, so nothing was written
            // (contributions F7). Never the exception's own message, as in Update (R2-M1).
            return SparkAddOnEndpoints.ConcurrencyConflict(clientAccessor, ex);
        }
        catch (SparkValidationException ex)
        {
            return ClientResult.Envelope(clientAccessor, new { errors = new[] { ex.ToError() } }, StatusCodes.Status400BadRequest);
        }
        catch (SparkRowLevelAccessDeniedException)
        {
            return ClientResult.EnvelopeRefusal(clientAccessor, httpContext);
        }
        catch (SparkActionDisabledException ex)
        {
            return ClientResult.ActionDisabled(clientAccessor, ex);
        }
        catch (SparkAccessDeniedException)
        {
            return ClientResult.EnvelopeRefusal(clientAccessor, httpContext);
        }
        catch (SparkThrottledException ex)
        {
            return ClientResult.Throttled(clientAccessor, httpContext, ex);
        }
    }
}

/// <summary>The body of <c>POST /spark/po/delete-many</c>.</summary>
internal sealed class DeleteManyRequest : ISparkTypedRequest, IRetryableRequest
{
    /// <inheritdoc />
    public string? ObjectTypeId { get; set; }

    /// <summary>
    /// The selected rows, each with the etag the list showed (#467, D14). Raven ids contain slashes,
    /// hence the body rather than a route.
    /// </summary>
    public DeleteManyItem[]? Items { get; set; }

    /// <summary>
    /// The query the rows were selected in. Required (#467, D12): the rows are fetched through it, and
    /// its <c>OnDisableActionsAsync</c> decision applies.
    /// </summary>
    public string? QueryId { get; set; }

    /// <summary>The sub-query's container id, when deleting from a sub-query.</summary>
    public string? ParentId { get; set; }

    /// <summary>The container's entity type (name, alias or id).</summary>
    public string? ParentType { get; set; }

    /// <summary>One reason for the whole batch, recorded on every soft-deleted row (#467, D20).</summary>
    public string? Reason { get; set; }

    /// <inheritdoc />
    public RetryResult[]? RetryResults { get; set; }
}

/// <summary>One row of a <c>delete-many</c> request: its id and the version the caller saw.</summary>
internal sealed class DeleteManyItem
{
    public string? Id { get; set; }

    /// <summary>The row's <c>etag</c> from the query result. Required.</summary>
    public string? Etag { get; set; }
}
