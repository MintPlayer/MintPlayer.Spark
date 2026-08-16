using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Replication.Abstractions.Configuration;
using MintPlayer.Spark.Replication.Abstractions.Models;
using MintPlayer.Spark.Replication.Indexes;
using MintPlayer.Spark.Replication.Models;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Replication.Services;

/// <summary>
/// The wake-up for sync actions parked on a retry backoff.
/// <para>
/// <c>SyncActionSubscriptionWorker</c>'s subscription is change-vector-driven: a document is tested
/// against the query only when it is written. Time passing writes nothing, so an action parked with a
/// future <c>NextAttemptAtUtc</c> would never be looked at again — and the query cannot ask the
/// question itself, because <c>now()</c> is not evaluable in a subscription expression (RavenDB 7.2.1
/// silently answered false; 7.2.5 rejects the query outright).
/// </para>
/// <para>
/// So this service evaluates the clock instead, and records the answer as
/// <see cref="SparkSyncAction.WakeUp"/> — plain field state the subscription can match. The patch
/// that sets it is also the write that makes RavenDB re-evaluate the document. Redelivery
/// granularity is therefore <see cref="SparkReplicationOptions.FallbackPollInterval"/>.
/// </para>
/// <para>
/// Deliberately the same shape as <c>MessageRetrySweeper</c>, which solved this for messaging in
/// #233. Two copies of this pattern is one more than ideal, but the two live in independent packages
/// with different documents and neither depends on the other.
/// </para>
/// </summary>
internal sealed partial class SyncActionRetrySweeper : BackgroundService
{
    // Bounds a single sweep; a larger backlog drains over subsequent sweeps.
    private const int MaxActionsPerSweep = 512;

    [Inject] private readonly IDocumentStore documentStore;
    [Inject] private readonly IOptions<SparkReplicationOptions> options;
    [Inject] private readonly ILogger<SyncActionRetrySweeper> logger;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Delay first: at startup the subscription already delivers everything currently
                // due, and SparkSyncActions_ByStatus may still be deploying.
                await Task.Delay(options.Value.FallbackPollInterval, stoppingToken);

                var woken = await SweepOnceAsync(stoppingToken);
                if (woken > 0)
                    logger.LogInformation("Woke up {Count} due sync action(s) for redelivery", woken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Sync action retry sweep failed; retrying next interval");
            }
        }
    }

    /// <summary>Wakes every due parked sync action once. Returns how many were woken. Internal for tests.</summary>
    internal async Task<int> SweepOnceAsync(CancellationToken cancellationToken)
    {
        using var session = documentStore.OpenAsyncSession();
        var now = DateTime.UtcNow;

        // Pending-with-null-NextAttemptAtUtc is excluded on purpose: those are new actions the
        // subscription already receives on the write that created them. Only Pending is swept —
        // Failed is terminal for replication (retries exhausted, or a 400/404 rejection), and
        // reviving it here would silently change the retry contract.
        var dueIds = await session.Query<SparkSyncAction, SparkSyncActions_ByStatus>()
            .Where(a => a.Status == ESyncActionStatus.Pending
                        && a.NextAttemptAtUtc != null
                        && a.NextAttemptAtUtc <= now
                        && a.WakeUp == false)
            .Select(a => a.Id)
            .Take(MaxActionsPerSweep)
            .ToListAsync(cancellationToken);

        if (dueIds.Count == 0)
            return 0;

        // Field-level server-side patches, never load-modify-save: with last-write-wins a full
        // document save could resurrect an action the worker completed after our query ran. A patch
        // touches only the gate and an informational timestamp; if the action reached a terminal
        // state meanwhile, the re-evaluation this triggers simply doesn't match the query.
        foreach (var id in dueIds)
        {
            session.Advanced.Patch<SparkSyncAction, bool>(id!, a => a.WakeUp, true);
            session.Advanced.Patch<SparkSyncAction, DateTime?>(id!, a => a.LastWakeUpUtc, now);
        }

        await session.SaveChangesAsync(cancellationToken);
        return dueIds.Count;
    }
}
