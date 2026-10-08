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

namespace MintPlayer.Spark.SoftDelete.Endpoints;

/// <summary>
/// <c>/spark/po</c>, shared with core's persistent-object endpoints: the route table is fully
/// literal, and ids — which contain slashes — travel in the body (#460, T1).
/// </summary>
internal class SoftDeletePersistentObjectGroup : IEndpointGroup
{
    public static string Prefix => "/spark/po";
}

/// <summary>The body of <c>POST /spark/po/restore</c> and <c>POST /spark/po/purge</c>.</summary>
internal sealed class SoftDeleteRequest : ISparkTypedRequest
{
    /// <inheritdoc />
    public string? ObjectTypeId { get; set; }

    /// <summary>The row's id.</summary>
    public string? Id { get; set; }

    /// <summary>
    /// <c>purge</c> only, and required there (#467, D14): the version of the deleted row the caller
    /// saw. Ignored by <c>restore</c>.
    /// </summary>
    public string? Etag { get; set; }
}

/// <summary>
/// <c>POST /spark/po/restore { objectTypeId, id }</c> → 200 with the restored object. Requires
/// <c>Restore/T</c>; the row must be deleted. Anything the caller may not act on is the standard
/// refusal (401 anonymous / 404), a disabled restore is 403 naming the action.
/// </summary>
[MemberOf<SoftDeletePersistentObjectGroup>]
internal sealed partial class RestorePersistentObject : IPostEndpoint<SoftDeleteRequest>
{
    public static string Path => "/restore";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.WithMetadata(new RequireAntiforgeryTokenAttribute(true));

    [Inject] private readonly ISparkSoftDelete softDelete;
    [Inject] private readonly IDatabaseAccess databaseAccess;
    [Inject] private readonly ISparkAddOnEndpoints addOn;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    /// <summary>A body that cannot be bound is the standard refusal, as an unusable request is below; never a parse error (PRD D3a).</summary>
    protected override ValueTask<IResult> OnBindFailedAsync(HttpContext context, EndpointBindingException? failure)
        => new(addOn.Refusal(context));

    public override async Task<IResult> HandleAsync(SoftDeleteRequest request, CancellationToken cancellationToken)
    {
        var httpContext = httpContextAccessor.HttpContext!;
        var entityType = addOn.ResolveType(request);
        if (entityType is null || string.IsNullOrEmpty(request.Id))
            return addOn.Refusal(httpContext);

        try
        {
            await softDelete.RestoreAsync(entityType.Id, request.Id, httpContext.RequestAborted);
            // Read back through the row-gated path, like every other endpoint that returns an object.
            var restored = await databaseAccess.GetPersistentObjectAsync(entityType.Id, request.Id);
            return addOn.Envelope(restored, StatusCodes.Status200OK);
        }
        catch (SparkCancelException)
        {
            // An interceptor cancelled the restore (#482): nothing was written; answered with the row as stored.
            return addOn.Envelope(
                await databaseAccess.GetPersistentObjectAsync(entityType.Id, request.Id), StatusCodes.Status200OK);
        }
        catch (Exception ex) when (addOn.IsConcurrencyConflict(ex))
        {
            // A restore is a save, written with the version it loaded: a concurrent edit is a 409.
            return addOn.ConcurrencyConflict(ex);
        }
        catch (SparkValidationException ex)
        {
            return addOn.ValidationFailed(ex);
        }
        catch (SparkActionDisabledException ex)
        {
            return addOn.ActionDisabled(ex);
        }
        catch (SparkAccessDeniedException)
        {
            return addOn.Refusal(httpContext);
        }
    }
}

/// <summary>
/// <c>POST /spark/po/purge { objectTypeId, id }</c> → 204. Requires <c>Purge/T</c>; the row must
/// already be deleted. Removes the document and every revision of it; cannot be undone.
/// </summary>
[MemberOf<SoftDeletePersistentObjectGroup>]
internal sealed partial class PurgePersistentObject : IPostEndpoint<SoftDeleteRequest>
{
    public static string Path => "/purge";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.WithMetadata(new RequireAntiforgeryTokenAttribute(true));

    [Inject] private readonly ISparkSoftDelete softDelete;
    [Inject] private readonly ISparkAddOnEndpoints addOn;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    /// <summary>A body that cannot be bound is the standard refusal, as an unusable request is below; never a parse error (PRD D3a).</summary>
    protected override ValueTask<IResult> OnBindFailedAsync(HttpContext context, EndpointBindingException? failure)
        => new(addOn.Refusal(context));

    public override async Task<IResult> HandleAsync(SoftDeleteRequest request, CancellationToken cancellationToken)
    {
        var httpContext = httpContextAccessor.HttpContext!;
        var entityType = addOn.ResolveType(request);
        if (entityType is null || string.IsNullOrEmpty(request.Id))
            return addOn.Refusal(httpContext);

        // A purge says which version it removes (#467, D14), as every delete does.
        if (string.IsNullOrEmpty(request.Etag))
            return addOn.Envelope(
                new { error = "A purge must carry the etag of the version it removes." }, StatusCodes.Status400BadRequest);

        try
        {
            await softDelete.PurgeAsync(entityType.Id, request.Id, request.Etag, httpContext.RequestAborted);
            return addOn.Envelope(null, StatusCodes.Status204NoContent);
        }
        catch (SparkCancelException)
        {
            // An interceptor cancelled the purge (#482): nothing was purged, and nothing went wrong.
            return addOn.Envelope(null, StatusCodes.Status204NoContent);
        }
        catch (Exception ex) when (addOn.IsConcurrencyConflict(ex))
        {
            // Changed since the caller saw it — restored and edited, say: nothing was purged.
            return addOn.ConcurrencyConflict(ex);
        }
        catch (SparkValidationException ex)
        {
            return addOn.ValidationFailed(ex);
        }
        catch (SparkActionDisabledException ex)
        {
            return addOn.ActionDisabled(ex);
        }
        catch (SparkAccessDeniedException)
        {
            return addOn.Refusal(httpContext);
        }
    }
}
