using Microsoft.Extensions.Options;
using MintPlayer.Spark.Messaging.Indexes;
using MintPlayer.Spark.Messaging.Models;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Messaging.Services;

/// <summary>
/// Returns messages stranded in <see cref="EMessageStatus.Processing"/> to the queue.
/// </summary>
/// <remarks>
/// <para>
/// A message is marked <c>Processing</c> before its handlers run. If the process dies in between —
/// a deploy, a crash, an OOM — nothing ever moves it: it is not <c>Pending</c> or <c>Failed</c>, so
/// no drain selects it, and its partition stays blocked behind a message that will never finish.
/// This was already true before the ordering work; it simply had no owner.
/// </para>
/// <para>
/// The lease is generous by default because a handler may legitimately run for minutes — the
/// ingestion recipient parses whole coverage reports. Reaping too eagerly is worse than reaping
/// late: it double-processes work that is still running.
/// </para>
/// <para>
/// The attempt counter is incremented on the way back, so a message that reliably kills its host
/// cannot loop forever — it climbs its ladder and eventually dead-letters like any other failure.
/// </para>
/// </remarks>
internal sealed class MessageReaper(
    IDocumentStore store,
    IOptions<SparkMessagingOptions> options,
    TimeProvider timeProvider,
    ILogger<MessageReaper> logger) : BackgroundService
{
    private readonly SparkMessagingOptions options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Once at startup, so anything stranded by the deploy that just happened is released before
        // the lanes settle, then periodically for crashes that happen while running.
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ReapAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Reaper pass failed");
            }

            try
            {
                await Task.Delay(options.ReaperInterval, timeProvider, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    internal async Task<int> ReapAsync(CancellationToken cancellationToken)
    {
        var cutoff = timeProvider.GetUtcNow().UtcDateTime - options.ProcessingLease;

        using var session = store.OpenAsyncSession();
        var stranded = await session.Query<SparkMessage, SparkMessages_ByQueue>()
            .Customize(x => x.WaitForNonStaleResults(TimeSpan.FromSeconds(15)))
            .Where(m => m.Status == EMessageStatus.Processing && m.CreatedAtUtc < cutoff)
            .Take(256)
            .ToListAsync(cancellationToken);

        // CreatedAtUtc is a coarse filter — it narrows the scan to messages old enough to be
        // suspicious. Whether one is genuinely stranded is decided below, from when it was last
        // touched, which is what the lease is really about.
        var reaped = 0;
        foreach (var message in stranded)
        {
            var lastTouched = session.Advanced.GetLastModifiedFor(message) ?? message.CreatedAtUtc;
            if (lastTouched > cutoff)
                continue;

            message.Status = EMessageStatus.Failed;
            message.NextAttemptAtUtc = null;
            message.AttemptCount++;
            reaped++;

            logger.LogWarning(
                "Reaped {MessageId} on lane {Lane}: left Processing since {LastTouched}, past the {Lease} lease",
                message.Id, message.QueueName, lastTouched, options.ProcessingLease);
        }

        if (reaped > 0)
            await session.SaveChangesAsync(cancellationToken);

        return reaped;
    }
}
