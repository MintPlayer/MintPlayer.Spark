using CodeCoverage.Entities;
using CodeCoverage.Feedback;
using CodeCoverage.Forge;
using MintPlayer.SourceGenerators.Attributes;

namespace CodeCoverage.Services;

/// <summary>
/// GitHub's <see cref="IForgeIntegration"/> — a facade over the per-concern GitHub services (D16).
/// </summary>
/// <remarks>
/// <para>
/// <b>Deliberately thin.</b> GitHub's surface is roughly 3,000 lines across accounts, repositories,
/// diffs, content, statuses, comments, reconciliation and boards. Collapsing that into one type
/// would produce the file nobody reviews, and would orphan the ~30 existing GitHub test files that
/// target the services directly. Those services stay, keep their tests, and become internal to the
/// GitHub library in M15; this type exists so the <em>app</em> sees one interface.
/// </para>
/// <para>
/// So there should be almost no logic here. Anything that is not a delegation is a sign the
/// behaviour belongs in a service instead — the exception being the capability list, which is a
/// statement about GitHub and has nowhere better to live.
/// </para>
/// </remarks>
[Register(typeof(IForgeIntegration), ServiceLifetime.Scoped)]
public partial class GitHubForgeIntegration : IForgeIntegration
{
    [Inject] private readonly IForgeAccessService access;
    [Inject] private readonly IForgeClient client;
    [Inject] private readonly IForgeFeedbackPublisher feedback;

    public EForgeProvider Provider => EForgeProvider.GitHub;

    /// <remarks>
    /// ⚠️ <see cref="EForgeCapability.Boards"/> is declared because GitHub Projects V2 exists, but
    /// the board members are not on this interface yet — they arrive in M2c. Declaring the
    /// capability before the members would make the conformance test (M2b) fail, correctly, so the
    /// list grows with the interface rather than ahead of it.
    /// <para>
    /// <see cref="EForgeCapability.CiIdentity"/> is likewise absent until the OIDC surface moves.
    /// It is also the capability with the sharpest caveat: GitHub mints a job token for ordinary
    /// runs but <em>never</em> for a pull request from a fork, so a capability declared per forge
    /// cannot express it honestly — whatever lands must be per call, not per implementation.
    /// </para>
    /// </remarks>
    public EForgeCapability[] Capabilities => [EForgeCapability.Statuses, EForgeCapability.Comments];

    public Task<ForgeVisibility> GetVisibilityAsync(CancellationToken cancellationToken = default)
        => access.GetVisibilityAsync(cancellationToken);

    public Task<ForgeOwner[]> GetAllowedOwnersAsync(CancellationToken cancellationToken = default)
        => access.GetAllowedOwnersAsync(cancellationToken);

    public Task<bool> IsOwnerAllowedAsync(ForgeOwner owner, CancellationToken cancellationToken = default)
        => access.IsOwnerAllowedAsync(owner, cancellationToken);

    public Task InvalidateAsync(CancellationToken cancellationToken = default)
        => access.InvalidateAsync(cancellationToken);

    public Task<ForgeAccess> CheckAccessAsync(Repository repository, CancellationToken cancellationToken = default)
        => client.CheckAccessAsync(repository, cancellationToken);

    public Task<CommitComparison?> CompareAsync(Repository repository, string baseRef, string headSha, CancellationToken cancellationToken = default)
        => client.CompareAsync(repository, baseRef, headSha, cancellationToken);

    public Task<string?> GetFirstParentAsync(Repository repository, string sha, CancellationToken cancellationToken = default)
        => client.GetFirstParentAsync(repository, sha, cancellationToken);

    public Task<string?> GetFileContentAsync(Repository repository, string sha, string path, CancellationToken cancellationToken = default)
        => client.GetFileContentAsync(repository, sha, path, cancellationToken);

    public Task<long> PublishStatusAsync(Repository repository, string sha, string name, ForgeVerdict verdict, long? existingId, CancellationToken cancellationToken = default)
        => feedback.PublishStatusAsync(repository, sha, name, verdict, existingId, cancellationToken);

    public Task DeleteBranchAsync(Repository repository, string branch, CancellationToken cancellationToken = default)
        => client.DeleteBranchAsync(repository, branch, cancellationToken);

    public Task PublishCommentAsync(Repository repository, int pullRequestNumber, string sha, string body, CancellationToken cancellationToken = default)
        => feedback.PublishCommentAsync(repository, pullRequestNumber, sha, body, cancellationToken);
}
