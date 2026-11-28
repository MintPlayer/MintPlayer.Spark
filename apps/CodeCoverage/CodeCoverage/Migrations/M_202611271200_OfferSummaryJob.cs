using CodeCoverage.Dependencies.Lifecycle;
using CodeCoverage.Entities;
using CodeCoverage.MergeQueue;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Messaging.Abstractions;
using MintPlayer.Spark.Migrations;
using Raven.Client.Documents;

namespace CodeCoverage.Migrations;

/// <summary>
/// #714 Dec11: queues the summary-job offer once for every connected, unarchived repository whose merge queue is on, so the
/// managers of a quiet repository hear that their rulesets can switch to one summary job, not only those of a repository
/// whose queue reads its rules at the next enable.
/// </summary>
/// <remarks>
/// <para>
/// "Whose merge queue is on" is <see cref="DependencyUpdateRun.FileMergeQueue"/> = <c>enabled</c>: what the default branch's
/// <c>coverage.yml</c> said the last time the bot read it, which the queue itself records on every read. A repository whose
/// file has no section, or turns the queue off, has no queue for the rulesets to hold.
/// </para>
/// <para>
/// Streams by id prefix rather than querying <c>Repositories_Overview</c>, and loads the run documents by id: a migration must
/// not depend on an index that may not be deployed yet. Modelled on <see cref="M_202610151200_BackfillRepositoryKnownChecks"/>.
/// The forge reads happen in <see cref="OfferSummaryJobRecipient"/>, never here, so a slow forge cannot hold the startup.
/// </para>
/// </remarks>
public partial class M_202611271200_OfferSummaryJob : ISparkMigration
{
    public static long Version => 202611271200;
    public static string? Description => "Queue the one-off summary-job offer for every repository whose merge queue is on (#714 Dec11)";

    /// <summary>Run documents loaded per session request.</summary>
    private const int Batch = 256;

    [Inject] private readonly IDocumentStore store;
    [Inject] private readonly IMessageBus messageBus;
    [Inject] private readonly ILogger<M_202611271200_OfferSummaryJob> logger;

    public async Task UpAsync(CancellationToken cancellationToken)
    {
        var repositoryIds = new List<string>();
        using (var session = store.OpenAsyncSession())
        {
            await using var stream = await session.Advanced.StreamAsync<Repository>(
                startsWith: "Repositories/", token: cancellationToken);
            while (await stream.MoveNextAsync())
            {
                if (!string.Equals(stream.Current.Metadata.GetString("@collection"), "Repositories", StringComparison.Ordinal)) continue;
                var repository = stream.Current.Document;
                if (repository.Connection == RepositoryConnection.Disconnected || repository.Archived) continue;
                repositoryIds.Add(stream.Current.Id);
            }
        }

        var queued = 0;
        foreach (var chunk in repositoryIds.Chunk(Batch))
        {
            Dictionary<string, DependencyUpdateRun?> runs;
            // A session per batch, so the session's request cap never bounds how many repositories there may be.
            using (var session = store.OpenAsyncSession())
                runs = (await session.LoadAsync<DependencyUpdateRun>(chunk.Select(DependencyUpdateRun.ForRepository), cancellationToken))
                    .ToDictionary(pair => pair.Key, pair => (DependencyUpdateRun?)pair.Value, StringComparer.OrdinalIgnoreCase);

            foreach (var id in chunk)
            {
                if (runs.GetValueOrDefault(DependencyUpdateRun.ForRepository(id))?.FileMergeQueue != DependencyFileMergeQueue.Enabled) continue;
                await messageBus.BroadcastOnceAsync(
                    new OfferSummaryJobMessage { RepositoryId = id },
                    OfferSummaryJobMessage.OnceKey(id),
                    cancellationToken);
                queued++;
            }
        }

        logger.LogInformation("Queued the summary-job offer for {Count} repositories whose merge queue is on", queued);
    }
}
