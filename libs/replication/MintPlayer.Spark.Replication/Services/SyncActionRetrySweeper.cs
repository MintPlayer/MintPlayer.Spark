using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Replication.Abstractions.Configuration;
using MintPlayer.Spark.Replication.Abstractions.Models;
using MintPlayer.Spark.Replication.Indexes;
using MintPlayer.Spark.Replication.Models;
using Raven.Client.Documents;
using Raven.Client.Documents.Commands.Batches;
using Raven.Client.Documents.Operations;

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
        // `!= true` rather than `== false`, and the difference is not cosmetic: actions parked by a
        // pre-#258 build have no WakeUp property in their JSON at all, and a missing field does not
        // match `== false`. With that spelling the upgrade would fix only future failures and leave
        // the entire existing backlog stranded permanently. Pinned by
        // SyncActionRetrySweeperTests.Wakes_an_action_parked_before_WakeUp_existed.
        var dueIds = await session.Query<SparkSyncAction, SparkSyncActions_ByStatus>()
            .Where(a => a.Status == ESyncActionStatus.Pending
                        && a.NextAttemptAtUtc != null
                        && a.NextAttemptAtUtc <= now
                        && a.WakeUp != true)
            .Select(a => a.Id)
            .Take(MaxActionsPerSweep)
            .ToListAsync(cancellationToken);

        if (dueIds.Count == 0)
            return 0;

        // Server-side patches, never load-modify-save: with last-write-wins a full document save
        // could resurrect an action the worker completed after our query ran. The patch re-checks
        // the query's predicate on the document as it is now, because the ids came from an index
        // that may be stale: an unconditional patch rewrote an action that had meanwhile completed
        // (or been woken already) on every sweep until the index caught up, each write re-dirtying
        // that index. MessageRetrySweeper had the same flaw, measured in S-M3 under load
        // (contributions_PRD §5c). A document that no longer matches is left untouched.
        var patch = new PatchRequest
        {
            Script = """
                if (this.Status === 'Pending'
                    && this.NextAttemptAtUtc != null && this.NextAttemptAtUtc <= args.now
                    && this.WakeUp !== true) {
                    this.WakeUp = true;
                    this.LastWakeUpUtc = args.now;
                }
                """,
            // Compared as strings: chronological, because RavenDB writes a UTC DateTime in this same fixed-width form.
            Values = { ["now"] = now.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", System.Globalization.CultureInfo.InvariantCulture) },
        };
        var commands = dueIds.Select(id => (ICommandData)new PatchCommandData(id!, changeVector: null, patch)).ToList();

        var executor = documentStore.GetRequestExecutor();
        using (executor.ContextPool.AllocateOperationContext(out var context))
        {
            var command = new SingleNodeBatchCommand(documentStore.Conventions, commands);
            await executor.ExecuteAsync(command, context, sessionInfo: null, cancellationToken);

            var woken = 0;
            foreach (var item in command.Result!.Results)
            {
                if (item is Sparrow.Json.BlittableJsonReaderObject result
                    && result.TryGet(nameof(PatchStatus), out string? status)
                    && status == nameof(PatchStatus.Patched))
                    woken++;
            }
            return woken;
        }
    }
}
