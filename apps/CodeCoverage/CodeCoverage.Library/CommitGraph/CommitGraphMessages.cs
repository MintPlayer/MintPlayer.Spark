using MintPlayer.Spark.Messaging.Abstractions;

namespace CodeCoverage.CommitGraph;

/// <summary>
/// Clone or fetch one repository's commit-graph mirror (#504, PRD §4.2); handled by
/// <c>FetchCommitGraphRecipient</c>.
/// </summary>
/// <remarks>
/// Always broadcast with a key from <see cref="CommitGraphTriggers"/>, so a redelivered webhook or a
/// burst of page reads queues one fetch, not one per event.
/// </remarks>
[MessageQueue(Feedback.CoverageQueues.CommitGraph)]
public record FetchCommitGraphMessage
{
    /// <summary>The repository document id, e.g. <c>Repositories/github/123</c>.</summary>
    public required string RepositoryId { get; init; }

    /// <summary>
    /// Whether this request may create a mirror that does not exist. D10: only a graph read may;
    /// a push, a repository joining an installation and the hourly safety net refresh an existing
    /// mirror and do nothing for a repository nobody has looked at.
    /// </summary>
    public bool AllowClone { get; init; }
}

/// <summary>
/// Delete one repository's mirror and its <c>RepositoryGraphState</c> (PRD §5.2: the mirror dies
/// with the access); handled by <c>DeleteCommitGraphMirrorRecipient</c>.
/// </summary>
/// <remarks>
/// Not deduplicated: the sidecar's DELETE is idempotent, and a deletion that is merged into an
/// earlier one already processed would be a deletion that never happened.
/// </remarks>
[MessageQueue(Feedback.CoverageQueues.CommitGraph)]
public record DeleteCommitGraphMirrorMessage
{
    /// <summary>The repository document id, e.g. <c>Repositories/github/123</c>. The repository itself may already be gone.</summary>
    public required string RepositoryId { get; init; }

    /// <summary>Why, for the log: <c>disconnected</c>, <c>deleted</c>, <c>evicted</c>, …</summary>
    public string? Reason { get; init; }
}

/// <summary>The deduplication keys fetches are queued under.</summary>
/// <remarks>
/// ⚠️ <b>Never the bare <c>graph-fetch:{repoId}</c>.</b> <c>BroadcastOnceAsync</c> refuses a key for
/// as long as the first message's document exists, and processed messages are retained for days,
/// so a fixed key would allow one fetch per repository per retention window. Each key therefore
/// names what it deduplicates: one pushed head, one staleness window, one hour.
/// </remarks>
public static class CommitGraphTriggers
{
    /// <summary>One fetch per pushed ref update.</summary>
    public static string PushKey(string repositoryId, string @ref, string after)
        => $"graph-fetch:{repositoryId}:push:{@ref}:{after}";

    /// <summary>One fetch per repository joining an installation, per day.</summary>
    public static string AddedKey(string repositoryId, DateTimeOffset now)
        => $"graph-fetch:{repositoryId}:added:{now.UtcDateTime:yyyy-MM-dd}";

    /// <summary>
    /// One clone per time the state was marked syncing (<paramref name="syncingSinceUtc"/> is that
    /// mark). Not a time bucket: a second clone asked for in the same bucket (a mirror lost a minute
    /// after it was made) would be dropped while the state still said syncing.
    /// </summary>
    public static string CloneKey(string repositoryId, DateTime syncingSinceUtc)
        => $"graph-fetch:{repositoryId}:clone:{syncingSinceUtc.Ticks}";

    /// <summary>One fetch per read-triggered window (<paramref name="window"/> is the staleness bound).</summary>
    public static string ViewKey(string repositoryId, DateTimeOffset now, TimeSpan window)
        => $"graph-fetch:{repositoryId}:view:{now.UtcTicks / Math.Max(1, window.Ticks)}";

    /// <summary>One safety-net fetch per repository per UTC hour.</summary>
    public static string HourlyKey(string repositoryId, DateTimeOffset now)
        => $"graph-fetch:{repositoryId}:hourly:{now.UtcDateTime:yyyy-MM-ddTHH}";
}
