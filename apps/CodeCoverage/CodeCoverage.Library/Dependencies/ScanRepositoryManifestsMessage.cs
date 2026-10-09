using MintPlayer.Spark.Messaging.Abstractions;

namespace CodeCoverage.Dependencies;

/// <summary>
/// Re-reads one repository's manifests from its default branch into its
/// <see cref="Entities.RepositoryManifest"/>; handled by <c>ScanRepositoryManifestsRecipient</c>.
/// </summary>
/// <remarks>
/// On <see cref="Feedback.CoverageQueues.Publishing"/> because the scan calls the forge, and that
/// queue carries everything that does. A scan whose tree is unchanged costs two forge requests and
/// no write, so the nightly fan-out is cheap.
/// <para>
/// Always broadcast with a deduplication key from <see cref="ManifestScanTriggers"/>, so a redelivered
/// webhook or a second reconcile on the same day does not queue a second scan.
/// </para>
/// </remarks>
[MessageQueue(Feedback.CoverageQueues.Publishing)]
public record ScanRepositoryManifestsMessage
{
    /// <summary>The repository document id, e.g. <c>Repositories/github/123</c>.</summary>
    public required string RepositoryId { get; init; }
}

/// <summary>When a manifest scan is due, and the deduplication key it is queued under.</summary>
public static class ManifestScanTriggers
{
    /// <summary>
    /// GitHub's push payload lists at most 20 commits. A push with that many may have dropped some,
    /// so its file list cannot prove that no manifest changed.
    /// </summary>
    public const int PushCommitListCap = 20;

    /// <summary>
    /// Whether a push to the default branch should rescan: a commit touched a manifest path (added,
    /// modified or removed), or the commit list may be truncated.
    /// </summary>
    public static bool PushTouchesManifests(int commitCount, IEnumerable<string> changedPaths)
        => commitCount >= PushCommitListCap || changedPaths.Any(ManifestFiles.IsManifest);

    /// <summary>One scan per pushed head.</summary>
    public static string PushKey(string repositoryId, string headSha) => $"manifest-scan-{repositoryId}-{headSha}";

    /// <summary>One scheduled scan per repository per UTC day.</summary>
    public static string DailyKey(string repositoryId, DateTimeOffset now) => $"manifest-scan-{repositoryId}-{now.UtcDateTime:yyyy-MM-dd}";
}
