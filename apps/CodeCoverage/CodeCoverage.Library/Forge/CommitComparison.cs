namespace CodeCoverage.Forge;

/// <summary>
/// A three-dot compare (<c>base...head</c>): the merge-base commit and, per changed file, the lines
/// the head side <em>added</em> in new-file numbering — the entire input patch coverage needs, and
/// the merge base is what base resolution needs.
/// </summary>
/// <param name="MergeBaseSha">The common ancestor the forge computed for <c>base...head</c>.</param>
/// <param name="Files">Changed files with their added-line numbers; removed files carry none.</param>
/// <param name="Truncated">
/// The forge capped the comparison (GitHub stops at 300 files). When set, the diff under-reports and
/// consumers must say so rather than pretend completeness.
/// </param>
/// <remarks>
/// Lives in the library rather than beside an implementation because it is the <em>shape</em> of a
/// diff, not one forge's rendering of it — and because <see cref="IForgeIntegration"/>, which every
/// forge library implements, is typed on it.
/// </remarks>
public sealed record CommitComparison(string? MergeBaseSha, IReadOnlyList<DiffFile> Files, bool Truncated);

/// <param name="Path">Repo-relative forward-slash path — the same shape <c>PathNormalizer</c> produces.</param>
/// <param name="Status">
/// added | modified | removed | renamed. ⚠️ GitHub's vocabulary, adopted as ours: an implementation
/// for another forge maps onto these rather than introducing its own.
/// </param>
/// <param name="PreviousPath">The former path for a rename; null otherwise.</param>
/// <param name="AddedLines">
/// Line numbers in the head file the diff added; empty when the forge sent no patch (binary or
/// oversized files), which is not the same as a file with no additions.
/// </param>
public sealed record DiffFile(string Path, string Status, string? PreviousPath, int[] AddedLines);
