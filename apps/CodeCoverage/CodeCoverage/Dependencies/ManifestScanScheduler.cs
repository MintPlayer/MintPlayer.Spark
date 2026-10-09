using CodeCoverage.Entities;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Messaging.Abstractions;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;

namespace CodeCoverage.Dependencies;

public interface IManifestScanScheduler
{
    /// <summary>
    /// Queues a manifest scan for every connected, unarchived repository of <paramref name="account"/>,
    /// at most once per repository per UTC day. Never throws: a failure here must not undo the
    /// reconcile that called it.
    /// </summary>
    /// <param name="waitForIndex">
    /// True right after the caller saved repositories it may have just created (an installation
    /// event), so the index query sees them.
    /// </param>
    Task ScheduleAccountAsync(Account account, bool waitForIndex, CancellationToken cancellationToken = default);
}

/// <summary>
/// The scheduled half of the manifest-scan triggers (dependency-updates PRD §6.3): after every
/// account reconcile — the nightly sweep, and the one an installation change queues — each of the
/// account's repositories is scanned. A repository newly added to an installation is therefore
/// scanned as soon as the reconcile has given it a default branch, and every repository is re-checked
/// nightly in case a push was missed. The scan itself stops at the tree listing when nothing changed.
/// </summary>
[Register(typeof(IManifestScanScheduler), ServiceLifetime.Scoped)]
public partial class ManifestScanScheduler : IManifestScanScheduler
{
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly IMessageBus messageBus;
    [Inject] private readonly ILogger<ManifestScanScheduler> logger;

    public async Task ScheduleAccountAsync(Account account, bool waitForIndex, CancellationToken cancellationToken = default)
    {
        try
        {
            var ownerKey = account.OwnerKey;
            var query = session.Query<Repository, Indexes.Repositories_Overview>();
            if (waitForIndex)
                query = query.Customize(c => c.WaitForNonStaleResults(TimeSpan.FromSeconds(15)));

            var repositories = await query
                .Where(r => r.OwnerKey == ownerKey)
                .Take(1024)
                .ToListAsync(cancellationToken);

            var now = DateTimeOffset.UtcNow;
            var queued = 0;
            foreach (var repository in repositories)
            {
                if (repository.Connection == RepositoryConnection.Disconnected || repository.Archived) continue;

                await messageBus.BroadcastOnceAsync(
                    new ScanRepositoryManifestsMessage { RepositoryId = repository.Id! },
                    ManifestScanTriggers.DailyKey(repository.Id!, now),
                    cancellationToken);
                queued++;
            }

            logger.LogDebug("Queued manifest scans for {Count} repositories of {Login}", queued, account.Login);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The nightly sweep tries again tomorrow; the reconcile that called this has already saved.
            logger.LogWarning(ex, "Could not queue manifest scans for {Login}", account.Login);
        }
    }
}
