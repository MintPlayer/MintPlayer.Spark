using CodeCoverage.Entities;

namespace CodeCoverage.Ingestion;

/// <summary>
/// The two numbers a partial (nx-affected) build earns against its base
/// (docs/coverage-analyzer-suite.md §1), computed purely from two tree
/// summaries so nothing is stored and nothing touches the max-only merger:
///
/// <para><b>Scoped baseline</b> — the base restricted to the paths this build
/// measured: the like-for-like denominator, honest by construction.</para>
///
/// <para><b>Projection</b> — the base tree with the measured files overwritten
/// and PR-deleted files pruned (a base path absent from the head's git file
/// list no longer exists): a whole-workspace number that *asserts* unmeasured
/// files unchanged. The caller labels it and carries the completeness verdict;
/// this class only reports what it could and couldn't do.</para>
///
/// Both are line-based: tree summaries carry no branch totals, so
/// <see cref="CoverageSummary.BranchesTotal"/> stays zero here by design.
/// </summary>
public static class PartialComparison
{
    public sealed record Result(
        CoverageSummary ScopedBaseline,
        CoverageSummary Projection,
        int FilesInScope,
        int PrunedFiles);

    public static Result Compute(BuildTreeSummary headTree, BuildTreeSummary baseTree, IReadOnlyCollection<string>? headFileList)
    {
        var headPaths = headTree.Files.Select(f => f.Path).ToHashSet(StringComparer.Ordinal);

        var scopedFiles = baseTree.Files.Where(f => headPaths.Contains(f.Path)).ToList();

        // Tree paths are normalized forward-slash repo-relative; git ls-files
        // output already is too, but unify defensively.
        var liveFiles = headFileList?
            .Select(p => p.Replace('\\', '/'))
            .ToHashSet(StringComparer.Ordinal);

        var merged = new Dictionary<string, TreeFileSummary>(StringComparer.Ordinal);
        var pruned = 0;
        foreach (var file in baseTree.Files)
        {
            // A measured file self-evidently exists; pruning only ever removes
            // base entries the head neither measured nor lists. Without a file
            // list nothing is pruned — the caller reports that as incomplete.
            if (liveFiles is not null && !liveFiles.Contains(file.Path) && !headPaths.Contains(file.Path))
            {
                pruned++;
                continue;
            }
            merged[file.Path] = file;
        }
        foreach (var file in headTree.Files)
            merged[file.Path] = file;

        return new Result(Sum(scopedFiles), Sum(merged.Values), scopedFiles.Count, pruned);
    }

    private static CoverageSummary Sum(IReadOnlyCollection<TreeFileSummary> files) => new()
    {
        LinesCovered = files.Sum(f => f.LinesCovered),
        LinesCoverable = files.Sum(f => f.LinesCoverable),
        FilesCount = files.Count,
    };
}
