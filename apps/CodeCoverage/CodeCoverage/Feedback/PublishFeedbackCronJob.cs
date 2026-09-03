using CodeCoverage.Entities;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Cron;
using MintPlayer.Spark.Messaging.Abstractions;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;

namespace CodeCoverage.Feedback;

/// <summary>
/// Re-enqueues publishes whose last attempt failed transiently: the check-runs
/// of a build, and the sticky comment of a pull request. Terminal states
/// (Posted, Failed, Unavailable) are never swept.
/// <para>
/// The two are swept separately because they fail separately. The check-runs
/// filter on the queryable mirrors on Build (<see cref="Build.FeedbackState"/>),
/// since BuildFeedback is an embedded unindexed object; PullRequestFeedback is
/// its own document, so its own State is filterable. Sweeping only the former
/// would leave a failed comment stranded whenever the check-runs of the same
/// build had succeeded — which is the common case, since they are posted first.
/// </para>
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

        var comments = await session.Query<PullRequestFeedback, Indexes.PullRequestFeedbacks_Overview>()
            .Where(f => f.State == "Retry" && f.NextAttemptAtUtc < now)
            .Take(32)
            .ToListAsync(cancellationToken);

        foreach (var feedback in comments)
        {
            if (feedback.Id is null) continue;
            await messageBus.BroadcastAsync(new PublishPullRequestCommentMessage { FeedbackId = feedback.Id }, cancellationToken);
        }

        if (comments.Count > 0)
            logger.LogInformation("Re-enqueued {Count} coverage comment publish(es)", comments.Count);
    }
}
