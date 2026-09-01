using CodeCoverage.Entities;

namespace CodeCoverage.Services;

/// <summary>
/// GitHub's three-dot compare (<c>base...head</c>): the merge-base commit and,
/// per changed file, the lines the head side *added* (new-file numbering) —
/// which is the entire input patch coverage needs, and the merge-base is what
/// base resolution needs. Never throws for unavailability: a repo without an
/// App installation and without public access simply gets null, and the caller
/// discloses the degradation.
/// </summary>
public interface IGitHubDiffService
{
    Task<CommitComparison?> CompareAsync(Repository repository, long? installationId, string baseRef, string headSha, CancellationToken cancellationToken = default);
}

/// <param name="MergeBaseSha">The common ancestor GitHub computed for base...head.</param>
/// <param name="Files">Changed files with their added-line numbers; removed files carry none.</param>
/// <param name="Truncated">GitHub caps a comparison at 300 files — when hit, the diff under-reports and consumers must say so rather than pretend.</param>
public sealed record CommitComparison(string? MergeBaseSha, IReadOnlyList<DiffFile> Files, bool Truncated);

/// <param name="Path">Repo-relative forward-slash path — the same shape PathNormalizer produces.</param>
/// <param name="Status">added | modified | removed | renamed (GitHub's vocabulary).</param>
/// <param name="AddedLines">Line numbers in the head file that the diff added; empty when GitHub sent no patch (binary/huge files).</param>
public sealed record DiffFile(string Path, string Status, string? PreviousPath, int[] AddedLines);
