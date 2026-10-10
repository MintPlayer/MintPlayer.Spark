using Raven.Client.Documents;

namespace MintPlayer.Spark.Services;

/// <summary>
/// Waits, bounded, until every index over a set of collections has processed the writes committed before
/// the call. Shared by the request write path (<see cref="DatabaseAccess"/>: the caller's next query sees
/// its own write) and the live engine (X4: a pushed page re-runs only once the index caught up with the
/// change that triggered it, so it is not stale).
/// </summary>
/// <remarks>
/// <para>
/// One <c>GetIndexesStatisticsOperation</c> names the indexes (static and auto) whose source collections
/// include a written one, with their staleness; in the common case none is stale and that one request is the
/// whole cost. Each stale one gets <c>limit 0</c> queries with <c>WaitForNonStaleResults</c>, whose
/// server-side cutoff is the collections' last etag when the query starts: it waits for every write up to
/// that point and not for writes that land afterwards. The index map is fetched per call, not cached: an auto
/// index created by a previous query would be missing from a cached map.
/// </para>
/// <para>
/// ⚠️ Waiting queries are sliced, not one long query: RavenDB does not dispose an index while a query waits on
/// it, so a single long query held an index delete for the whole bound and failed it (measured: the delete's
/// own 15 s raft wait timed out). Each slice starts a fresh cutoff, so a collection written faster than its
/// index keeps up can run into the bound — which is then the truth about that index. An index disposed between
/// or under slices (an auto-index merge, a side-by-side swap, a reset or a delete) fails its query; that is
/// reported, the statistics are read again, and whatever replaced it is waited on with the remaining budget. A
/// PAUSED index never catches up, so it costs the full bound. Disabled and errored indexes, and a side-by-side
/// replacement (the original keeps answering queries), are not waited on.
/// </para>
/// <para>Never throws: a timeout or a failure is reported through <c>unfinished</c> and swallowed.</para>
/// </remarks>
internal static class SparkIndexWait
{
    /// <summary>The longest single waiting query inside the bound.</summary>
    internal static readonly TimeSpan Slice = TimeSpan.FromMilliseconds(500);

    public static async Task WaitAsync(
        IDocumentStore documentStore,
        string database,
        IReadOnlySet<string> collections,
        TimeSpan bound,
        Action<Exception, string, TimeSpan>? unfinished,
        CancellationToken cancellationToken)
    {
        if (collections.Count == 0)
            return;

        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(bound);
        string? waitingOn = null;
        try
        {
            using var waitSession = documentStore.OpenAsyncSession(new Raven.Client.Documents.Session.SessionOptions { Database = database, NoTracking = true, NoCaching = true });
            waitSession.Advanced.MaxNumberOfRequestsPerSession = int.MaxValue;
            var waited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool reread;
            do
            {
                reread = false;
                waitingOn = null;
                var indexes = await documentStore.Maintenance.ForDatabase(database)
                    .SendAsync(new Raven.Client.Documents.Operations.Indexes.GetIndexesStatisticsOperation(), limit.Token);
                var stale = indexes
                    .Where(index => index.IsStale
                        && !waited.Contains(index.Name)
                        && index.State is not Raven.Client.Documents.Indexes.IndexState.Disabled
                            and not Raven.Client.Documents.Indexes.IndexState.Error
                        && !index.Name.StartsWith(Raven.Client.Constants.Documents.Indexing.SideBySideIndexNamePrefix, StringComparison.OrdinalIgnoreCase)
                        && index.Collections?.Keys.Any(collections.Contains) == true)
                    .Select(index => index.Name)
                    .ToList();

                foreach (var name in stale)
                {
                    waitingOn = name;
                    waited.Add(name);
                    try
                    {
                        while (true)
                        {
                            var remaining = bound - elapsed.Elapsed;
                            if (remaining <= TimeSpan.Zero)
                                throw new TimeoutException($"The {bound.TotalSeconds:0.#} s bound ran out.");
                            try
                            {
                                await waitSession.Advanced.AsyncDocumentQuery<object>(name)
                                    .WaitForNonStaleResults(remaining < Slice ? remaining : Slice)
                                    .CountAsync(limit.Token);
                                break;
                            }
                            catch (TimeoutException) when (elapsed.Elapsed < bound)
                            {
                                // Still behind after one slice: ask again, until the bound.
                            }
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException && ex is not TimeoutException)
                    {
                        // This index went away under the wait (deleted, reset, or merged into a new auto
                        // index — seen under load in QnA). What replaced it covers the same collections and
                        // is waited on with the remaining budget, after a fresh read of the statistics.
                        unfinished?.Invoke(ex, name, elapsed.Elapsed);
                        reread = true;
                        break;
                    }
                }
            }
            while (reread);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller went away: nobody is left to read the index.
        }
        catch (Exception ex)
        {
            // The bound ran out, or the index statistics could not be read.
            unfinished?.Invoke(ex, waitingOn ?? "(index statistics)", elapsed.Elapsed);
        }
    }
}
