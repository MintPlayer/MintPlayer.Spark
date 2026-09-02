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

    /// <summary>Scripted git parents by sha; anything unlisted answers null (no API path).</summary>
    public Dictionary<string, string> Parents { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Task<string?> GetFirstParentAsync(Repository repository, long? installationId, string sha, CancellationToken cancellationToken = default)
        => Task.FromResult(Parents.TryGetValue(sha, out var parent) ? parent : null);

    public Task<CommitComparison?> CompareAsync(Repository repository, long? installationId, string baseRef, string headSha, CancellationToken cancellationToken = default)
    {
        Calls.Add((baseRef, headSha));
        return Task.FromResult(comparison);
    }
}
