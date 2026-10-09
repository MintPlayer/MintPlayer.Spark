using CodeCoverage.Dependencies;
using CodeCoverage.Entities;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Messaging.Abstractions;
using MintPlayer.Spark.Migrations;
using Raven.Client.Documents;

namespace CodeCoverage.Migrations;

/// <summary>
/// Queues a manifest scan for every connected, unarchived repository once, so the Account page's
/// dependency graph (dependency-updates PRD §6) is filled right after the deploy that introduces it,
/// instead of after the next nightly reconcile or a manifest-touching push.
/// </summary>
/// <remarks>
/// Streams by id prefix rather than querying <c>Repositories_Overview</c>: a migration must not depend
/// on an index that may not be deployed yet. Uses the same per-day deduplication key as the nightly
/// sweep, so a reconcile on the same day does not queue a second scan.
/// </remarks>
public partial class M_202610091400_BackfillRepositoryManifestScans : ISparkMigration
{
    public static long Version => 202610091400;
    public static string? Description => "Queue a first dependency-manifest scan for every connected repository";

    [Inject] private readonly IDocumentStore store;
    [Inject] private readonly IMessageBus messageBus;
    [Inject] private readonly ILogger<M_202610091400_BackfillRepositoryManifestScans> logger;

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
                new ScanRepositoryManifestsMessage { RepositoryId = id },
                ManifestScanTriggers.DailyKey(id, now),
                cancellationToken);
        }

        logger.LogInformation("Queued a first manifest scan for {Count} repositories", repositoryIds.Count);
    }
}
