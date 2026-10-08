using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.ClientOperations;
using MintPlayer.Spark.Abstractions.Requests;
using MintPlayer.Spark.Exceptions;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Endpoints;

/// <summary>
/// The response and request conventions of Spark's own endpoints, for add-on packages that map
/// endpoints of their own under <c>/spark/*</c> (SoftDelete's <c>/spark/po/restore</c>, History's
/// <c>/spark/po/revisions</c>, #460 T1). Scoped: inject it into the endpoint with <c>[Inject]</c>.
/// </summary>
/// <remarks>
/// <para>
/// An add-on endpoint must answer exactly like a core one: the same envelope
/// (<c>{ result, operations }</c>), and above all the same refusal. A refusal is deliberately
/// indistinguishable across "no such type", "no such row", "you may not" and "malformed body" —
/// 401 for an anonymous caller, 404 otherwise (#453) — and an add-on that invented its own would
/// reopen the existence oracle core closed. So the rules live here, once.
/// </para>
/// <para>
/// An instance service rather than a static class, so the request's client accessor, model and
/// row-policy state come from the endpoint's own scope instead of a <c>RequestServices</c> lookup.
/// </para>
/// </remarks>
public interface ISparkAddOnEndpoints
{
    /// <summary>
    /// The entity type <paramref name="request"/> names, or null when it names none or one the model
    /// does not declare — answer that with <see cref="Refusal"/>. The type always comes from the
    /// request's top-level <c>objectTypeId</c>, never from the document it carries.
    /// </summary>
    /// <remarks>
    /// An add-on endpoint is typed (<c>IPostEndpoint&lt;TRequest&gt;</c>), so the body arrives bound: it
    /// resolves the type with this and answers a body that cannot be bound with <see cref="Refusal"/>
    /// from <c>OnBindFailedAsync</c>, as core's endpoints do (endpoints generator completion, PRD D3a).
    /// </remarks>
    EntityTypeDefinition? ResolveType(ISparkTypedRequest? request);

    /// <summary>The standard <c>{ result, operations }</c> envelope with <paramref name="statusCode"/>.</summary>
    IResult Envelope(object? result, int statusCode);

    /// <summary>The standard refusal: 401 for an anonymous caller, 404 otherwise, in the envelope.</summary>
    IResult Refusal(HttpContext httpContext);

    /// <summary>A 400 carrying the validation error, in the envelope (<c>{ errors: [...] }</c>).</summary>
    IResult ValidationFailed(SparkValidationException exception);

    /// <summary>
    /// A 403 naming the withheld action (#460, D13). Only for a row the caller could see — the
    /// disabled-action gate runs after the row gate, which is what makes naming it safe.
    /// </summary>
    IResult ActionDisabled(SparkActionDisabledException exception);

    /// <summary>
    /// A 429 for a business quota, in the envelope, with <c>Retry-After</c> when known (#460, M12).
    /// </summary>
    IResult Throttled(HttpContext httpContext, SparkThrottledException exception);

    /// <summary>
    /// Whether <paramref name="exception"/> is the optimistic-concurrency refusal of a save (the
    /// posted etag is not the stored change vector). Answer it with <see cref="ConcurrencyConflict"/>.
    /// The exception type itself stays internal.
    /// </summary>
    bool IsConcurrencyConflict(Exception exception);

    /// <summary>The 409 <c>POST /spark/po/update</c> answers a concurrency conflict with, in the envelope.</summary>
    /// <remarks>
    /// Pass the caught exception: the body then says whether the row <c>changed</c> or was
    /// <c>deleted</c> since it was loaded (#467, D15), and carries the message naming the rows of a bulk
    /// refusal (D18). Never the exception's own message, which carries change vectors.
    /// </remarks>
    IResult ConcurrencyConflict(Exception? exception = null);

    /// <summary>
    /// Carries a request's <c>deleted: exclude|include|only</c> field to row policies
    /// (<c>RowPolicyContext.Deleted</c>, #460 T2), exactly as <c>/spark/po/load</c> and the query
    /// endpoints do. Call it <b>before anything asks row security in the request</b> — row filters are
    /// memoized per request. Core filters nothing on it; the SoftDelete package honours it only for
    /// holders of <c>ViewDeleted/T</c>, so an add-on passes the flag through and gates nothing itself.
    /// </summary>
    /// <remarks>
    /// Without an entity type the mode applies to every type the request touches. Pass the type the
    /// request is about (<see cref="UseDeletedFilter(SparkDeletedFilter?, EntityTypeDefinition?)"/>)
    /// so a deleted row's live references (a deleted answer's question) still resolve.
    /// </remarks>
    void UseDeletedFilter(SparkDeletedFilter? deleted);

    /// <summary>
    /// As <see cref="UseDeletedFilter(SparkDeletedFilter?)"/>, scoped to <paramref name="entityType"/>:
    /// every other type in the request (a reference's label, a breadcrumb) sees live rows only, as on
    /// <c>/spark/po/load</c>.
    /// </summary>
    void UseDeletedFilter(SparkDeletedFilter? deleted, EntityTypeDefinition? entityType);
}

[Register(typeof(ISparkAddOnEndpoints), ServiceLifetime.Scoped)]
internal sealed partial class SparkAddOnEndpoints : ISparkAddOnEndpoints
{
    [Inject] private readonly IClientAccessor client;
    [Inject] private readonly IModelLoader modelLoader;
    [Inject] private readonly IRowPolicyRequestState rowPolicyRequestState;

    public EntityTypeDefinition? ResolveType(ISparkTypedRequest? request)
        => SparkRequestType.Resolve(modelLoader, request);

    public IResult Envelope(object? result, int statusCode)
        => ClientResult.Envelope(client, result, statusCode);

    public IResult Refusal(HttpContext httpContext)
        => ClientResult.EnvelopeRefusal(client, httpContext);

    public IResult ValidationFailed(SparkValidationException exception)
        => ClientResult.Envelope(client, new { errors = new[] { exception.ToError() } }, StatusCodes.Status400BadRequest);

    public IResult ActionDisabled(SparkActionDisabledException exception)
        => ClientResult.ActionDisabled(client, exception);

    public IResult Throttled(HttpContext httpContext, SparkThrottledException exception)
        => ClientResult.Throttled(client, httpContext, exception);

    public bool IsConcurrencyConflict(Exception exception) => exception is SparkConcurrencyException;

    public IResult ConcurrencyConflict(Exception? exception = null)
        => ClientResult.ConcurrencyConflict(client, exception);

    public void UseDeletedFilter(SparkDeletedFilter? deleted)
        => rowPolicyRequestState.Deleted = deleted ?? SparkDeletedFilter.Exclude;

    public void UseDeletedFilter(SparkDeletedFilter? deleted, EntityTypeDefinition? entityType)
    {
        rowPolicyRequestState.Deleted = deleted ?? SparkDeletedFilter.Exclude;
        rowPolicyRequestState.DeletedScopeClrType = entityType?.ClrType;
    }
}
