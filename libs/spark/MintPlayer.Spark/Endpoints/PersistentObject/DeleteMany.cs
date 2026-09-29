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
/// <c>Delete</c> entry's selection rule first (400, no database work), then the Delete right, the
/// sub-query's container through its own gated read, and then — in
/// <see cref="IDatabaseAccess.DeletePersistentObjectsAsync"/> — the collection guard, the row gate,
/// the disabled-action hook and the interceptors. A row that is missing or denied refuses the lot
/// with the same answer as a missing row (M-3); a row whose hook withholds Delete refuses it with 403.
/// </para>
/// <para>
/// Separate from <c>/po/delete</c>, which stays the detail page's single-row delete; that one keeps
/// its quiet 204 for an already-gone row, which a bulk delete must not (never shrink silently).
/// </para>
/// </remarks>
[MemberOf<PersistentObjectGroup>]
internal sealed partial class DeleteManyPersistentObjects : IPostEndpoint
{
    public static string Path => "/delete-many";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder)
    {
        builder.WithMetadata(new RequireAntiforgeryTokenAttribute(true));
    }

    [Inject] private readonly IDatabaseAccess databaseAccess;
    [Inject] private readonly IModelLoader modelLoader;
    [Inject] private readonly IQueryLoader queryLoader;
    [Inject] private readonly ICustomActionsConfigurationLoader configLoader;
    [Inject] private readonly IRetryAccessor retryAccessor;
    [Inject] private readonly IClientAccessor clientAccessor;
    [Inject] private readonly ILogger<DeleteManyPersistentObjects> logger;
    [Inject] private readonly Raven.Client.Documents.Session.IAsyncDocumentSession session;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        var (request, entityType) = await SparkRequestType.ReadAsync<DeleteManyRequest>(httpContext, modelLoader);
        if (request is null || entityType is null)
            return ClientResult.EnvelopeRefusal(clientAccessor, httpContext);

        var ids = request.Ids ?? [];

        // Input validation first, as ExecuteCustomAction does: a violating request costs nothing.
        if (ids.Length > SparkDefaultActions.MaxSelectedItems)
        {
            return ClientResult.Envelope(clientAccessor,
                new { error = $"At most {SparkDefaultActions.MaxSelectedItems} items can be selected; {ids.Length} were submitted." },
                StatusCodes.Status400BadRequest);
        }

        var definition = SparkDefaultActions.Resolve(SparkDefaultActions.Delete, configLoader.GetConfiguration());
        if (!SparkDefaultActions.IsShowedOnQuery(definition.ShowedOn))
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

            var query = string.IsNullOrEmpty(request.QueryId) ? null : queryLoader.ResolveQuery(request.QueryId);

            await databaseAccess.DeletePersistentObjectsAsync(entityType.Id, ids, new SparkBulkDeleteContext
            {
                Query = query,
                Parent = parent,
                ParentType = parentTypeName,
            });
            return ClientResult.Envelope(clientAccessor, null, StatusCodes.Status204NoContent);
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

    /// <summary>The selected rows. Raven ids contain slashes, hence the body rather than a route.</summary>
    public string[]? Ids { get; set; }

    /// <summary>The query the rows were selected in, for the disabled-action hook.</summary>
    public string? QueryId { get; set; }

    /// <summary>The sub-query's container id, when deleting from a sub-query.</summary>
    public string? ParentId { get; set; }

    /// <summary>The container's entity type (name, alias or id).</summary>
    public string? ParentType { get; set; }

    /// <inheritdoc />
    public RetryResult[]? RetryResults { get; set; }
}
