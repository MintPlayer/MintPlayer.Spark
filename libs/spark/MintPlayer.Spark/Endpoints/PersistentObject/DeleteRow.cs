using Microsoft.AspNetCore.Antiforgery;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Abstractions.ClientOperations;
using MintPlayer.Spark.Exceptions;
using MintPlayer.Spark.Services;
using Po = MintPlayer.Spark.Abstractions.PersistentObject;

namespace MintPlayer.Spark.Endpoints.PersistentObject;

/// <summary>
/// Asks whether a row may leave its parent's <c>AsDetail</c> collection — the counterpart to
/// <see cref="NewPersistentObject"/>, and the half of the lifecycle that was never written.
/// <para>
/// Consultation, not deletion. Nothing is written: a <c>200</c> means the client may splice the row
/// out of the collection it is editing, and the removal reaches the database only when the parent is
/// saved. A hook that refuses answers <c>400</c> with a message the user can read.
/// </para>
/// </summary>
/// <remarks>
/// ⚠️ <b>A POST, for an operation whose verb is delete.</b> Two reasons, and the first is decisive:
/// the framework's real delete endpoint is <c>DELETE /{objectTypeId}/{**id}</c>, whose catch-all
/// segment would swallow any <c>DELETE</c> route added beside it. The second is that this needs a
/// body — the parent, the collection, the row key — and a request body on <c>DELETE</c> is
/// famously ill-served by intermediaries.
/// <para>
/// ⚠️ <b>This endpoint is not the enforcement point for <c>Delete/{RowType}</c>.</b> It checks the
/// right, so a caller poking the API directly is refused; but a caller who never calls it and simply
/// submits the parent with the row missing never passes through here at all. The save path's own
/// unconditional per-row check is what covers that caller, and it must never be made conditional on
/// <see cref="EntityTypeDefinition.ServerSideRowLifecycle"/> — that flag is outside the model hash,
/// so gating a rights check on it would put a security decision one unhashed model edit away from
/// being switched off.
/// </para>
/// </remarks>
internal sealed partial class DeleteRowPersistentObject : IPostEndpoint, IMemberOf<PersistentObjectGroup>
{
    public static string Path => "/{objectTypeId}/delete-row";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder)
    {
        builder.WithMetadata(new RequireAntiforgeryTokenAttribute(true));
    }

    [Inject] private readonly IModelLoader modelLoader;
    [Inject] private readonly IPermissionService permissionService;
    [Inject] private readonly IClientAccessor clientAccessor;
    [Inject] private readonly IDatabaseAccess databaseAccess;
    [Inject] private readonly IDeleteRowInvoker deleteRowInvoker;
    [Inject] private readonly ISparkTypeResolver typeResolver;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        var objectTypeId = httpContext.Request.RouteValues["objectTypeId"]!.ToString()!;

        var entityType = modelLoader.ResolveEntityType(objectTypeId);
        if (entityType is null)
        {
            return ClientResult.EnvelopeRefusal(clientAccessor, httpContext);
        }

        var request = await httpContext.Request.ReadFromJsonAsync<DeleteRowRequest>()
            ?? new DeleteRowRequest();

        try
        {
            return await HandleCoreAsync(httpContext, entityType, request);
        }
        catch (SparkValidationException ex)
        {
            // A hook refused — "this invoice line has already been settled". The one outcome of this
            // endpoint a user is meant to read.
            return ClientResult.Envelope(clientAccessor, new { errors = new[] { ex.ToError() } }, 400);
        }
        catch (SparkRowLevelAccessDeniedException)
        {
            return ClientResult.EnvelopeRefusal(clientAccessor, httpContext);
        }
        catch (SparkAccessDeniedException)
        {
            return ClientResult.EnvelopeRefusal(clientAccessor, httpContext);
        }
    }

    /// <summary>
    /// The same four rules <see cref="NewPersistentObject"/> documents, in the same order, because
    /// the two endpoints are exposed to exactly the same abuses: the row type's own right; the
    /// parent loaded server-side as a second gate; the child type taken from the parent's schema
    /// rather than the request; and the parent's contents never accepted from the body.
    /// </summary>
    /// <remarks>
    /// One rule is added that New has no need of: <b>the row must already be stored</b>. A key the
    /// parent's collection does not contain is refused identically to an unknown type, so the
    /// endpoint cannot be used to ask which keys exist. It is also the reason
    /// <see cref="Actions.SparkDeleteRowArgs{T}.Parent"/> is non-nullable — an unsaved parent has no
    /// stored rows, so there is nothing here for a client to ask about, and it does not ask.
    /// </remarks>
    private async Task<IResult> HandleCoreAsync(
        HttpContext httpContext, EntityTypeDefinition entityType, DeleteRowRequest request)
    {
        if (request.AsDetailAttribute is not { Length: > 0 }
            || request.ParentType is not { Length: > 0 }
            || request.ParentId is not { Length: > 0 }
            || request.RowKey is not { Length: > 0 })
        {
            return ClientResult.EnvelopeRefusal(clientAccessor, httpContext);
        }

        var parentType = modelLoader.ResolveEntityType(request.ParentType);
        if (parentType is null)
        {
            return ClientResult.EnvelopeRefusal(clientAccessor, httpContext);
        }

        var attrDef = parentType.Attributes?.FirstOrDefault(
            a => string.Equals(a.Name, request.AsDetailAttribute, StringComparison.Ordinal));

        if (attrDef is null
            || attrDef.DataType != "AsDetail"
            || attrDef.AsDetailType is null
            || !string.Equals(attrDef.AsDetailType, entityType.ClrType, StringComparison.Ordinal))
        {
            return ClientResult.EnvelopeRefusal(clientAccessor, httpContext);
        }

        // The row type's own right — Delete/PhoneNumber, not Delete/Person. Matches the button the
        // client renders, which is gated on the detail type's permissions.
        var rowTypeName = entityType.ClrType?.Split('.').Last() ?? entityType.Name;
        await permissionService.EnsureAuthorizedAsync("Delete", rowTypeName);

        // Applies the parent's Read right, collection guard and row filter, so a caller who cannot
        // see a parent cannot ask about its rows — nor learn that it exists.
        var parent = await databaseAccess.GetPersistentObjectAsync(parentType.Id, request.ParentId!);
        if (parent is null)
        {
            return ClientResult.EnvelopeRefusal(clientAccessor, httpContext);
        }

        var row = FindStoredRow(parent, request.AsDetailAttribute!, request.RowKey!);
        if (row is null)
        {
            return ClientResult.EnvelopeRefusal(clientAccessor, httpContext);
        }

        var clrType = typeResolver.Resolve(entityType.ClrType);
        if (clrType is not null)
        {
            await deleteRowInvoker.InvokeAsync(
                clrType,
                row,
                parent,
                request.AsDetailAttribute!,
                request.RowKey!,
                request.Parameters,
                httpContext.RequestAborted);
        }

        // Nothing to return but the verdict. The body is deliberately not the row: echoing it back
        // would invite a client to treat this as the delete itself.
        return ClientResult.Envelope(clientAccessor, new { removed = true }, StatusCodes.Status200OK);
    }

    /// <summary>
    /// The stored row whose key matches, or <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// Matched on <see cref="Po.Id"/>, which is where <c>EntityMapper</c> puts a value object's
    /// registered row key — not necessarily a property called <c>Id</c>. Reading the key off the
    /// mapped object rather than re-reflecting the entity is what keeps this agreeing with the
    /// client, which receives the same value as <c>__sparkRowKey</c>.
    /// </remarks>
    private static Po? FindStoredRow(Po parent, string asDetailAttribute, string rowKey)
    {
        var attr = parent.Attributes.FirstOrDefault(
            a => string.Equals(a.Name, asDetailAttribute, StringComparison.Ordinal));

        if (attr is not PersistentObjectAttributeAsDetail detail)
            return null;

        // A single (non-array) AsDetail has one row and the key still has to match: a client naming
        // the wrong key is asking about a row that is not there, whether or not the collection
        // happens to hold exactly one.
        if (detail.Objects is null)
        {
            return string.Equals(detail.Object?.Id, rowKey, StringComparison.Ordinal) ? detail.Object : null;
        }

        return detail.Objects.FirstOrDefault(o => string.Equals(o.Id, rowKey, StringComparison.Ordinal));
    }
}

internal sealed class DeleteRowRequest
{
    /// <summary>Name of the parent's <c>AsDetail</c> attribute the row is being removed from.</summary>
    public string? AsDetailAttribute { get; set; }

    /// <summary>The parent's entity type.</summary>
    public string? ParentType { get; set; }

    /// <summary>
    /// The parent's id. Required, unlike on the New path: a row on an unsaved parent is not stored,
    /// so there is nothing to consult and the client never asks.
    /// </summary>
    public string? ParentId { get; set; }

    /// <summary>The row's key, as the client received it in <c>__sparkRowKey</c>.</summary>
    public string? RowKey { get; set; }

    /// <summary>Free-form arguments, mirroring the New path.</summary>
    public Dictionary<string, string>? Parameters { get; set; }
}
