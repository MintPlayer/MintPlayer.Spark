using CodeCoverage.Entities;
using CodeCoverage.Indexes;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Cron;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;

namespace CodeCoverage.Ingestion;

/// <summary>
/// One-time in effect: commits that carried coverage before the Δ columns
/// existed have no verified parent and no deltas. Each run takes a slice of
/// commits with coverage whose parent was never looked up, asks GitHub for the
/// real first parent, and re-stamps both Δ columns from the stored headlines.
/// Every processed commit is marked as attempted (answered or not), so the
/// query drains and the job goes quiet; new commits are stamped by the
/// assembler and never reach it. Paced to stay inside GitHub's anonymous rate
/// limit, so a repository without an App installation is served the same way.
/// </summary>
public partial class BackfillCommitDeltasCronJob : ISparkCronJob
{
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly ICommitAssembler assembler;
    [Inject] private readonly ILogger<BackfillCommitDeltasCronJob> logger;

    public static string CronSchedule => "*/5 * * * *";

    // 4 lookups per 5 minutes = 48/h, under GitHub's 60/h anonymous limit, so
    // one uniform pace is safe for every repository whether or not the App is
    // installed. A few hundred historical commits drain in a few hours.
    private const int SliceSize = 4;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var pending = await session.Query<Commits_ByRepository.Result, Commits_ByRepository>()
            .Where(r => r.HasCoverage && !r.ParentLookupDone)
            .OrderBy(r => r.AuthoredAt)
            .OfType<Commit>()
            .Take(SliceSize)
            .ToListAsync(cancellationToken);

        if (pending.Count == 0)
            return;

        foreach (var commit in pending)
        {
            if (commit.Id is null)
                continue;
            await assembler.RestampDeltasAsync(commit.Id, cancellationToken);
            // A commit with no repository has no API path; mark it so it is not
            // picked up again.
            commit.ParentLookupAttemptedAtUtc ??= DateTime.UtcNow;
        }

        await session.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Backfilled parent and Δ columns for {Count} commit(s)", pending.Count);
    }
}
