using CodeCoverage.Entities;
using CodeCoverage.Services;

namespace CodeCoverage.Tests.Services;

/// <summary>
/// The diff service without GitHub: answers from a script, or (default) with
/// null — the exact shape a repo with no API access produces, which is what
/// most tests want so the resolver falls through to the walk.
/// </summary>
public sealed class ScriptedDiffService(CommitComparison? comparison = null) : IGitHubDiffService
{
    public List<(string BaseRef, string HeadSha)> Calls { get; } = [];

    public Task<CommitComparison?> CompareAsync(Repository repository, long? installationId, string baseRef, string headSha, CancellationToken cancellationToken = default)
    {
        Calls.Add((baseRef, headSha));
        return Task.FromResult(comparison);
    }
}
