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
internal sealed partial class RestorePersistentObject : IPostEndpoint
{
    public static string Path => "/restore";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder)
        => builder.WithMetadata(new RequireAntiforgeryTokenAttribute(true));

    [Inject] private readonly ISparkSoftDelete softDelete;
    [Inject] private readonly IDatabaseAccess databaseAccess;
    [Inject] private readonly IModelLoader modelLoader;
    [Inject] private readonly IClientAccessor clientAccessor;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        var (request, entityType) = await SparkAddOnEndpoints.ReadTypedRequestAsync<SoftDeleteRequest>(httpContext, modelLoader);
        if (request is null || entityType is null || string.IsNullOrEmpty(request.Id))
            return SparkAddOnEndpoints.Refusal(clientAccessor, httpContext);

        try
        {
            await softDelete.RestoreAsync(entityType.Id, request.Id, httpContext.RequestAborted);
            // Read back through the row-gated path, like every other endpoint that returns an object.
            var restored = await databaseAccess.GetPersistentObjectAsync(entityType.Id, request.Id);
            return SparkAddOnEndpoints.Envelope(clientAccessor, restored, StatusCodes.Status200OK);
        }
        catch (SparkCancelException)
        {
            // An interceptor cancelled the restore (#482): nothing was written; answered with the row as stored.
            return SparkAddOnEndpoints.Envelope(clientAccessor,
                await databaseAccess.GetPersistentObjectAsync(entityType.Id, request.Id), StatusCodes.Status200OK);
        }
        catch (Exception ex) when (SparkAddOnEndpoints.IsConcurrencyConflict(ex))
        {
            // A restore is a save, written with the version it loaded: a concurrent edit is a 409.
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

/// <summary>
/// <c>POST /spark/po/purge { objectTypeId, id }</c> → 204. Requires <c>Purge/T</c>; the row must
/// already be deleted. Removes the document and every revision of it; cannot be undone.
/// </summary>
[MemberOf<SoftDeletePersistentObjectGroup>]
internal sealed partial class PurgePersistentObject : IPostEndpoint
{
    public static string Path => "/purge";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder)
        => builder.WithMetadata(new RequireAntiforgeryTokenAttribute(true));

    [Inject] private readonly ISparkSoftDelete softDelete;
    [Inject] private readonly IModelLoader modelLoader;
    [Inject] private readonly IClientAccessor clientAccessor;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        var (request, entityType) = await SparkAddOnEndpoints.ReadTypedRequestAsync<SoftDeleteRequest>(httpContext, modelLoader);
        if (request is null || entityType is null || string.IsNullOrEmpty(request.Id))
            return SparkAddOnEndpoints.Refusal(clientAccessor, httpContext);

        // A purge says which version it removes (#467, D14), as every delete does.
        if (string.IsNullOrEmpty(request.Etag))
            return SparkAddOnEndpoints.Envelope(clientAccessor,
                new { error = "A purge must carry the etag of the version it removes." }, StatusCodes.Status400BadRequest);

        try
        {
            await softDelete.PurgeAsync(entityType.Id, request.Id, request.Etag, httpContext.RequestAborted);
            return SparkAddOnEndpoints.Envelope(clientAccessor, null, StatusCodes.Status204NoContent);
        }
        catch (SparkCancelException)
        {
            // An interceptor cancelled the purge (#482): nothing was purged, and nothing went wrong.
            return SparkAddOnEndpoints.Envelope(clientAccessor, null, StatusCodes.Status204NoContent);
        }
        catch (Exception ex) when (SparkAddOnEndpoints.IsConcurrencyConflict(ex))
        {
            // Changed since the caller saw it — restored and edited, say: nothing was purged.
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
