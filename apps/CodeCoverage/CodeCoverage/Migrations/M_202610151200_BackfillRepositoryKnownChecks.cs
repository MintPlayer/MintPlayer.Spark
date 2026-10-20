using CodeCoverage.Entities;
using CodeCoverage.MergeQueue;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Messaging.Abstractions;
using MintPlayer.Spark.Migrations;
using Raven.Client.Documents;

namespace CodeCoverage.Migrations;

/// <summary>
/// Queues a known-checks refresh for every connected, unarchived repository once, so the merge queue's exclusion lists
/// have names to pick from (merge-queue PRD §11, D109) right after the deploy that introduces the picker, instead of after
/// the next nightly reconcile or the next check that reports.
/// </summary>
/// <remarks>
/// Streams by id prefix rather than querying <c>Repositories_Overview</c>: a migration must not depend on an index that
/// may not be deployed yet. Uses the same per-day deduplication key as the nightly sweep, so a reconcile on the same day
/// does not queue a second refresh. Modelled on <see cref="M_202610091400_BackfillRepositoryManifestScans"/>.
/// </remarks>
public partial class M_202610151200_BackfillRepositoryKnownChecks : ISparkMigration
{
    public static string? Description => "Queue a first read of the checks that reported on every connected repository";

    [Inject] private readonly IDocumentStore store;
    [Inject] private readonly IMessageBus messageBus;
    [Inject] private readonly ILogger<M_202610151200_BackfillRepositoryKnownChecks> logger;

    public async Task UpAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
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

        foreach (var id in repositoryIds)
        {
            await messageBus.BroadcastOnceAsync(
                new RefreshKnownChecksMessage { RepositoryId = id },
                RefreshKnownChecksMessage.DailyKey(id, now),
                cancellationToken);
        }

        logger.LogInformation("Queued a first known-checks refresh for {Count} repositories", repositoryIds.Count);
    }
}
