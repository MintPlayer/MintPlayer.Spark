using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions.Visitor;
using Raven.Client;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;
using Raven.Client.Exceptions;

namespace MintPlayer.Spark.Visitor;

/// <summary>
/// Runs every <see cref="ISparkVisitorMergeHandler"/> for one visitor, exactly once (commerce PRD X1, S16).
/// </summary>
/// <remarks>
/// <para>
/// Two paths reach it: Authorization's sign-in hook, and <c>UseSparkVisitor</c>'s merge-on-sight for an
/// authenticated request that still carries a visitor cookie (a hook that failed — failures there never
/// refuse a sign-in — or a sign-in path that bypasses Spark's sign-in manager). They can race.
/// </para>
/// <para>
/// <b>Exactly once:</b> the handlers write through one cluster-wide session that also creates the
/// compare-exchange value <c>visitor-merge/{visitorId}</c> holding the user id. Whichever path commits first
/// wins; the other's commit fails as a whole on the existing key, so its handlers' writes never land. A
/// recorded merge expires (lazily, <c>@expires</c>) after <see cref="SparkVisitorOptions.Lifetime"/>, when no
/// cookie that could ask for it again can still be valid.
/// </para>
/// </remarks>
internal sealed class SparkVisitorMerge(
    IHttpContextAccessor accessor,
    IDocumentStore store,
    IEnumerable<ISparkVisitorMergeHandler> handlers,
    IOptions<SparkVisitorOptions> options,
    IServiceProvider services) : ISparkVisitorMerge
{
    /// <summary>The compare-exchange key recording that <paramref name="visitorId"/> was merged (a 32-hex-digit id).</summary>
    internal static string KeyFor(string visitorId) => "visitor-merge/" + visitorId;

    public async Task<bool> MergeIntoAsync(string userId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(userId);
        if (accessor.HttpContext is not { } context || SparkVisitorFeature.For(context) is not { VisitorId: { } visitorId } visitor)
            return false;

        var ran = await MergeAsync(visitorId, userId, cancellationToken);
        visitor.Forget();
        return ran;
    }

    /// <summary>
    /// Merges <paramref name="visitorId"/> into <paramref name="userId"/> unless that already happened. Throws
    /// when a handler does: nothing is recorded then, so the next attempt runs them again.
    /// </summary>
    /// <returns><see langword="true"/> when this call committed the merge.</returns>
    internal async Task<bool> MergeAsync(string visitorId, string userId, CancellationToken cancellationToken)
    {
        var all = handlers.ToList();
        if (all.Count == 0)
            return false;

        var key = KeyFor(visitorId);
        using var session = store.OpenAsyncSession(new Raven.Client.Documents.Session.SessionOptions { TransactionMode = TransactionMode.ClusterWide });
        if (await session.Advanced.ClusterTransaction.GetCompareExchangeValueAsync<string>(key, cancellationToken) is not null)
            return false;

        var record = session.Advanced.ClusterTransaction.CreateCompareExchangeValue(key, userId);
        var time = services.GetService<TimeProvider>() ?? TimeProvider.System;
        record.Metadata[Constants.Documents.Metadata.Expires] = (time.GetUtcNow() + options.Value.Lifetime).UtcDateTime.ToString("O");

        foreach (var handler in all)
            await handler.MergeAsync(visitorId, userId, session, cancellationToken);

        try
        {
            await session.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (ConcurrencyException)
        {
            // The other path committed first (ClusterTransactionConcurrencyException derives from this): its
            // handlers' writes stand, and none of this session's did.
            return false;
        }
    }
}
