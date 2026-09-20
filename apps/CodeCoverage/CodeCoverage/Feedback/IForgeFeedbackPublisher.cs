using CodeCoverage.Entities;
using CodeCoverage.Forge;

namespace CodeCoverage.Feedback;

/// <summary>
/// Publishes a build's verdict back to the forge: the two commit-level statuses and the sticky
/// pull-request comment.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the verdict vocabulary is declared here rather than passed through.</b> GitHub check runs
/// conclude as success / failure / <em>neutral</em>, and nothing else we will ever target has a
/// neutral. GitLab commit statuses are pending, running, success, failed, canceled or skipped;
/// Bitbucket build statuses are SUCCESSFUL, FAILED, INPROGRESS or STOPPED. Passing Octokit's enum
/// across this boundary would make GitHub's vocabulary the contract and leave every other provider
/// to guess — so the interface states its own three outcomes and each provider maps them, losing
/// the mapping explicitly rather than by accident.
/// </para>
/// <para>
/// <b>Why the rich text goes to the comment and not the status.</b> Our coverage table is markdown
/// of a few hundred characters. GitHub check-run output accepts it happily; GitLab caps a commit
/// status <c>description</c> at 255 characters and Bitbucket Code Insights allows ten typed
/// key/value cells. A design in which the status carries the detail therefore cannot be implemented
/// on either, so the split is deliberate: the status carries a headline, a number and a link, and
/// the comment carries the table.
/// </para>
/// <para>
/// <b>Credentials do not appear here</b>, for the same reason they do not appear on
/// <see cref="Services.IForgeClient"/>: resolving them is the provider's business.
/// </para>
/// </remarks>
public interface IForgeFeedbackPublisher
{
    /// <summary>The single forge this instance publishes to.</summary>
    EForgeProvider Provider { get; }

    /// <summary>
    /// Creates or updates one named status on a commit, returning the provider's id for it so a
    /// re-publish updates rather than duplicates.
    /// </summary>
    /// <param name="existingId">The id from a previous publish, or null to create.</param>
    Task<long> PublishStatusAsync(
        Repository repository,
        string sha,
        string name,
        ForgeVerdict verdict,
        long? existingId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Ensures the pull request carries exactly one comment with this body.
    /// </summary>
    /// <remarks>
    /// Never throws: every outcome is recorded on the feedback outbox, because a failure here must
    /// not undo statuses that were already published in the same invocation.
    /// </remarks>
    Task PublishCommentAsync(
        Repository repository,
        int pullRequestNumber,
        string sha,
        string body,
        CancellationToken cancellationToken = default);
}
