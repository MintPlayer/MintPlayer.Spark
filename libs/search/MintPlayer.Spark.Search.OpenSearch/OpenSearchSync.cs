using Microsoft.Extensions.Logging;
using MintPlayer.Spark.Abstractions.Search;
using Raven.Client.Documents;
using Raven.Client.Documents.Queries;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Search.OpenSearch;

/// <summary>
/// Copies <c>Spark_Search</c> to the engine (<c>docs/spark_search_PRD.md</c> D8): every interval, the rows modified since
/// the checkpoint, in (<c>LastModified</c>, id) order; a soft-deleted row (<c>IsDeleted</c>) is removed from the engine.
/// Hard deletes leave the index without a trace, so <see cref="OpenSearchDeleteInterceptor"/> removes those. A new,
/// wiped or recreated engine index has no checkpoint, so it is filled from the start.
/// </summary>
/// <remarks>
/// The checkpoint only moves on a non-stale answer. RavenDB indexes in etag order, so while the index is stale a row
/// modified earlier may still be on its way, and moving past it would lose it.
/// </remarks>
internal sealed class OpenSearchSync(
    OpenSearchEngine engine,
    IDocumentStore store,
    SparkOpenSearchOptions options,
    ILogger<OpenSearchSync> logger) : BackgroundService
{
    private string? lastFailure;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.SyncInterval);
        do
        {
            try
            {
                await SyncOnceAsync(stoppingToken);
                if (lastFailure is not null)
                    logger.LogInformation("The search engine is in sync again.");
                lastFailure = null;
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                // Logged once per distinct failure, not once per outage: an engine still starting refuses connections,
                // and a bulk it then rejects must not hide behind that first warning (production, 2026-11-05).
                if (ex.Message != lastFailure)
                    logger.LogWarning(ex, "Syncing the search engine failed; retrying every {Interval}.", options.SyncInterval);
                lastFailure = ex.Message;
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>Copies every pending row; returns how many were copied.</summary>
    internal async Task<int> SyncOnceAsync(CancellationToken cancellationToken)
    {
        await engine.EnsureIndexAsync(cancellationToken);
        var checkpoint = await engine.ReadCheckpointAsync(cancellationToken) ?? new SyncCheckpoint(DateTime.MinValue, 0);
        var copied = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            var (rows, isStale) = await ReadRowsAsync(store, checkpoint, options.SyncBatchSize, cancellationToken);

            if (rows.Count == 0)
                break;

            await engine.BulkAsync(
                rows.Where(r => !r.IsDeleted).ToList(),
                rows.Where(r => r.IsDeleted).Select(r => r.Id).ToList(),
                cancellationToken);
            copied += rows.Count;

            if (isStale)
                break; // Copied, but the checkpoint stays: the next run reads these again, which is harmless.

            var newest = rows[^1].LastModified;
            var atNewest = rows.Count(r => r.LastModified == newest);
            checkpoint = newest == checkpoint.Since
                ? checkpoint with { Skip = checkpoint.Skip + rows.Count }
                : new SyncCheckpoint(newest, atNewest);
            await engine.WriteCheckpointAsync(checkpoint, cancellationToken);

            if (rows.Count < options.SyncBatchSize)
                break;
        }
        return copied;
    }

    /// <summary>One batch of <c>Spark_Search</c> rows from the checkpoint on, in (<c>LastModified</c>, id) order.</summary>
    internal static async Task<(List<SparkSearchEntry> Rows, bool IsStale)> ReadRowsAsync(
        IDocumentStore store, SyncCheckpoint checkpoint, int take, CancellationToken cancellationToken)
    {
        using var session = store.OpenAsyncSession();
        var rows = await session.Advanced.AsyncDocumentQuery<SparkSearchEntry>(SparkSearchIndex.Name)
            .Statistics(out QueryStatistics stats)
            .WhereGreaterThanOrEqual(SparkSearchIndex.LastModified, checkpoint.Since)
            .OrderBy(SparkSearchIndex.LastModified)
            .OrderBy(Raven.Client.Constants.Documents.Indexing.Fields.DocumentIdFieldName)
            .Skip(checkpoint.Skip)
            .Take(take)
            .SelectFields<SyncRow>(
                ProjectionBehavior.FromIndexOrThrow,
                SparkSearchIndex.Type, SparkSearchIndex.Text, SparkSearchIndex.IsDeleted, SparkSearchIndex.LastModified)
            .ToListAsync(cancellationToken);
        var entries = rows.Select(r => new SparkSearchEntry
        {
            Id = r.Id ?? throw new InvalidOperationException($"{SparkSearchIndex.Name} answered a row without a document id."),
            Type = r.Type ?? "",
            Text = r.Text,
            IsDeleted = r.IsDeleted,
            LastModified = r.LastModified,
        }).ToList();
        return (entries, stats.IsStale);
    }

    /// <summary>
    /// The projection's shape. Its <c>Id</c> must start out null: RavenDB fills a projection's id only into an empty
    /// identity property, and <see cref="SparkSearchEntry.Id"/> starts out <c>""</c>, so it stayed blank and the engine
    /// refused every bulk ("id is missing", production 2026-11-05).
    /// </summary>
    private sealed class SyncRow
    {
        public string? Id { get; set; }
        public string? Type { get; set; }
        public string? Text { get; set; }
        public bool IsDeleted { get; set; }
        public DateTime LastModified { get; set; }
    }
}
