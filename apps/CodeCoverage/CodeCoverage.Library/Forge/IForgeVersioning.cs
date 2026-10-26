using CodeCoverage.Entities;

namespace CodeCoverage.Forge;

/// <summary>
/// What package versioning needs from a forge beyond git itself (docs/coverage_package_versioning_PRD.md D16): pull-request
/// titles and issues (D14), Releases (D15), release branches and backport pull requests (D10, D12), comments, and the
/// workflow dispatch of trigger B (D3).
/// </summary>
/// <remarks>
/// <para>
/// <b>A narrow seam of its own</b> (<see cref="EForgeCapability.Versioning"/>), like <see cref="IForgeLabels"/>: a forge
/// declaring the capability registers exactly one implementation for its provider. GitHub is the only one; what GitLab
/// and Bitbucket need is listed on #422.
/// </para>
/// <para>
/// Tags are not here: they are git objects, pushed through the git writer like any other ref.
/// </para>
/// <para>
/// A missing credential or permission throws <see cref="ForgeAccessDeniedException"/> naming the permission, so the check
/// run can say what to grant.
/// </para>
/// </remarks>
public interface IForgeVersioning
{
    EForgeProvider Provider { get; }

    /// <summary>A pull request's title, body and state, read fresh: a queued message may be older than a human's edit.</summary>
    Task<ForgePullRequestText?> GetPullRequestTextAsync(Repository repository, int number, CancellationToken cancellationToken = default);

    /// <summary>An issue of this repository with its labels; null when there is none, or the number is a pull request.</summary>
    Task<ForgeIssue?> GetIssueAsync(Repository repository, int number, CancellationToken cancellationToken = default);

    /// <summary>Sets a pull request's title.</summary>
    Task UpdatePullRequestTitleAsync(Repository repository, int number, string title, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates or updates the check named <see cref="ForgeVersioningCheck.Name"/> on <paramref name="sha"/> and returns its
    /// id. Without <paramref name="existingId"/> it looks the check up by name first, so a redelivery updates one check.
    /// </summary>
    Task<long> PublishCheckAsync(Repository repository, string sha, ForgeVersioningCheck check, long? existingId, CancellationToken cancellationToken = default);

    /// <summary>Creates the Release for an existing tag; <see cref="ForgeWriteOutcome.Exists"/> when the tag already has one.</summary>
    Task<ForgeWriteOutcome> CreateReleaseAsync(Repository repository, ForgeRelease release, CancellationToken cancellationToken = default);

    /// <summary>Creates a branch at a commit; <see cref="ForgeWriteOutcome.Exists"/> when the name is taken.</summary>
    Task<ForgeWriteOutcome> CreateBranchAsync(Repository repository, string branch, string sha, CancellationToken cancellationToken = default);

    /// <summary>Opens a pull request and returns its number, or the number of the open one with the same head and base.</summary>
    Task<int> OpenPullRequestAsync(Repository repository, string head, string @base, string title, string body, CancellationToken cancellationToken = default);

    /// <summary>The merged pull requests into <paramref name="baseBranch"/> that the commit belongs to (a squash or merge commit's one).</summary>
    Task<IReadOnlyList<int>> MergedPullRequestsAsync(Repository repository, string sha, string baseBranch, CancellationToken cancellationToken = default);

    /// <summary>Comments on an issue or pull request.</summary>
    Task CommentAsync(Repository repository, int number, string body, CancellationToken cancellationToken = default);

    /// <summary>Fires <c>workflow_dispatch</c> on a workflow file of the repository at a branch (trigger B, D3).</summary>
    Task DispatchWorkflowAsync(Repository repository, string workflowFile, string branch, CancellationToken cancellationToken = default);
}

/// <summary>A pull request's text and state, for title correction (D14) and backports (D10).</summary>
/// <param name="MergeCommitSha">The commit a merge produced; null while unmerged.</param>
public sealed record ForgePullRequestText(
    int Number,
    string Title,
    string? Body,
    string HeadRef,
    string HeadSha,
    string BaseRef,
    bool IsOpen,
    bool Merged,
    bool AuthorIsBot,
    string? MergeCommitSha,
    IReadOnlyList<string> Labels);

/// <summary>An issue and its label names.</summary>
public sealed record ForgeIssue(int Number, IReadOnlyList<string> Labels);

/// <summary>One versioning check run: in progress, or completed with a conclusion.</summary>
/// <param name="Conclusion">"success", "neutral" or "failure"; ignored while in progress.</param>
public sealed record ForgeVersioningCheck(string Name, bool Completed, string Conclusion, string Title, string Summary, string? Text);

/// <summary>A Release for an existing tag (D15).</summary>
public sealed record ForgeRelease(string TagName, string Name, string Body, bool Prerelease);

/// <summary>The outcome of a write that may meet an existing object.</summary>
public enum ForgeWriteOutcome
{
    Created,
    Exists,
}
