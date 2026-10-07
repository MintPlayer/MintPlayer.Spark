using MintPlayer.Spark.Abstractions.Requests;
using MintPlayer.Spark.Abstractions.Retry;
using Microsoft.AspNetCore.Antiforgery;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Abstractions.ClientOperations;
using MintPlayer.Spark.Exceptions;
using MintPlayer.Spark.Actions;
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
[MemberOf<PersistentObjectGroup>]
internal sealed partial class NewPersistentObject : IPostEndpoint
{
    public static string Path => "/new";

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
    [Inject] private readonly IRetryAccessor retryAccessor;
    [Inject] private readonly IQueryLoader queryLoader;
    [Inject] private readonly ILogger<NewPersistentObject> logger;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        var (request, entityType) = await SparkRequestType.ReadAsync<NewPersistentObjectRequest>(httpContext, modelLoader);
        if (request is null || entityType is null)
        {
            return ClientResult.EnvelopeRefusal(clientAccessor, httpContext);
        }

        RetryScope.Accept(retryAccessor, request);

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
        var typeName = entityType.Name;
        await permissionService.EnsureAuthorizedAsync("New", typeName);

        // A New started from a sub-query on a parent's detail page (#460, D19). All three fields or
        // none; each is verified rather than trusted, and every mismatch is refused exactly like a
        // missing row, so none of them answers "does this parent / query exist".
        // The create that follows resolves the same parent the same way (SubQueryNewParent).
        var (resolved, refused) = await SubQueryNewParent.ResolveAsync(
            modelLoader, queryLoader, databaseAccess, entityType, request.ParentId, request.ParentType, request.QueryId);
        if (refused)
            return ClientResult.EnvelopeRefusal(clientAccessor, httpContext);

        Po? parent = resolved?.Parent;
        SparkNewSubQueryContext? subQuery = resolved is null ? null
            : new SparkNewSubQueryContext(entityType, resolved.ParentType, resolved.Query, resolved.ParentReference, logger);

        var clrType = typeResolver.Resolve(entityType.ClrType);
        // Built for the caller (D13a): Read-denied absent, New-denied read-only, before the hook runs;
        // a value the hook writes onto a removed attribute goes nowhere.
        var po = await ScaffoldAsync(entityType, clrType, httpContext.RequestAborted);
        await InvokeHookAsync(clrType, po, parent, asDetailParent: null, request, httpContext, subQuery);
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
        var rowTypeName = entityType.Name;
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

        // Resolving the CLR type through the same resolver the rest of the framework uses keeps an
        // undeclared type unreachable here, exactly as it is on the save path.
        var clrType = typeResolver.Resolve(entityType.ClrType);
        var po = await ScaffoldAsync(entityType, clrType, httpContext.RequestAborted);

        // Both references, and deliberately the same instance: they differ in meaning, not identity.
        // AsDetailParent is the narrow one that says the parent owns the save.
        await InvokeHookAsync(clrType, po, parent, asDetailParent: parent, request, httpContext);
        return ClientResult.Envelope(clientAccessor, po, StatusCodes.Status200OK);
    }

    /// <summary>
    /// Builds the object the hook is handed: the model's shape, then a freshly constructed CLR
    /// instance reflected over it.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>The second half is not decoration.</b> <c>GetPersistentObjectAsync</c> scaffolds from the
    /// model file and never constructs the entity, so the row-key field initializer that every
    /// <c>[ValueObject]</c> has carried since #382 never runs — the row reaches the hook, and the
    /// client, with <b>no key at all</b>.
    /// <para>
    /// That is survivable by accident on the save path (an unmatched row correctly takes the
    /// create branch) but not here: a construction hook cannot reference the row it is building —
    /// for an audit entry, a cross-row default, or a parent-scoped <c>(root id, child key)</c> pair
    /// — if the row has no identity yet, and a client that round-trips a keyless row through
    /// <c>__sparkRowKey</c> sends back nothing.
    /// </para>
    /// <para>
    /// Constructing the instance settles both halves at once, and the second is the reason to
    /// prefer it over minting a bare guid: every C# property initializer becomes the default the
    /// user sees, which is the ordinary way a .NET developer expects to state one.
    /// </para>
    /// </remarks>
    private async Task<Po> ScaffoldAsync(EntityTypeDefinition entityType, Type? clrType, CancellationToken cancellationToken)
    {
        var po = await entityMapper.GetPersistentObjectAsync(entityType.Id, SparkCoreActions.New, cancellationToken);
        if (clrType is null)
            return po;

        // A type with no accessible parameterless constructor is not an error — it simply cannot
        // contribute defaults, so the model's shape stands and the hook is handed that.
        object? instance;
        try
        {
            instance = Activator.CreateInstance(clrType);
        }
        catch (Exception ex) when (ex is MissingMethodException or MemberAccessException)
        {
            return po;
        }

        if (instance is not null)
            entityMapper.PopulateAttributeValues(po, instance);
        return po;
    }

    private async Task InvokeHookAsync(
        Type? clrType,
        Po po,
        Po? parent,
        Po? asDetailParent,
        NewPersistentObjectRequest request,
        HttpContext httpContext,
        SparkNewSubQueryContext? subQuery = null)
    {
        if (clrType is null)
            return;

        await newInvoker.InvokeAsync(
            clrType,
            po,
            parent,
            asDetailParent,
            request.AsDetailAttribute,
            request.Parameters,
            httpContext.RequestAborted,
            subQuery);
    }
}

internal sealed class NewPersistentObjectRequest : ISparkTypedRequest, IRetryableRequest
{
    /// <inheritdoc />
    public string? ObjectTypeId { get; set; }

    /// <inheritdoc />
    public RetryResult[]? RetryResults { get; set; }

    /// <summary>Name of the parent's <c>AsDetail</c> attribute the row is for; absent for a standalone New.</summary>
    public string? AsDetailAttribute { get; set; }

    /// <summary>
    /// The parent's entity type — required whenever <see cref="AsDetailAttribute"/> is set, and for a
    /// New started from a sub-query (with <see cref="ParentId"/> and <see cref="QueryId"/>, #460 D19).
    /// </summary>
    public string? ParentType { get; set; }

    /// <summary>
    /// The sub-query New was started from, by id or alias; with <see cref="ParentType"/> and
    /// <see cref="ParentId"/> and no <see cref="AsDetailAttribute"/> (#460, D19). The parent's type must
    /// declare it among its <c>Queries</c>, and it must list the type being constructed.
    /// </summary>
    public string? QueryId { get; set; }

    /// <summary>
    /// The parent's id, or absent when the parent is itself unsaved. A present id is re-loaded
    /// server-side rather than trusted; an absent one means the hook is handed a null parent, which
    /// is the honest answer — there is no parent state the server can vouch for yet.
    /// </summary>
    public string? ParentId { get; set; }

    /// <summary>Free-form arguments, e.g. which variant a New menu chose.</summary>
    public Dictionary<string, string>? Parameters { get; set; }
}
