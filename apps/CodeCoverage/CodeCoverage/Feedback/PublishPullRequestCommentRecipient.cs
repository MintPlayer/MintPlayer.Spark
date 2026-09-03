using CodeCoverage.Entities;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Messaging.Abstractions;
using Raven.Client.Documents.Session;

namespace CodeCoverage.Feedback;

/// <summary>
/// Re-sends the body a pull request's comment still owes GitHub.
/// <para>
/// Deliberately dumb: it re-publishes <see cref="PullRequestFeedback.PendingBody"/>
/// verbatim rather than re-deriving it. Re-deriving would mean re-resolving the
/// base and re-fetching coverage.yml over the GitHub API, and any drift between
/// attempts would let the comment contradict the check-runs it was rendered
/// beside.
/// </para>
/// </summary>
public partial class PublishPullRequestCommentRecipient : IRecipient<PublishPullRequestCommentMessage>
{
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly IPullRequestCommentPublisher publisher;
    [Inject] private readonly ILogger<PublishPullRequestCommentRecipient> logger;

    public async Task HandleAsync(PublishPullRequestCommentMessage message, CancellationToken cancellationToken = default)
    {
        var feedback = await session.LoadAsync<PullRequestFeedback>(message.FeedbackId, cancellationToken);
        if (feedback is null) return;

        // Nothing owed, or nothing to publish through: a terminal state or a
        // repository that lost its installation between attempts.
        if (feedback.PendingBody is not { Length: > 0 } body
            || feedback.InstallationId is not { } installationId
            || feedback.Repository is null)
            return;

        var repository = await session.LoadAsync<Entities.Repository>(feedback.Repository, cancellationToken);
        if (repository is null) return;

        logger.LogInformation("Retrying the coverage comment on {Repo}#{Pr} (attempt {Attempts})",
            repository.FullName, feedback.PullRequestNumber, feedback.Attempts + 1);

        await publisher.PublishAsync(
            repository, installationId, feedback.PullRequestNumber,
            feedback.PendingSha ?? feedback.LastPublishedSha ?? string.Empty,
            body, cancellationToken);
    }
}
