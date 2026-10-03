using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Abstractions.ClientOperations;
using MintPlayer.Spark.Abstractions.Requests;
using MintPlayer.Spark.Endpoints;
using MintPlayer.Spark.Exceptions;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.History.Endpoints;

/// <summary>
/// <c>/spark/po</c>, shared with core's persistent-object endpoints: the route table is fully
/// literal, and ids — which contain slashes — travel in the body (#460, T1).
/// </summary>
internal class HistoryPersistentObjectGroup : IEndpointGroup
{
    public static string Prefix => "/spark/po";
}

/// <summary>The body of <c>/spark/po/revisions</c>, <c>/spark/po/revision</c> and <c>/spark/po/revert</c>.</summary>
internal sealed class HistoryRequest : ISparkTypedRequest
{
    /// <inheritdoc />
    public string? ObjectTypeId { get; set; }

    /// <summary>The row's id.</summary>
    public string? Id { get; set; }

    /// <summary>The revision (<c>revision</c>, <c>revert</c>); ignored by <c>revisions</c>.</summary>
    public string? ChangeVector { get; set; }

    /// <summary><c>revisions</c> paging: rows to skip (default 0).</summary>
    public int? Skip { get; set; }

    /// <summary><c>revisions</c> paging: rows to return (default 50, at most 200).</summary>
    public int? Take { get; set; }

    /// <summary>
    /// <c>revisions</c> and <c>revision</c> only (ignored by <c>revert</c>): whether the row may be a
    /// deleted one — <c>exclude</c> (default), <c>include</c> or <c>only</c>, exactly as on
    /// <c>/spark/po/load</c> (#460, T2). The SoftDelete package honours it only for holders of
    /// <c>ViewDeleted/T</c>; everyone else keeps the 404, so a soft-deleted row's history is readable
    /// from the recycle bin without disclosing the row to anyone else.
    /// </summary>
    public SparkDeletedFilter? Deleted { get; set; }
}

/// <summary>
/// <c>POST /spark/po/revisions { objectTypeId, id, skip?, take? }</c> → 200 with the row's revisions,
/// newest first. Requires <c>History/T</c> and a row the caller can load now.
/// </summary>
[MemberOf<HistoryPersistentObjectGroup>]
internal sealed partial class ListRevisions : IPostEndpoint
{
    public static string Path => "/revisions";

    // ⚠️ EXPLICITLY exempt: a read, like /spark/po/load. A forged one changes nothing and its response
    // cannot be read cross-origin. Stated, not merely absent, so the next default change cannot sweep
    // it in.
    static void IEndpointBase.Configure(RouteHandlerBuilder builder)
        => builder.WithMetadata(new RequireAntiforgeryTokenAttribute(false));

    [Inject] private readonly ISparkHistory history;
    [Inject] private readonly IModelLoader modelLoader;
    [Inject] private readonly IClientAccessor clientAccessor;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        var (request, entityType) = await SparkAddOnEndpoints.ReadTypedRequestAsync<HistoryRequest>(httpContext, modelLoader);
        if (request is null || entityType is null || string.IsNullOrEmpty(request.Id))
            return SparkAddOnEndpoints.Refusal(clientAccessor, httpContext);

        // Before the current-row gate asks row security (row filters are memoized per request).
        SparkAddOnEndpoints.UseDeletedFilter(httpContext, request.Deleted, entityType);

        try
        {
            var revisions =await history.ListAsync(entityType.Id, request.Id, request.Skip ?? 0, request.Take ?? 50, httpContext.RequestAborted);
            return SparkAddOnEndpoints.Envelope(clientAccessor, revisions, StatusCodes.Status200OK);
        }
        catch (SparkAccessDeniedException)
        {
            return SparkAddOnEndpoints.Refusal(clientAccessor, httpContext);
        }
    }
}

/// <summary>
/// <c>POST /spark/po/revision { objectTypeId, id, changeVector }</c> → 200 with the revision as a
/// read-only, redacted persistent object. Requires <c>History/T</c>; a change vector of another row
/// is refused like a missing one.
/// </summary>
[MemberOf<HistoryPersistentObjectGroup>]
internal sealed partial class GetRevision : IPostEndpoint
{
    public static string Path => "/revision";

    // ⚠️ EXPLICITLY exempt: a read (see ListRevisions).
    static void IEndpointBase.Configure(RouteHandlerBuilder builder)
        => builder.WithMetadata(new RequireAntiforgeryTokenAttribute(false));

    [Inject] private readonly ISparkHistory history;
    [Inject] private readonly IModelLoader modelLoader;
    [Inject] private readonly IClientAccessor clientAccessor;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        var (request, entityType) = await SparkAddOnEndpoints.ReadTypedRequestAsync<HistoryRequest>(httpContext, modelLoader);
        if (request is null || entityType is null || string.IsNullOrEmpty(request.Id) || string.IsNullOrEmpty(request.ChangeVector))
            return SparkAddOnEndpoints.Refusal(clientAccessor, httpContext);

        SparkAddOnEndpoints.UseDeletedFilter(httpContext, request.Deleted, entityType);

        try
        {
            var revision =await history.GetAsync(entityType.Id, request.Id, request.ChangeVector, httpContext.RequestAborted);
            return SparkAddOnEndpoints.Envelope(clientAccessor, revision, StatusCodes.Status200OK);
        }
        catch (SparkAccessDeniedException)
        {
            return SparkAddOnEndpoints.Refusal(clientAccessor, httpContext);
        }
    }
}

/// <summary>
/// <c>POST /spark/po/revert { objectTypeId, id, changeVector }</c> → 200 with the reverted row.
/// Requires <c>History/T</c>, <c>Revert/T</c> and <c>Edit/T</c>; saved through the normal pipeline
/// (row gate, WITH CHECK, interceptors). A disabled revert is 403 naming the action, a concurrent edit
/// 409, an interceptor's refusal 400.
/// </summary>
[MemberOf<HistoryPersistentObjectGroup>]
internal sealed partial class RevertPersistentObject : IPostEndpoint
{
    public static string Path => "/revert";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder)
        => builder.WithMetadata(new RequireAntiforgeryTokenAttribute(true));

    [Inject] private readonly ISparkHistory history;
    [Inject] private readonly IModelLoader modelLoader;
    [Inject] private readonly IClientAccessor clientAccessor;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        var (request, entityType) = await SparkAddOnEndpoints.ReadTypedRequestAsync<HistoryRequest>(httpContext, modelLoader);
        if (request is null || entityType is null || string.IsNullOrEmpty(request.Id) || string.IsNullOrEmpty(request.ChangeVector))
            return SparkAddOnEndpoints.Refusal(clientAccessor, httpContext);

        try
        {
            var reverted = await history.RevertAsync(entityType.Id, request.Id, request.ChangeVector, httpContext.RequestAborted);
            return SparkAddOnEndpoints.Envelope(clientAccessor, reverted, StatusCodes.Status200OK);
        }
        catch (Exception ex) when (SparkAddOnEndpoints.IsConcurrencyConflict(ex))
        {
            return SparkAddOnEndpoints.ConcurrencyConflict(clientAccessor, ex);
        }
        catch (SparkValidationException ex)
        {
            return SparkAddOnEndpoints.ValidationFailed(clientAccessor, ex);
        }
        catch (SparkActionDisabledException ex)
        {
            return SparkAddOnEndpoints.ActionDisabled(clientAccessor, ex);
        }
        catch (SparkAccessDeniedException)
        {
            return SparkAddOnEndpoints.Refusal(clientAccessor, httpContext);
        }
    }
}
