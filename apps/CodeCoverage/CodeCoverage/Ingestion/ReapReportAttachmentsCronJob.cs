using CodeCoverage.Entities;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Cron;
using MintPlayer.Spark;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;

namespace CodeCoverage.Ingestion;

/// <summary>
/// Deletes the uploaded coverage reports from builds finalized longer ago than
/// the retention window, keeping the build itself and everything derived from it.
/// </summary>
/// <remarks>
/// <para>
/// Raw reports are the one thing here that grows without bound and is never read
/// again. <c>ParseSessionRecipient</c> consumes them once at ingest; after that
/// every consumer — the file page, the tree, badges, the PR comment, the gate —
/// reads <c>FileCoverage</c> and the commit assembly instead. Measured on
/// production 2026-09-19: 142 MB of reports across 1,899 attachments, growing
/// ~3.8 MB a day for a single user.
/// </para>
/// <para>
/// Storage under this policy is <c>daily report bytes × window</c>, so the window
/// is the whole lever and is configurable. It is not zero by default because a
/// parser defect is usually found within days of shipping it, and until the
/// reports are gone such a fix can be replayed over the affected builds.
/// </para>
/// <para>
/// ⚠️ The <c>filelist</c> attachment is deliberately NOT reaped.
/// <c>BuildComparer</c> and <c>CommitAssembler</c> read it whenever this build is
/// a comparison or carry-forward base, which can be long after it finalized. It
/// is also the smaller half — 36 MB against 142 MB.
/// </para>
/// </remarks>
public partial class ReapReportAttachmentsCronJob : ISparkCronJob
{
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly IConfiguration configuration;
    [Inject] private readonly ILogger<ReapReportAttachmentsCronJob> logger;

    /// <summary>04:10 UTC. Nothing is urgent about reclaiming storage, and this keeps it clear of the 03:20 GitHub reconcile.</summary>
    public static string CronSchedule => "10 4 * * *";

    /// <summary>
    /// Days a finalized build keeps its reports. <c>0</c> reaps as soon as a build
    /// is finalized; a negative value disables the sweep entirely.
    /// </summary>
    private const int DefaultRetentionDays = 7;

    /// <summary>
    /// Builds per run. Each one costs a handful of attachment deletes, and the
    /// sweep is daily against a backlog that only grows by the day's builds, so
    /// there is no need to move faster.
    /// </summary>
    private const int MaxBuildsPerRun = 256;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var retentionDays = configuration.GetValue("Coverage:Retention:ReportAttachmentDays", DefaultRetentionDays);
        if (retentionDays < 0)
            return;

        var cutoff = DateTime.UtcNow.AddDays(-retentionDays);

        // ⚠️ `ReportsReapedAtUtc == null` would match nothing here. A document
        // that has never carried the field has no index entry for it, and
        // equality-to-null does not match an absent field — which is every build
        // written before this job existed, i.e. the entire backlog the sweep is
        // meant to reclaim. Ask whether the field exists instead.
        //
        // WhereLessThan on FinalizedAtUtc excludes open builds for the same
        // reason: an unfinalized build has no value there, so it cannot match.
        // Both shapes have to match, and neither predicate catches the other:
        // a build written before this field existed has NO entry for it, which
        // `= null` does not match; a build written since carries an explicit
        // null, which `exists` does match. Production will hold both for as long
        // as any pre-#420 build survives.
        var builds = await session.Advanced
            .AsyncDocumentQuery<Build, Indexes.Builds_Overview>()
            .WhereLessThan(b => b.FinalizedAtUtc, cutoff)
            .AndAlso()
            .OpenSubclause()
                .Not
                .WhereExists(b => b.ReportsReapedAtUtc)
                .OrElse()
                .WhereEquals(b => b.ReportsReapedAtUtc, (DateTime?)null)
            .CloseSubclause()
            .Take(MaxBuildsPerRun)
            .ToListAsync(cancellationToken);

        if (builds.Count == 0)
            return;

        // One attachment delete is one request, and a build can carry a dozen.
        using var requestScope = session.IgnoreMaxRequests(logger: logger);

        var reapedBuilds = 0;
        var reapedAttachments = 0;

        foreach (var build in builds)
        {
            if (cancellationToken.IsCancellationRequested) break;

            try
            {
                foreach (var name in ReportAttachmentNames(build))
                {
                    session.Advanced.Attachments.Delete(build, name);
                    reapedAttachments++;
                }

                // Stamped even when the build carried no reports: the marker is
                // what keeps the sweep from re-reading the same builds forever,
                // since a reaped build stays in this query while a deleted
                // document would simply leave it.
                build.ReportsReapedAtUtc = DateTime.UtcNow;
                reapedBuilds++;
            }
            catch (Exception ex)
            {
                // One unreapable build must not cost the rest of the run.
                logger.LogWarning(ex, "Could not reap report attachments on {BuildId}; the other builds continue", build.Id);
            }
        }

        await session.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Reaped {Attachments} report attachment(s) from {Builds} build(s) finalized before {Cutoff:u}",
            reapedAttachments, reapedBuilds, cutoff);
    }

    /// <summary>
    /// The build's report attachments — every attachment it holds except the
    /// per-session filelists.
    /// </summary>
    /// <remarks>
    /// Read off the loaded entity's metadata rather than from
    /// <c>BuildSession.RawFileNames</c>, so an attachment that never made it into
    /// that list is still reclaimed, and asked of the metadata rather than the
    /// server, so enumerating costs no request.
    /// <para>
    /// The test is the trailing segment, not a prefix or an extension:
    /// <c>UploadAttachments.FileListName</c> ends in exactly <c>/filelist</c>,
    /// while a report is <c>sessions/{id}/{index}-{name}</c>. A report that the
    /// uploader actually called "filelist" sanitises to <c>…/0-filelist</c> and
    /// so is still correctly treated as a report.
    /// </para>
    /// </remarks>
    private IEnumerable<string> ReportAttachmentNames(Build build)
        => session.Advanced.Attachments.GetNames(build)
            .Select(a => a.Name)
            .Where(name => !name.EndsWith("/filelist", StringComparison.Ordinal))
            .ToList();
}
