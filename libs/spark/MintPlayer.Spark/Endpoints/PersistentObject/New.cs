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
/// Constructs a new, unsaved object and hands it to the client — a standalone New, or a row for an
/// <c>AsDetail</c> collection.
/// <para>
/// Construction, not persistence. Nothing is written; for an <c>AsDetail</c> row the parent still
/// owns the save, and the row reaches the database only when the parent is saved.
/// </para>
/// </summary>
internal sealed partial class NewPersistentObject : IPostEndpoint, IMemberOf<PersistentObjectGroup>
{
    public static string Path => "/{objectTypeId}/new";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder)
    {
        builder.WithMetadata(new RequireAntiforgeryTokenAttribute(true));
    }

    [Inject] private readonly IModelLoader modelLoader;
    [Inject] private readonly IPermissionService permissionService;
    [Inject] private readonly IClientAccessor clientAccessor;
    [Inject] private readonly IDatabaseAccess databaseAccess;
    [Inject] private readonly IEntityMapper entityMapper;
    [Inject] private readonly INewInvoker newInvoker;
    [Inject] private readonly ISparkTypeResolver typeResolver;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        var objectTypeId = httpContext.Request.RouteValues["objectTypeId"]!.ToString()!;

        var entityType = modelLoader.ResolveEntityType(objectTypeId);
        if (entityType is null)
        {
            return ClientResult.EnvelopeRefusal(clientAccessor, httpContext);
        }

        var request = await httpContext.Request.ReadFromJsonAsync<NewPersistentObjectRequest>()
            ?? new NewPersistentObjectRequest();

        try
        {
            return request.AsDetailAttribute is { Length: > 0 }
                ? await HandleAsDetailRowAsync(httpContext, entityType, request)
                : await HandleStandaloneAsync(httpContext, entityType, request);
        }
        catch (SparkValidationException ex)
        {
            // A construction hook may refuse outright — "this contract already has a signatory".
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
    /// A standalone New: the caller needs the <c>New</c> right on the type being constructed.
    /// </summary>
    private async Task<IResult> HandleStandaloneAsync(
        HttpContext httpContext, EntityTypeDefinition entityType, NewPersistentObjectRequest request)
    {
        var typeName = entityType.ClrType?.Split('.').Last() ?? entityType.Name;
        await permissionService.EnsureAuthorizedAsync("New", typeName);

        var po = entityMapper.GetPersistentObject(entityType.Id);
        await InvokeHookAsync(entityType, po, parent: null, asDetailParent: null, request, httpContext);
        return ClientResult.Envelope(clientAccessor, po, StatusCodes.Status200OK);
    }

    /// <summary>
    /// A row for a parent's <c>AsDetail</c> collection.
    /// </summary>
    /// <remarks>
    /// Three things here are load-bearing, and each closes a hole the obvious implementation leaves
    /// open.
    /// <list type="number">
    /// <item>
    /// <b>The right is the row type's own.</b> Adding a phone number to a person needs
    /// <c>New/PhoneNumber</c>, and the delete button on a row needs <c>Delete/PhoneNumber</c> — the
    /// grid's affordances are governed by the type in the grid. The client already works this way:
    /// it loads permissions for the <em>detail</em> type and gates the New and Delete buttons on
    /// them, so checking the parent's right here would have made the button and the endpoint
    /// disagree.
    /// <para>
    /// This is narrower than the rule the refresh path states for nested triggers, and deliberately
    /// so. Refresh reshapes a form and has no verb of its own, so it borrows the owner's. Adding and
    /// removing rows are real verbs that a deployment may want to grant separately — a person's
    /// details editable by many, their phone numbers by few.
    /// </para>
    /// </item>
    /// <item>
    /// <b>The parent is still loaded, and that load is still a gate.</b> The row type's right says
    /// the caller may create rows of this kind; it does not say which parent they may attach one to.
    /// The load applies the parent's Read right, collection guard and row filter, so a caller who
    /// cannot see a parent cannot add rows to it — nor learn that it exists.
    /// </item>
    /// <item>
    /// <b>The child type comes from the parent's schema, never from the request.</b> The route names
    /// a type, but it is only honoured once it matches the <c>AsDetailType</c> the parent's own
    /// attribute declares. Without that check a caller could name any type at all and have the
    /// framework construct it under a parent that has no such collection.
    /// </item>
    /// <item>
    /// <b>A saved parent is re-loaded server-side rather than accepted from the body.</b> Taking the
    /// client's word for a parent's contents is how a caller reaches one collection through
    /// another's permissions.
    /// </item>
    /// </list>
    /// </remarks>
    private async Task<IResult> HandleAsDetailRowAsync(
        HttpContext httpContext, EntityTypeDefinition entityType, NewPersistentObjectRequest request)
    {
        if (request.ParentType is not { Length: > 0 })
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

        // Not an AsDetail attribute of this parent, or a different child type than the route claims.
        // Refused identically to an unknown type, so neither answers "does this collection exist".
        if (attrDef is null
            || attrDef.DataType != "AsDetail"
            || attrDef.AsDetailType is null
            || !string.Equals(attrDef.AsDetailType, entityType.ClrType, StringComparison.Ordinal))
        {
            return ClientResult.EnvelopeRefusal(clientAccessor, httpContext);
        }

        // The row type's own right — New/PhoneNumber, not New/Person. Matches the button the client
        // renders, which is gated on the detail type's permissions.
        var rowTypeName = entityType.ClrType?.Split('.').Last() ?? entityType.Name;
        await permissionService.EnsureAuthorizedAsync("New", rowTypeName);

        Po? parent = null;
        if (request.ParentId is { Length: > 0 })
        {
            parent = await databaseAccess.GetPersistentObjectAsync(parentType.Id, request.ParentId!);
            if (parent is null)
            {
                return ClientResult.EnvelopeRefusal(clientAccessor, httpContext);
            }
        }

        var po = entityMapper.GetPersistentObject(entityType.Id);

        // Both references, and deliberately the same instance: they differ in meaning, not identity.
        // AsDetailParent is the narrow one that says the parent owns the save.
        await InvokeHookAsync(entityType, po, parent, asDetailParent: parent, request, httpContext);
        return ClientResult.Envelope(clientAccessor, po, StatusCodes.Status200OK);
    }

    private async Task InvokeHookAsync(
        EntityTypeDefinition entityType,
        Po po,
        Po? parent,
        Po? asDetailParent,
        NewPersistentObjectRequest request,
        HttpContext httpContext)
    {
        // Resolving the CLR type through the same resolver the rest of the framework uses keeps an
        // undeclared type unreachable here, exactly as it is on the save path.
        var clrType = typeResolver.Resolve(entityType.ClrType);
        if (clrType is null)
            return;

        await newInvoker.InvokeAsync(
            clrType,
            po,
            parent,
            asDetailParent,
            request.AsDetailAttribute,
            request.Parameters,
            httpContext.RequestAborted);
    }
}

internal sealed class NewPersistentObjectRequest
{
    /// <summary>Name of the parent's <c>AsDetail</c> attribute the row is for; absent for a standalone New.</summary>
    public string? AsDetailAttribute { get; set; }

    /// <summary>The parent's entity type — required whenever <see cref="AsDetailAttribute"/> is set.</summary>
    public string? ParentType { get; set; }

    /// <summary>
    /// The parent's id, or absent when the parent is itself unsaved. A present id is re-loaded
    /// server-side rather than trusted; an absent one means the hook is handed a null parent, which
    /// is the honest answer — there is no parent state the server can vouch for yet.
    /// </summary>
    public string? ParentId { get; set; }

    /// <summary>Free-form arguments, e.g. which variant a New menu chose.</summary>
    public Dictionary<string, string>? Parameters { get; set; }
}
