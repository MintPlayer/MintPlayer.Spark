using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Abstractions.Visitor;

/// <summary>
/// Moves what a guest built up onto the account they signed in to: the basket, the waiting-room entry. Multi-
/// registered; every handler runs once per visitor, in one cluster-wide transaction (<paramref name="session"/>
/// of <see cref="MergeAsync"/>) that also records the merge, so the merge happens exactly once even when the
/// sign-in hook and the next request race for it.
/// </summary>
/// <remarks>
/// <para>
/// Called from the sign-in hook (Authorization's <c>ISparkSignedInHandler</c>) and, as a backstop, from the
/// first authenticated request that still carries a visitor cookie. Write through <paramref name="session"/>
/// only and do not call <c>SaveChangesAsync</c>: the framework commits the handlers' writes and the merge
/// record together, or none of them.
/// </para>
/// <para>
/// A cluster-wide session takes documents and compare-exchange values
/// (<c>session.Advanced.ClusterTransaction</c>); it refuses patches, counters, attachments and time series.
/// </para>
/// </remarks>
public interface ISparkVisitorMergeHandler
{
    /// <summary>Moves the state kept for <paramref name="visitorId"/> onto <paramref name="userId"/>.</summary>
    Task MergeAsync(string visitorId, string userId, IAsyncDocumentSession session, CancellationToken cancellationToken);
}
