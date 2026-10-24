namespace CodeCoverage.Entities;

/// <summary>
/// What the app last read of a repository's <c>versioning.dispatch</c> and release-branch patterns (#153 M11, PRD D3
/// trigger B). It exists so a push to an ordinary branch costs one document load and no forge call: only the default
/// branch and the branches these patterns match are planned.
/// </summary>
/// <remarks>
/// A cache, never the authority: every planned push re-reads <c>coverage.yml</c> from the default branch (D17) and
/// rewrites it. A push to the default branch is always planned, so a change to the patterns lands with that push.
/// Loaded by id only (<see cref="ForRepository"/>); not indexed.
/// </remarks>
public class VersioningDispatchState
{
    public string? Id { get; set; }

    /// <summary>The workflow file the server dispatches, or null when the repository uses the action step alone.</summary>
    public string? Dispatch { get; set; }

    /// <summary><c>versioning.releaseBranches</c>, defaults applied.</summary>
    public List<string> ReleaseBranches { get; set; } = [];

    public DateTime UpdatedAtUtc { get; set; }

    /// <summary><c>Repositories/github/1</c> becomes <c>VersioningDispatchStates/github/1</c>.</summary>
    public static string ForRepository(string repositoryId)
    {
        const string prefix = "Repositories/";
        if (!repositoryId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"'{repositoryId}' is not a repository document id.", nameof(repositoryId));
        return "VersioningDispatchStates/" + repositoryId[prefix.Length..];
    }
}
