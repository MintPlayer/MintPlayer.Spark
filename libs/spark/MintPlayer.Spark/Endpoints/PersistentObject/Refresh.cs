using Microsoft.AspNetCore.Antiforgery;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Abstractions.ClientOperations;
using MintPlayer.Spark.Abstractions.Retry;
using MintPlayer.Spark.Exceptions;
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
        var typeName = entityType.ClrType.Split('.').Last();

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

        var clrType = typeResolver.Resolve(entityType.ClrType);
        if (clrType is not null)
        {
            await refreshInvoker.InvokeAsync(
                clrType, effective, request.TriggeredBy, isNew, httpContext.RequestAborted);
        }

        if (existing is not null)
        {
            ApplyRedactionOf(existing, entityType, effective);
        }

        return ClientResult.Envelope(clientAccessor, effective, StatusCodes.Status200OK);
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
internal sealed class RefreshPersistentObjectRequest
{
    public Po? PersistentObject { get; set; }
    public string? TriggeredBy { get; set; }
    public RetryResult[]? RetryResults { get; set; }
}
