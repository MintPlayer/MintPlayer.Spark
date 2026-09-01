using CodeCoverage.Entities;

namespace CodeCoverage.Services;

/// <summary>
/// Resolves the commit a partial upload's comparison should run against.
///
/// The chain (docs/coverage-analyzer-suite.md §1): the uploader-declared base
/// exactly; then the PR merge-base (slotted in once the diff service exists);
/// then a bounded walk down the default branch's covered commits; then none.
/// A candidate is usable only when its finalized build's tree summary still
/// exists — <c>Commit.Coverage</c>/<c>HasCoverage</c> deliberately survive as
/// display denormalizations after a PR's build data is deleted at merge, so
/// the flag must never be trusted without the document behind it.
///
/// Resolution never fails: a missing base is the routine case (#11 SP3), and
/// the caller's contract is to abstain, not error. The result always discloses
/// how far it strayed from what was asked for.
/// </summary>
public interface IBaseResolver
{
    Task<ResolvedBase> ResolveAsync(Repository repository, Commit head, string? declaredBaseSha, CancellationToken cancellationToken);
}

/// <param name="RequestedSha">What the uploader declared, verbatim; null when nothing was declared.</param>
/// <param name="ResolvedSha">The commit the comparison will actually use; null when nothing resolved.</param>
/// <param name="Mode">One of <see cref="ResolvedBase.Exact"/> / <see cref="ResolvedBase.MergeBase"/> / <see cref="ResolvedBase.Walked"/> / <see cref="ResolvedBase.None"/> — the API's <c>baseResolution</c> value.</param>
/// <param name="BaseBuildId">The finalized build whose tree summary carries the base's per-file numbers.</param>
/// <param name="Coverage">The base commit's whole-workspace totals.</param>
/// <param name="Branch">The base commit's recorded branch, for display.</param>
public sealed record ResolvedBase(string? RequestedSha, string? ResolvedSha, string Mode, string? BaseBuildId, CoverageSummary? Coverage, string? Branch = null)
{
    public const string Exact = "exact";
    public const string MergeBase = "mergeBase";
    public const string Walked = "walked";
    public const string None = "none";
}
