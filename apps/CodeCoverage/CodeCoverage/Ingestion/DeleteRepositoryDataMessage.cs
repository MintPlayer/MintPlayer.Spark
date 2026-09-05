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
/// </summary>
[MessageQueue("coverage-delete-repository-data")]
public record DeleteRepositoryDataMessage
{
    public required long RepositoryGitHubId { get; init; }

    /// <summary>Who asked, for the audit line in the log.</summary>
    public required string RequestedByUserId { get; init; }
}
