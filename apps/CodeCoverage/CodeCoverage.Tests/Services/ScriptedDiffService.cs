using CodeCoverage.Entities;
using CodeCoverage.Forge;
using CodeCoverage.Services;

namespace CodeCoverage.Tests.Services;

/// <summary>
/// The forge client without a forge: answers from a script, or (default) with null — the exact
/// shape a repository with no API access produces, which is what most tests want so the resolver
/// falls through to the walk.
/// </summary>
/// <remarks>
/// Named for the diff it scripts rather than for the interface it implements, because that is what
/// every test using it cares about. It now also stands in for file-content reads, which return null
/// unless scripted: a test that wants no <c>coverage.yml</c> gets exactly that, which is the
/// overwhelmingly common case.
/// </remarks>
public sealed class ScriptedDiffService(CommitComparison? comparison = null) : IForgeClient
{
    public List<(string BaseRef, string HeadSha)> Calls { get; } = [];

    /// <summary>Scripted git parents by sha; anything unlisted answers null (no API path).</summary>
    public Dictionary<string, string> Parents { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Scripted file content keyed by <c>{sha}/{path}</c>; anything unlisted answers null.</summary>
    public Dictionary<string, string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Whether the scripted forge claims a usable credential. Defaults to true so the feedback
    /// pipeline proceeds past its access check; set false to exercise the Unavailable branch.
    /// </summary>
    public bool AccessAvailable { get; set; } = true;

    public EForgeProvider Provider => EForgeProvider.GitHub;

    public Task<ForgeAccess> CheckAccessAsync(Repository repository, CancellationToken cancellationToken = default)
        => Task.FromResult(AccessAvailable
            ? ForgeAccess.Yes
            : ForgeAccess.No("No GitHub App installation for this repository."));

    public Task<string?> GetFirstParentAsync(Repository repository, string sha, CancellationToken cancellationToken = default)
        => Task.FromResult(Parents.TryGetValue(sha, out var parent) ? parent : null);

    public Task<CommitComparison?> CompareAsync(Repository repository, string baseRef, string headSha, CancellationToken cancellationToken = default)
    {
        Calls.Add((baseRef, headSha));
        return Task.FromResult(comparison);
    }

    public Task<string?> GetFileContentAsync(Repository repository, string sha, string path, CancellationToken cancellationToken = default)
        => Task.FromResult(Files.TryGetValue($"{sha}/{path}", out var content) ? content : null);
}
