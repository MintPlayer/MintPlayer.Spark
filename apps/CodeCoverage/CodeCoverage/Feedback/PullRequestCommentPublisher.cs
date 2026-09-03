using System.Security.Cryptography;
using System.Text;
using CodeCoverage.Entities;
using MintPlayer.SourceGenerators.Attributes;
using Octokit;
using Raven.Client.Documents.Session;

namespace CodeCoverage.Feedback;

public interface IPullRequestCommentPublisher
{
    /// <summary>
    /// Ensures the pull request carries exactly one comment with this body.
    /// Never throws: every outcome is recorded on the PullRequestFeedback
    /// outbox, because a GitHub failure must not undo the check-runs that were
    /// already posted in the same invocation.
    /// </summary>
    Task PublishAsync(Entities.Repository repository, long installationId, int pullRequestNumber, string sha, string body, CancellationToken cancellationToken);
}

/// <summary>
/// Owns the sticky comment: one per pull request, created once and edited
/// thereafter.
/// <para>
/// Recovery order matters. The stored id is tried first because it is one API
/// call; only when it is missing or gone does this list the PR's comments and
/// re-adopt by marker and author. Without that fallback, a human deleting the
/// comment would make the bot post a second one on the next push, and a third
/// after that.
/// </para>
/// </summary>
[Register(typeof(IPullRequestCommentPublisher), ServiceLifetime.Scoped)]
public partial class PullRequestCommentPublisher : IPullRequestCommentPublisher
{
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly IPullRequestCommentGateway gateway;
    [Inject] private readonly ILogger<PullRequestCommentPublisher> logger;

    private const int MaxAttempts = 5;

    public async Task PublishAsync(Entities.Repository repository, long installationId, int pullRequestNumber, string sha, string body, CancellationToken cancellationToken)
    {
        var id = PullRequestFeedback.DocumentId(repository.GitHubId, pullRequestNumber);
        var feedback = await session.LoadAsync<PullRequestFeedback>(id, cancellationToken);
        if (feedback is null)
        {
            feedback = new PullRequestFeedback
            {
                Repository = repository.Id,
                PullRequestNumber = pullRequestNumber,
            };
            await session.StoreAsync(feedback, id, cancellationToken);
        }

        var hash = Hash(body);
        if (feedback.CommentId is not null
            && feedback.State == "Posted"
            && feedback.LastPublishedBodyHash == hash
            && feedback.LastPublishedSha == sha)
        {
            // Nothing changed. An edit would be silent anyway (measured), so
            // this saves an API call rather than a notification.
            return;
        }

        // Recorded before the attempt so a retry has everything it needs even
        // if this process dies mid-call.
        feedback.InstallationId = installationId;
        feedback.PendingBody = body;
        feedback.PendingSha = sha;

        try
        {
            feedback.CommentId = await UpsertAsync(repository, installationId, pullRequestNumber, feedback.CommentId, body, cancellationToken);
            feedback.State = "Posted";
            feedback.Error = null;
            feedback.NextAttemptAtUtc = null;
            feedback.LastPublishedSha = sha;
            feedback.LastPublishedBodyHash = hash;
            feedback.LastPublishedAtUtc = DateTime.UtcNow;
            feedback.PendingBody = null;
            feedback.PendingSha = null;
            logger.LogInformation("Published coverage comment {CommentId} on {Repo}#{Pr}", feedback.CommentId, repository.FullName, pullRequestNumber);
        }
        // On the status code rather than the exception type: Octokit raises
        // ForbiddenException for most 403s but a bare ApiException on some
        // paths, and the response is what actually decides this.
        catch (ApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Forbidden)
        {
            // The installation has not accepted `Pull requests: write`. Retrying
            // cannot help — it would burn five attempts per build until someone
            // consents — so this is terminal-but-recoverable, exactly like a
            // repository with no installation at all.
            feedback.State = "Unavailable";
            feedback.Error = $"Pull requests: write not granted to the installation ({ex.Message})";
            feedback.NextAttemptAtUtc = null;
            // Nothing is owed any more: the sweep must not carry this forever.
            feedback.PendingBody = null;
            feedback.PendingSha = null;
            logger.LogInformation(
                "Coverage comment unavailable on {Repo}#{Pr}: the installation lacks Pull requests: write",
                repository.FullName, pullRequestNumber);
        }
        catch (Exception ex)
        {
            feedback.Attempts++;
            feedback.Error = ex.Message;
            if (feedback.Attempts >= MaxAttempts)
            {
                feedback.State = "Failed";
                feedback.NextAttemptAtUtc = null;
                feedback.PendingBody = null;
                feedback.PendingSha = null;
                logger.LogWarning(ex, "Giving up on the coverage comment for {Repo}#{Pr} after {Attempts} attempts", repository.FullName, pullRequestNumber, feedback.Attempts);
            }
            else
            {
                feedback.State = "Retry";
                feedback.NextAttemptAtUtc = DateTime.UtcNow + TimeSpan.FromMinutes(Math.Pow(2, feedback.Attempts));
                logger.LogWarning(ex, "Coverage comment failed for {Repo}#{Pr}; retry {Attempts}/{Max} at {Next}", repository.FullName, pullRequestNumber, feedback.Attempts, MaxAttempts, feedback.NextAttemptAtUtc);
            }
        }

        await session.SaveChangesAsync(cancellationToken);
    }

    private async Task<long> UpsertAsync(Entities.Repository repository, long installationId, int pullRequestNumber, long? existingId, string body, CancellationToken cancellationToken)
    {
        if (existingId is { } id)
        {
            try
            {
                await gateway.UpdateAsync(repository, installationId, id, body, cancellationToken);
                return id;
            }
            catch (NotFoundException)
            {
                // Deleted by a human. Fall through to adoption rather than
                // treating it as a failure.
                logger.LogInformation("Coverage comment {CommentId} on {Repo}#{Pr} is gone; re-adopting", id, repository.FullName, pullRequestNumber);
            }
        }

        var adopted = await AdoptAsync(repository, installationId, pullRequestNumber, cancellationToken);
        if (adopted is { } adoptedId)
        {
            await gateway.UpdateAsync(repository, installationId, adoptedId, body, cancellationToken);
            return adoptedId;
        }

        return await gateway.CreateAsync(repository, installationId, pullRequestNumber, body, cancellationToken);
    }

    /// <summary>
    /// Our own comment on this PR, found by marker AND bot authorship. The
    /// author half matters: a human quoting the bot's body — which contains the
    /// marker — must not be mistaken for the bot's own comment and edited.
    /// </summary>
    private async Task<long?> AdoptAsync(Entities.Repository repository, long installationId, int pullRequestNumber, CancellationToken cancellationToken)
    {
        var comments = await gateway.ListAsync(repository, installationId, pullRequestNumber, cancellationToken);
        foreach (var comment in comments)
        {
            if (comment.AuthoredByApp && comment.Body.Contains(PullRequestCommentRenderer.Marker, StringComparison.Ordinal))
                return comment.Id;
        }
        return null;
    }

    private static string Hash(string body)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(body)))[..16];
}
