using CodeCoverage.Entities;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Cron;
using MintPlayer.Spark.Messaging.Abstractions;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;

namespace CodeCoverage.Feedback;

/// <summary>
/// Re-enqueues check-run publishes whose last attempt failed transiently.
/// Filters on the queryable mirrors (<see cref="Build.FeedbackState"/>) —
/// the outbox object itself is deliberately unindexed. Terminal states
/// (Posted, Failed, Unavailable) are never swept.
/// </summary>
public partial class PublishFeedbackCronJob : ISparkCronJob
{
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly IMessageBus messageBus;
    [Inject] private readonly ILogger<PublishFeedbackCronJob> logger;

    public static string CronSchedule => "*/5 * * * *";

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var due = await session.Query<Build, Indexes.Builds_Overview>()
            .Where(b => b.FeedbackState == "Retry" && b.FeedbackNextAttemptAtUtc < now)
            .Take(32)
            .ToListAsync(cancellationToken);

        foreach (var build in due)
        {
            if (build.Id is null) continue;
            await messageBus.BroadcastAsync(new PublishFeedbackMessage { BuildId = build.Id }, cancellationToken);
        }

        if (due.Count > 0)
            logger.LogInformation("Re-enqueued {Count} check-run publish(es)", due.Count);
    }
}
