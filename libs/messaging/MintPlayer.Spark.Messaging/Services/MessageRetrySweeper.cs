using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Messaging.Indexes;
using MintPlayer.Spark.Messaging.Models;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Messaging.Services;

/// <summary>
/// The wake-up for parked messages (issue #233). RavenDB subscriptions are
/// change-vector-driven: a document is re-evaluated against the subscription query only
/// when it is written — time passing re-evaluates nothing, and a where-clause cannot
/// evaluate time either (<c>NextAttemptAtUtc &lt;= now()</c> silently never matches). A
/// message parked at <c>Failed</c> (retry backoff) or <c>Pending</c> with a future
/// <c>NextAttemptAtUtc</c> (delayed broadcast) is therefore invisible to
/// <see cref="MessageSubscriptionWorker"/> until this service acts. Every
/// <see cref="SparkMessagingOptions.FallbackPollInterval"/> it patches
/// <see cref="SparkMessage.WakeUp"/> to true on due messages — materializing "the backoff
/// has elapsed" as plain field state the subscription query can match, while the patch
/// itself bumps the change vector that triggers re-evaluation. Redelivery granularity is
/// therefore <see cref="SparkMessagingOptions.FallbackPollInterval"/> (default 30s).
/// </summary>
internal sealed partial class MessageRetrySweeper : BackgroundService
{
    // Bounds a single sweep; a larger backlog drains over subsequent sweeps.
    private const int MaxMessagesPerSweep = 512;

    [Inject] private readonly IDocumentStore documentStore;
    [Inject] private readonly IOptions<SparkMessagingOptions> options;
    [Inject] private readonly ILogger<MessageRetrySweeper> logger;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Delay first: at startup the subscriptions themselves deliver everything
                // already due, and the SparkMessages_ByQueue index may still be deploying.
                await Task.Delay(options.Value.FallbackPollInterval, stoppingToken);

                var touched = await SweepOnceAsync(stoppingToken);
                if (touched > 0)
                    logger.LogInformation("Woke up {Count} due message(s) for redelivery", touched);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Message retry sweep failed; retrying next interval");
            }
        }
    }

    /// <summary>Touches every due parked message once. Internal for tests.</summary>
    internal async Task<int> SweepOnceAsync(CancellationToken cancellationToken)
    {
        using var session = documentStore.OpenAsyncSession();
        var now = DateTime.UtcNow;

        // Pending-with-null-NextAttempt is excluded on purpose: those are new messages the
        // subscription already receives on the write that created them.
        var dueIds = await session.Query<SparkMessage, SparkMessages_ByQueue>()
            .Where(m => (m.Status == EMessageStatus.Pending || m.Status == EMessageStatus.Failed)
                        && m.NextAttemptAtUtc != null
                        && m.NextAttemptAtUtc <= now)
            .Select(m => m.Id)
            .Take(MaxMessagesPerSweep)
            .ToListAsync(cancellationToken);

        if (dueIds.Count == 0)
            return 0;

        // Field-level server-side patches, never load-modify-save: with last-write-wins a
        // full-document save could resurrect a message the worker completed after our query
        // ran. A patch touches only the gate + an informational timestamp; if the message
        // reached a terminal state meanwhile, the triggered re-evaluation simply doesn't
        // match the query, and the worker clears WakeUp again on the next pickup anyway.
        foreach (var id in dueIds)
        {
            session.Advanced.Patch<SparkMessage, bool>(id!, m => m.WakeUp, true);
            session.Advanced.Patch<SparkMessage, DateTime?>(id!, m => m.LastWakeUpUtc, now);
        }

        await session.SaveChangesAsync(cancellationToken);
        return dueIds.Count;
    }
}
