using MintPlayer.Spark.Messaging.Abstractions;

namespace CodeCoverage.Ingestion;

/// <summary>
/// Queued when a pull request is merged; processed by
/// <see cref="DeletePullRequestBuildsRecipient"/>. Retention decision D4
/// (docs/coverage-analyzer-suite.md): PR build data is deleted at merge, not
/// on a timer — repos without the App get no webhook and keep theirs, an
/// accepted, documented gap.
/// </summary>
[MessageQueue("coverage-delete-pr-builds")]
public record DeletePullRequestBuildsMessage
{
    public required long RepositoryGitHubId { get; init; }
    public required int PullRequestNumber { get; init; }
}
