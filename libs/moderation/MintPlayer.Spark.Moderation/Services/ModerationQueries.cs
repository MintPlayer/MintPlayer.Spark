using MintPlayer.Spark.Abstractions.Storage;

namespace MintPlayer.Spark.Moderation.Services;

internal static class ModerationQueries
{
    /// <summary>
    /// Every row of the view <typeparamref name="TView"/> that <paramref name="shape"/> selects, streamed, after the view
    /// caught up. RavenDB refuses <c>WaitForNonStaleResults</c> on a stream (measured: <c>NotSupportedException</c>,
    /// "Stream() does not wait for indexing"), so a zero-row query waits first and the stream follows.
    /// </summary>
    public static async Task<List<TRow>> StreamAllAsync<TRow, TView>(ISparkSession session, Func<IQueryable<TRow>, IQueryable<TRow>> shape, CancellationToken cancellationToken)
        where TRow : class where TView : class, new()
    {
        await session.ToListAsync(shape(session.Query<TRow, TView>(ReputationLedger.IndexWait)).Take(0), cancellationToken);
        var rows = new List<TRow>();
        await foreach (var row in session.StreamAsync(shape(session.Query<TRow, TView>()), cancellationToken))
            rows.Add(row.Document);
        return rows;
    }
}
