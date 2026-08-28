using Microsoft.AspNetCore.Antiforgery;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Abstractions.ClientOperations;
using MintPlayer.Spark.Abstractions.Retry;
using MintPlayer.Spark.Exceptions;
using System.Text.Json;
using MintPlayer.Spark.Services;
using Po = MintPlayer.Spark.Abstractions.PersistentObject;

namespace MintPlayer.Spark.Endpoints.PersistentObject;

/// <summary>
/// Reshapes an in-progress object in response to one attribute's value changing.
/// <para>
/// This endpoint writes nothing. It exists so that the form the user is filling in can be a function
/// of what they have typed so far, rather than only of the model file — and it is called far more
/// often than any other endpoint under <c>/spark/po</c>, potentially on every field blur, which is
/// why it never loads more than it must.
/// </para>
/// </summary>
internal sealed partial class RefreshPersistentObject : IPostEndpoint, IMemberOf<PersistentObjectGroup>
{
    public static string Path => "/{objectTypeId}/refresh";

    /// <summary>
    /// Advisory ceiling for one refresh: the row-gated load, plus room for a handler that looks a
    /// few things up. Exceeding it logs a warning rather than failing — the point is to make a
    /// runaway hook visible, not to guess a limit for application code.
    /// </summary>
    private const int RefreshRequestBudget = 20;

    static void IEndpointBase.Configure(RouteHandlerBuilder builder)
    {
        builder.WithMetadata(new RequireAntiforgeryTokenAttribute(true));
    }

    [Inject] private readonly IModelLoader modelLoader;
    [Inject] private readonly IPermissionService permissionService;
    [Inject] private readonly IClientAccessor clientAccessor;
    [Inject] private readonly IRetryAccessor retryAccessor;
    [Inject] private readonly IDatabaseAccess databaseAccess;
    [Inject] private readonly IEffectiveObjectFactory effectiveObjectFactory;
    [Inject] private readonly IRefreshInvoker refreshInvoker;
    [Inject] private readonly ISparkTypeResolver typeResolver;
    // The request-scoped session — the same instance IDatabaseAccess uses, so a scope opened here
    // covers the row-gated load below as well as anything the hook does.
    [Inject] private readonly Raven.Client.Documents.Session.IAsyncDocumentSession session;
    [Inject] private readonly ILogger<RefreshPersistentObject> logger;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        var objectTypeId = httpContext.Request.RouteValues["objectTypeId"]!.ToString()!;

        var entityType = modelLoader.ResolveEntityType(objectTypeId);
        if (entityType is null)
        {
            return ClientResult.EnvelopeRefusal(clientAccessor, httpContext);
        }

        var request = await httpContext.Request.ReadFromJsonAsync<RefreshPersistentObjectRequest>()
            ?? throw new InvalidOperationException("Request could not be deserialized from the request body.");

        var submitted = request.PersistentObject
            ?? throw new InvalidOperationException("PersistentObject is required.");

        var isNew = string.IsNullOrEmpty(submitted.Id);
        var typeName = entityType.ClrType?.Split('.').Last() ?? entityType.Name;

        // Vidyano maps a refresh onto New for a new object and Read for an existing one, and that is
        // the right vocabulary: a refresh reveals what the form would look like, which is a read of
        // the model, not a write. Introducing a "Refresh" verb would mean every application's
        // security.json had to grow a right before the feature worked at all.
        try
        {
            await permissionService.EnsureAuthorizedAsync(isNew ? "New" : "Read", typeName);
        }
        catch (SparkAccessDeniedException)
        {
            return ClientResult.EnvelopeRefusal(clientAccessor, httpContext);
        }

        if (request.RetryResults is { Length: > 0 } retryResults)
        {
            var accessor = (RetryAccessor)retryAccessor;
            accessor.AnsweredResults = retryResults.ToDictionary(r => r.Step);
        }

        // Refresh handlers are chatty by nature — they answer "what should this form look like
        // now", which usually means looking something up — and unlike load or save this runs on
        // every field blur. Fleet hit RavenDB's 30-request session cap inside a single Vidyano
        // OnRefresh for exactly this reason. The scope is restored on dispose, so a chatty hook
        // cannot silently elevate the budget for the rest of the request.
        using var _ = session.IgnoreMaxRequests(RefreshRequestBudget, logger);

        Po? existing = null;
        if (!isNew)
        {
            // Row security for an existing row. The load is the gate: GetPersistentObjectAsync runs
            // the Read right, the collection guard and IsAllowedAsync, and answers null for a row
            // the caller may not see — so a caller who cannot read a row cannot use refresh to learn
            // that it exists, nor to run a hook against it. It also applies attribute redaction,
            // which is used below.
            //
            // Deliberately by entityType.Id from the ROUTE, never by the ObjectTypeId on the wire
            // object: taking the client's word for the type is how a caller reads one collection
            // through another's permissions (security sweep C3).
            existing = await databaseAccess.GetPersistentObjectAsync(entityType.Id, submitted.Id!);
            if (existing is null)
            {
                return ClientResult.EnvelopeRefusal(clientAccessor, httpContext);
            }
        }

        var effective = effectiveObjectFactory.Build(entityType, submitted);

        // A trigger inside a detail grid is addressed by path — "Jobs[1].ProfessionId" — and belongs
        // to the ROW's type, not this one. CarreerJob.ProfessionId reaches CarreerJobActions, because
        // the hook that owns a type's shape is that type's own; the row is handed its Parent for the
        // context it cannot have alone.
        //
        // Authorization stays on the type in the ROUTE regardless. Nested AsDetail types are not in
        // security.json — nobody grants rights on CarreerJob — so the right that governs editing a
        // row is the one governing the object that owns it.
        if (NestedTrigger.TryParse(request.TriggeredBy) is { } nested
            && BuildNestedRow(entityType, effective, nested) is { } row)
        {
            await InvokeFor(row.EntityType, row.Object, nested.Column, isNew, httpContext);
            return ClientResult.Envelope(clientAccessor, row.Object, StatusCodes.Status200OK);
        }

        await InvokeFor(entityType, effective, request.TriggeredBy, isNew, httpContext);

        if (existing is not null)
        {
            ApplyRedactionOf(existing, entityType, effective);
        }

        return ClientResult.Envelope(clientAccessor, effective, StatusCodes.Status200OK);
    }

    private async Task InvokeFor(
        EntityTypeDefinition entityType, Po obj, string? triggeredBy, bool isNew, HttpContext httpContext)
    {
        var clrType = typeResolver.Resolve(entityType.ClrType);
        if (clrType is null)
            return;

        await refreshInvoker.InvokeAsync(clrType, obj, triggeredBy, isNew, httpContext.RequestAborted);
    }

    /// <summary>
    /// Scaffolds the addressed detail row from its own model, carrying the submitted row's values,
    /// and links it to <paramref name="parent"/>. Null when the path does not resolve — an unknown
    /// attribute, one that is not an AsDetail, or a row index nobody sent.
    /// </summary>
    private (EntityTypeDefinition EntityType, Po Object)? BuildNestedRow(
        EntityTypeDefinition parentType, Po parent, NestedTrigger nested)
    {
        var attribute = parentType.Attributes
            .FirstOrDefault(a => string.Equals(a.Name, nested.Attribute, StringComparison.Ordinal));

        if (attribute?.AsDetailType is null)
            return null;

        var nestedType = modelLoader.GetEntityTypeByClrType(attribute.AsDetailType);
        if (nestedType is null)
            return null;

        var rows = parent.Attributes
            .FirstOrDefault(a => string.Equals(a.Name, nested.Attribute, StringComparison.Ordinal))
            ?.Value;

        var submittedRow = RowAt(rows, nested.Index);

        var row = effectiveObjectFactory.Build(nestedType, submittedRow);
        row.Parent = parent;

        return (nestedType, row);
    }

    /// <summary>
    /// Reads one row out of an AsDetail attribute's value. Rows arrive as JSON, so this reaches into
    /// a <see cref="JsonElement"/> rather than a typed collection.
    /// </summary>
    private static Po? RowAt(object? rows, int index)
    {
        if (rows is not JsonElement { ValueKind: JsonValueKind.Array } array)
            return null;

        if (index < 0 || index >= array.GetArrayLength())
            return null;

        var element = array[index];
        if (element.ValueKind != JsonValueKind.Object)
            return null;

        // A row is a flat dictionary of values, not a PersistentObject — that is the shape the form
        // holds and the shape it posts. Lift it into the attribute list Build expects.
        var attributes = element.EnumerateObject()
            .Select(property => new PersistentObjectAttribute
            {
                Name = property.Name,
                Value = property.Value.Clone(),
                IsValueChanged = true,
            })
            .ToArray();

        return new Po { Name = string.Empty, ObjectTypeId = Guid.Empty, Attributes = attributes };
    }

    /// <summary>
    /// Carries the load's attribute redaction onto the reshaped object.
    /// <para>
    /// The reshaped object is scaffolded from the model, so it does <b>not</b> inherit what
    /// <see cref="IDatabaseAccess.GetPersistentObjectAsync"/> redacted — every protected attribute
    /// comes back with its metadata intact and an empty value slot the hook is free to fill.
    /// Without this, refresh is a way to read around <c>GetProtectedAttributesAsync</c>.
    /// </para>
    /// <para>
    /// A redacted attribute is identified as the delta between the model and the load: the model
    /// declares it visible and the loaded object does not. That is exact, and it costs nothing —
    /// the alternative, calling <c>RedactAsync</c> again, needs the row entity and so a second
    /// database round trip on the hottest endpoint in <c>/spark/po</c>.
    /// </para>
    /// <para>
    /// Applied <em>after</em> the hook, and as an intersection rather than an assignment: a hook may
    /// legitimately hide an attribute, and must never be able to reveal one.
    /// </para>
    /// </summary>
    private static void ApplyRedactionOf(Po existing, EntityTypeDefinition entityType, Po effective)
    {
        var declaredVisible = entityType.Attributes
            .Where(a => a.IsVisible)
            .Select(a => a.Name)
            .ToHashSet(StringComparer.Ordinal);

        var redacted = existing.Attributes
            .Where(a => !a.IsVisible && declaredVisible.Contains(a.Name))
            .Select(a => a.Name)
            .ToHashSet(StringComparer.Ordinal);

        if (redacted.Count == 0)
            return;

        foreach (var attribute in effective.Attributes)
        {
            if (!redacted.Contains(attribute.Name))
                continue;

            attribute.Value = null;
            attribute.IsVisible = false;
        }
    }
}

/// <summary>
/// The refresh request body. <see cref="TriggeredBy"/> is the attribute's <em>name</em> rather than
/// its id: attributes scaffolded from the model carry no id, so a name is the only identifier both
/// sides always have. For a trigger inside an AsDetail row it is the path form the inline validation
/// errors already use — <c>Jobs[2].ProfessionId</c>.
/// </summary>
/// <summary>
/// A trigger addressed inside a detail grid: <c>Jobs[1].ProfessionId</c>. The same path form the
/// inline validation errors already use, so there is one addressing scheme rather than two.
/// </summary>
internal readonly record struct NestedTrigger(string Attribute, int Index, string Column)
{
    public static NestedTrigger? TryParse(string? triggeredBy)
    {
        if (string.IsNullOrEmpty(triggeredBy))
            return null;

        var open = triggeredBy.IndexOf('[');
        if (open <= 0)
            return null;

        var close = triggeredBy.IndexOf(']', open);
        if (close < 0 || close + 2 >= triggeredBy.Length || triggeredBy[close + 1] != '.')
            return null;

        if (!int.TryParse(triggeredBy[(open + 1)..close], out var index))
            return null;

        return new NestedTrigger(triggeredBy[..open], index, triggeredBy[(close + 2)..]);
    }
}

internal sealed class RefreshPersistentObjectRequest
{
    public Po? PersistentObject { get; set; }
    public string? TriggeredBy { get; set; }
    public RetryResult[]? RetryResults { get; set; }
}
