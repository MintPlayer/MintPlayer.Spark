using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Moderation.Services;

internal static class ModerationQueries
{
    /// <summary>
    /// Every result of <paramref name="query"/>, streamed, after the index caught up. RavenDB refuses
    /// <c>WaitForNonStaleResults</c> on a stream (measured: <c>NotSupportedException</c>, "Stream()
    /// does not wait for indexing"), so a zero-row query waits first and the stream follows.
    /// </summary>
    public static async Task<List<(string Id, T Document)>> StreamAllAsync<T>(IAsyncDocumentSession session, Func<IRavenQueryable<T>> query, CancellationToken cancellationToken)
    {
        await query().Customize(c => c.WaitForNonStaleResults(TimeSpan.FromSeconds(30))).Take(0).ToListAsync(cancellationToken);
        var results = new List<(string, T)>();
        await using var stream = await session.Advanced.StreamAsync(query(), cancellationToken);
        while (await stream.MoveNextAsync())
            results.Add((stream.Current.Id, stream.Current.Document));
        return results;
    }
}
