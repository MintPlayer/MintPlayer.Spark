using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.ClientOperations;
using MintPlayer.Spark.Abstractions.Requests;
using MintPlayer.Spark.Exceptions;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Endpoints;

/// <summary>
/// The response and request conventions of Spark's own endpoints, for add-on packages that map
/// endpoints of their own under <c>/spark/*</c> (SoftDelete's <c>/spark/po/restore</c>, History's
/// <c>/spark/po/revisions</c>, #460 T1).
/// </summary>
/// <remarks>
/// <para>
/// An add-on endpoint must answer exactly like a core one: the same envelope
/// (<c>{ result, operations }</c>), and above all the same refusal. A refusal is deliberately
/// indistinguishable across "no such type", "no such row", "you may not" and "malformed body" —
/// 401 for an anonymous caller, 404 otherwise (#453) — and an add-on that invented its own would
/// reopen the existence oracle core closed. So the rules live here, once.
/// </para>
/// </remarks>
public static class SparkAddOnEndpoints
{
    /// <summary>
    /// Reads a typed request body and resolves the entity type it names. Both are null when the body
    /// is malformed or names no type the model declares — answer that with <see cref="Refusal"/>,
    /// never with a distinguishable error.
    /// </summary>
    public static Task<(TRequest? Request, EntityTypeDefinition? EntityType)> ReadTypedRequestAsync<TRequest>(
        HttpContext httpContext,
        IModelLoader modelLoader)
        where TRequest : class, ISparkTypedRequest
        => SparkRequestType.ReadAsync<TRequest>(httpContext, modelLoader);

    /// <summary>The standard <c>{ result, operations }</c> envelope with <paramref name="statusCode"/>.</summary>
    public static IResult Envelope(IClientAccessor client, object? result, int statusCode)
        => ClientResult.Envelope(client, result, statusCode);

    /// <summary>The standard refusal: 401 for an anonymous caller, 404 otherwise, in the envelope.</summary>
    public static IResult Refusal(IClientAccessor client, HttpContext httpContext)
        => ClientResult.EnvelopeRefusal(client, httpContext);

    /// <summary>A 400 carrying the validation error, in the envelope (<c>{ errors: [...] }</c>).</summary>
    public static IResult ValidationFailed(IClientAccessor client, SparkValidationException exception)
        => ClientResult.Envelope(client, new { errors = new[] { exception.ToError() } }, StatusCodes.Status400BadRequest);

    /// <summary>
    /// A 403 naming the withheld action (#460, D13). Only for a row the caller could see — the
    /// disabled-action gate runs after the row gate, which is what makes naming it safe.
    /// </summary>
    public static IResult ActionDisabled(IClientAccessor client, SparkActionDisabledException exception)
        => ClientResult.ActionDisabled(client, exception);

    /// <summary>
    /// Whether <paramref name="exception"/> is the optimistic-concurrency refusal of a save (the
    /// posted etag is not the stored change vector). Answer it with <see cref="ConcurrencyConflict"/>.
    /// The exception type itself stays internal.
    /// </summary>
    public static bool IsConcurrencyConflict(Exception exception) => exception is SparkConcurrencyException;

    /// <summary>The 409 <c>POST /spark/po/update</c> answers a concurrency conflict with, in the envelope.</summary>
    public static IResult ConcurrencyConflict(IClientAccessor client)
        => ClientResult.Envelope(client, new { error = "Concurrency conflict" }, StatusCodes.Status409Conflict);

    /// <summary>
    /// Carries a request's <c>deleted: exclude|include|only</c> field to row policies
    /// (<c>RowPolicyContext.Deleted</c>, #460 T2), exactly as <c>/spark/po/load</c> and the query
    /// endpoints do. Call it <b>before anything asks row security in the request</b> — row filters are
    /// memoized per request. Core filters nothing on it; the SoftDelete package honours it only for
    /// holders of <c>ViewDeleted/T</c>, so an add-on passes the flag through and gates nothing itself.
    /// </summary>
    public static void UseDeletedFilter(HttpContext httpContext, SparkDeletedFilter? deleted)
        => httpContext.RequestServices.GetRequiredService<IRowPolicyRequestState>().Deleted = deleted ?? SparkDeletedFilter.Exclude;
}
