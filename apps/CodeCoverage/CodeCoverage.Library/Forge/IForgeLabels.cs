using CodeCoverage.Entities;

namespace CodeCoverage.Forge;

/// <summary>
/// A repository's label definitions and a pull request's labels and changed files, for the PR size labeller
/// (docs/coverage_pr_size_labels_PRD.md D3, D5, D6, D8).
/// </summary>
/// <remarks>
/// <para>
/// <b>A narrow seam of its own</b> (<see cref="EForgeCapability.PullRequestLabels"/>), like <see cref="IForgeMergeQueue"/>:
/// a forge declaring the capability registers exactly one implementation for its provider, and the conformance test holds
/// both directions. GitHub is the only implementation; Bitbucket Cloud has no pull-request labels, and the GitLab port
/// implements this when it lands (S4).
/// </para>
/// <para>
/// The merge queue's own <see cref="IForgeMergeQueue.AddLabelAsync"/> stays where it is: moving it would widen this PR
/// without changing what either feature does.
/// </para>
/// <para>
/// A missing credential or permission throws <see cref="ForgeAccessDeniedException"/> naming the permission, so the check
/// run can say what to grant (D7).
/// </para>
/// </remarks>
public interface IForgeLabels
{
    EForgeProvider Provider { get; }

    /// <summary>Every label defined in the repository.</summary>
    Task<IReadOnlyList<ForgeLabel>> ListRepositoryLabelsAsync(Repository repository, CancellationToken cancellationToken = default);

    /// <summary>Creates a label. <see cref="ForgeLabelWrite.Conflict"/> when one with that name (case-insensitively) exists.</summary>
    Task<ForgeLabelWrite> CreateLabelAsync(Repository repository, ForgeLabelDefinition label, CancellationToken cancellationToken = default);

    /// <summary>
    /// Renames and restyles the label now called <paramref name="currentName"/>, keeping it on every pull request that
    /// carries it (S1). <see cref="ForgeLabelWrite.NotFound"/> when no such label exists, so the caller can create it.
    /// </summary>
    Task<ForgeLabelWrite> UpdateLabelAsync(Repository repository, string currentName, ForgeLabelDefinition label, CancellationToken cancellationToken = default);

    /// <summary>Deletes a label, which removes it from every pull request, closed ones too (S1). A missing label is <see cref="ForgeLabelWrite.NotFound"/>.</summary>
    Task<ForgeLabelWrite> DeleteLabelAsync(Repository repository, string name, CancellationToken cancellationToken = default);

    /// <summary>The names of the labels a pull request carries.</summary>
    Task<IReadOnlyList<string>> ListPullRequestLabelsAsync(Repository repository, int number, CancellationToken cancellationToken = default);

    /// <summary>Adds one label to a pull request.</summary>
    Task AddLabelAsync(Repository repository, int number, string label, CancellationToken cancellationToken = default);

    /// <summary>Removes one label from a pull request; false when it did not carry it.</summary>
    Task<bool> RemoveLabelAsync(Repository repository, int number, string label, CancellationToken cancellationToken = default);

    /// <summary>
    /// The pull request's changed files with their line counts, paged in full up to the forge's cap (GitHub: 3 000 files).
    /// Path and counts only, never the patch (S2).
    /// </summary>
    Task<IReadOnlyList<ForgePullRequestFile>> ListPullRequestFilesAsync(Repository repository, int number, CancellationToken cancellationToken = default);
}

/// <summary>A label as the forge holds it.</summary>
/// <param name="Color">Six lower-case hex digits, no <c>#</c>.</param>
/// <param name="Archived">The user archived it (S1): it stays on what carries it, and cannot be added to anything.</param>
public sealed record ForgeLabel(string Name, string Color, string? Description, bool Archived);

/// <summary>A label as the app wants it.</summary>
/// <param name="Color">Six lower-case hex digits, no <c>#</c>.</param>
public sealed record ForgeLabelDefinition(string Name, string Color, string Description);

/// <summary>The outcome of a label definition write.</summary>
public enum ForgeLabelWrite
{
    Done,
    /// <summary>The label to update or delete does not exist.</summary>
    NotFound,
    /// <summary>A label with the new name already exists.</summary>
    Conflict,
}

/// <summary>One changed file of a pull request, with its line counts.</summary>
public sealed record ForgePullRequestFile(string Path, int Additions, int Deletions);
