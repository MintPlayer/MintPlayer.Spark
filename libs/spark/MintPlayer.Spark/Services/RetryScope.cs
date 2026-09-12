using MintPlayer.Spark.Abstractions.Retry;

namespace MintPlayer.Spark.Services;

/// <summary>
/// The one place an endpoint hands a request's retry answers to the accessor.
/// </summary>
/// <remarks>
/// <para>
/// This exists to stop the wiring being retyped. Before it, every retry-capable endpoint carried its
/// own copy of
/// </para>
/// <code>
/// if (request.RetryResults is { Length: > 0 } retryResults)
/// {
///     var accessor = (RetryAccessor)retryAccessor;
///     accessor.AnsweredResults = retryResults.ToDictionary(r => r.Step);
/// }
/// </code>
/// <para>
/// — four lines and a downcast, in <c>Create</c>, <c>Update</c>, <c>Delete</c> and <c>Refresh</c>.
/// Nothing connected those copies to the matching <c>catch (SparkRetryActionException)</c>, so an
/// endpoint could ship with one half of a retry and look finished. Three did.
/// </para>
/// <para>
/// ⚠️ This covers the <b>accept</b> half only. The <b>emit</b> half is a <c>catch</c> clause inside
/// the endpoint's own <c>try</c>, which cannot be factored out without restructuring five
/// multi-catch methods — a large re-indentation for little gain over the thing that actually holds
/// the two halves together, which is the endpoint matrix in <c>RetryFromEveryHookTests</c>. Add a
/// row there for any new retry-capable endpoint; that test is the guard, not the type system.
/// </para>
/// </remarks>
internal static class RetryScope
{
    /// <summary>
    /// Feeds <paramref name="request"/>'s answered steps to <paramref name="retryAccessor"/>.
    /// Safe to call with a null request or no answers — that is simply a first attempt.
    /// </summary>
    public static void Accept(IRetryAccessor retryAccessor, IRetryableRequest? request)
        => ((RetryAccessor)retryAccessor).Accept(request);
}
