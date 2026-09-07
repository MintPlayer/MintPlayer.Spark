using MintPlayer.Spark.Messaging.Abstractions;

namespace CodeCoverage.Ingestion;

/// <summary>
/// Queued when an owner deletes a disconnected repository's data from its page; processed by
/// <see cref="DeleteRepositoryDataRecipient"/>.
/// <para>
/// Queued rather than done in the request because the sweep is unbounded: a repository's documents
/// are one per commit, per build, per covered file and per flag, which for a long-lived repository
/// is tens of thousands. Authorization has already happened at the point this is broadcast — the
/// message means "this has been authorized", so nothing may broadcast it without checking.
/// </para>
/// <para>
/// On <see cref="Feedback.CoverageQueues.Publishing"/> because it is retention, alongside
/// <see cref="DeletePullRequestBuildsMessage"/> — and because it may not have a queue of its own:
/// the licence caps subscriptions per database, and a third name would silently kill one of the
/// two that exist. Its own dedicated queue is what was dead in production, which is why the
/// button appeared to do nothing.
/// </para>
/// </summary>
[MessageQueue(Feedback.CoverageQueues.Publishing)]
public record DeleteRepositoryDataMessage
{
    public required long RepositoryGitHubId { get; init; }

    /// <summary>Who asked, for the audit line in the log.</summary>
    public required string RequestedByUserId { get; init; }
}
