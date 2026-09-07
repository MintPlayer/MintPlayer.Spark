using MintPlayer.Spark.Messaging.Abstractions;

namespace CodeCoverage.Feedback;

/// <summary>
/// A pull request was opened or reopened: post the placeholder comment so the
/// author sees coverage is coming, before CI has finished.
/// <para>
/// Broadcast by the pull_request webhook, which stays a pure persister — the
/// GitHub write happens off the webhook thread, so an outage on GitHub's side
/// can never fail the delivery and cost us the event.
/// </para>
/// <para>
/// On the shared <see cref="CoverageQueues.Publishing"/> queue. It had its own
/// queue when first written, which is exactly why it never ran: that pushed the
/// database past its subscription cap and the queue was silently never created.
/// </para>
/// </summary>
[MessageQueue(CoverageQueues.Publishing)]
public record OpenPullRequestCommentMessage
{
    public required long RepositoryGitHubId { get; init; }

    public required int PullRequestNumber { get; init; }

    /// <summary>Head sha at the moment the PR was opened.</summary>
    public required string HeadSha { get; init; }

    /// <summary>
    /// Whether the PR was opened by a bot (dependabot, renovate).
    /// <para>
    /// Measured: dependabot-triggered workflow runs receive no repository
    /// secrets, and this repo's pull-request.yml grants no id-token: write, so
    /// such a PR can never upload coverage. A placeholder there would say
    /// "waiting for coverage" forever.
    /// </para>
    /// </summary>
    public bool AuthorIsBot { get; init; }
}
