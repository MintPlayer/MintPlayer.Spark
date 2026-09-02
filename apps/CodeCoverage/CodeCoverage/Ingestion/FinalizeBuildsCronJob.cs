using CodeCoverage.Entities;
using CodeCoverage.Services;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Cron;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;

namespace CodeCoverage.Ingestion;

/// <summary>
/// Closes open builds nobody finished explicitly: ~2 minutes after the last
/// upload (debounce — the workflow's jobs are done) or 30 minutes after
/// creation (timeout — a crashed job must never wedge a build open forever).
/// Cluster-safe via Spark cron's compare-exchange claim.
/// </summary>
public partial class FinalizeBuildsCronJob : ISparkCronJob
{
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly IGitHubDiffService diffService;
    [Inject] private readonly MintPlayer.Spark.Messaging.Abstractions.IMessageBus messageBus;
    [Inject] private readonly ILogger<FinalizeBuildsCronJob> logger;

    public static string CronSchedule => "* * * * *";

    private static readonly TimeSpan DebounceAfterLastUpload = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan TimeoutAfterCreation = TimeSpan.FromMinutes(30);

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var debounceCutoff = now - DebounceAfterLastUpload;
        var timeoutCutoff = now - TimeoutAfterCreation;

        var openBuilds = await session.Query<Build, Indexes.Builds_Overview>()
            .Where(b => b.Status == "Open")
            .Where(b => b.LastUploadAtUtc < debounceCutoff || b.CreatedAtUtc < timeoutCutoff)
            .Take(128)
            .ToListAsync(cancellationToken);

        if (openBuilds.Count == 0)
            return;

        foreach (var build in openBuilds)
        {
            // Only debounce once every session is parsed — otherwise the commit
            // would get a partial summary; the timeout closes regardless.
            var allParsed = build.Sessions.All(s => s.ParseStatus != "Pending");
            var timedOut = build.CreatedAtUtc < timeoutCutoff;
            if (!allParsed && !timedOut)
                continue;

            var reason = timedOut && !allParsed ? "Timeout" : "Debounce";
            if (reason == "Timeout")
            {
                // A session still Pending at timeout will never parse — the
                // worker has long since retried and given up. Label it so the
                // UI and badge stop implying work is in flight.
                foreach (var pending in build.Sessions.Where(s => s.ParseStatus == "Pending"))
                {
                    pending.ParseStatus = "Failed";
                    pending.Error = "Never parsed before the build timed out";
                }
            }
            await BuildFinalizer.Finalize(session, diffService, build, reason, cancellationToken);
            logger.LogInformation("Finalized build {BuildId} ({Reason})", build.Id, reason);
        }

        await session.SaveChangesAsync(cancellationToken);

        // After the save: an assemble/feedback message racing an unsaved
        // finalize would load a still-open build and skip. The assembler
        // publishes feedback once the commit's headline is rebuilt.
        foreach (var build in openBuilds)
        {
            if (build.Id is null)
                continue;
            if (build.Commit is not null)
                await messageBus.BroadcastAsync(new AssembleCommitMessage { CommitId = build.Commit, BuildId = build.Id }, cancellationToken);
            else
                await messageBus.BroadcastAsync(new Feedback.PublishFeedbackMessage { BuildId = build.Id }, cancellationToken);
        }
    }
}
