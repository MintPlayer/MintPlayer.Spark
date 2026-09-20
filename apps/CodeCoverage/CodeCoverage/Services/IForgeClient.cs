using CodeCoverage.Entities;
using CodeCoverage.Forge;

namespace CodeCoverage.Services;

/// <summary>
/// Reads from a forge on a repository's behalf: comparisons, parentage and file content.
/// </summary>
/// <remarks>
/// <para>
/// <b>No credential appears in these signatures, and that is the point.</b> Before this interface,
/// every caller resolved a GitHub App installation id itself — the same five lines repeated at five
/// sites — and passed it in. That made "installation id" part of the vocabulary of ingestion,
/// feedback and browsing, none of which should know what a GitHub App is; GitLab has no such
/// concept to supply. Resolving the credential is the provider's own business, so it moved behind
/// the implementation, taking the duplication with it.
/// </para>
/// <para>
/// <b>Unavailability is never an exception.</b> A repository whose credential is missing, revoked or
/// simply never granted returns <c>null</c>, and the caller discloses the degradation rather than
/// failing. This mirrors the existing contract of the GitHub services underneath and is load-bearing
/// for public repositories, which are readable with no credential at all.
/// </para>
/// </remarks>
public interface IForgeClient
{
    /// <summary>The single forge this instance reads from.</summary>
    EForgeProvider Provider { get; }

    /// <summary>
    /// Whether we hold a credential that can act for this repository, and if not, a message fit to
    /// show a user.
    /// </summary>
    /// <remarks>
    /// Callers need this separately from the read methods because "no credential" and "the read
    /// found nothing" are different states that deserve different handling — the feedback pipeline
    /// parks a build as <c>Unavailable</c> for the first and retries the second. The message comes
    /// from the provider so it can name the real remedy ("the GitHub App is not installed") without
    /// the caller knowing which forge it is talking about.
    /// </remarks>
    Task<ForgeAccess> CheckAccessAsync(Repository repository, CancellationToken cancellationToken = default);

    /// <summary>
    /// Three-dot compare (<c>base...head</c>): the merge-base commit, and per changed file the
    /// lines the head side added in new-file numbering. Null when no path to the forge exists.
    /// </summary>
    Task<CommitComparison?> CompareAsync(Repository repository, string baseRef, string headSha, CancellationToken cancellationToken = default);

    /// <summary>
    /// The git first parent of <paramref name="sha"/> according to the forge, or null when no API
    /// path exists or the call fails. Authoritative over any stored hint.
    /// </summary>
    Task<string?> GetFirstParentAsync(Repository repository, string sha, CancellationToken cancellationToken = default);

    /// <summary>Deletes a branch. Best-effort, never throws; the implementation logs its outcome.</summary>
    Task DeleteBranchAsync(Repository repository, string branch, CancellationToken cancellationToken = default);

    /// <summary>Source of one file at an exact commit, or null when unavailable. Never stored.</summary>
    Task<string?> GetFileContentAsync(Repository repository, string sha, string path, CancellationToken cancellationToken = default);
}
