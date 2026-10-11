using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Cron;
using MintPlayer.Spark.Messaging.Abstractions;

namespace CodeCoverage.CommitGraph;

/// <summary>
/// Runs one queued clone or fetch (#504), modelled on <c>ScanRepositoryManifestsRecipient</c>: all
/// the logic is in <see cref="ICommitGraphMirrors"/>.
/// </summary>
/// <remarks>
/// A messaging claim can expire during a long clone and the message be redelivered (D19). The
/// redelivered call reaches the sidecar while the first is still running and joins it, so the
/// clone is not done twice.
/// </remarks>
public partial class FetchCommitGraphRecipient : IRecipient<FetchCommitGraphMessage>
{
    [Inject] private readonly ICommitGraphMirrors mirrors;

    public Task HandleAsync(FetchCommitGraphMessage message, CancellationToken cancellationToken = default)
        => mirrors.FetchAsync(message.RepositoryId, message.AllowClone, cancellationToken);
}

/// <summary>Deletes one mirror whose access was lost (PRD §5.2). Throws on a sidecar failure, so it is retried.</summary>
public partial class DeleteCommitGraphMirrorRecipient : IRecipient<DeleteCommitGraphMirrorMessage>
{
    [Inject] private readonly ICommitGraphMirrors mirrors;

    public Task HandleAsync(DeleteCommitGraphMirrorMessage message, CancellationToken cancellationToken = default)
        => mirrors.DeleteAsync(message.RepositoryId, message.Reason ?? "access lost", cancellationToken: cancellationToken);
}

/// <summary>
/// The hourly safety net (PRD §4.2, D23): refreshes every existing mirror whose last fetch is over
/// an hour old. It covers dropped webhooks, tag events nothing else reports, and public repositories
/// with no installation, which get no webhooks at all. It never creates a mirror (D10).
/// </summary>
public partial class RefreshCommitGraphMirrorsCronJob : ISparkCronJob
{
    [Inject] private readonly ICommitGraphMirrors mirrors;
    [Inject] private readonly ILogger<RefreshCommitGraphMirrorsCronJob> logger;

    /// <summary>Hourly, off the hour so it does not line up with the other jobs.</summary>
    public static string CronSchedule => "17 * * * *";

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var queued = await mirrors.QueueRefreshesAsync(cancellationToken);
        if (queued > 0) logger.LogInformation("Queued {Count} commit-graph mirror refreshes", queued);
    }
}

/// <summary>
/// The daily eviction (D10): deletes mirrors nobody has viewed for <c>CommitGraph:EvictAfterDays</c>,
/// and any left behind by a repository that is gone or disconnected.
/// </summary>
public partial class EvictCommitGraphMirrorsCronJob : ISparkCronJob
{
    [Inject] private readonly ICommitGraphMirrors mirrors;
    [Inject] private readonly ILogger<EvictCommitGraphMirrorsCronJob> logger;

    /// <summary>03:40 UTC, after the nightly reconcile (03:20) has settled which repositories are reachable.</summary>
    public static string CronSchedule => "40 3 * * *";

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var removed = await mirrors.EvictIdleAsync(cancellationToken);
        logger.LogInformation("Commit-graph eviction removed {Count} mirrors", removed);
    }
}
