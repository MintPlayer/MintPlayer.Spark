using CodeCoverage.Git;

namespace CodeCoverage.GitWriter.Jobs.Dependencies;

/// <summary>
/// The plan commit on the bot-owned dispatch ref (docs/code-coverage/dependency-updates-PRD.md R14): exactly
/// <c>expected_sha</c> plus ONE commit that adds or modifies only <see cref="DependencyFiles.PlanPath"/>, as a regular
/// file. Anything else (a second path, a <c>.github/</c> touch, a mode, a merge) is refused before dispatch: the
/// dispatched workflow's own definition must stay the default branch's (provenance).
/// </summary>
public static class PlanCommitRule
{
    /// <summary>Null when <paramref name="planCommit"/> is acceptable, else why not.</summary>
    public static async Task<string?> CheckAsync(Func<IReadOnlyList<string>, Task<GitResult>> git, string expectedSha, string planCommit)
    {
        var parents = await git(["rev-list", "--parents", "-n", "1", "--end-of-options", planCommit]);
        if (!parents.Succeeded)
            return "The plan commit cannot be read.";
        var ids = parents.StandardOutput.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (ids.Length != 2 || ids[1] != expectedSha)
            return "The plan commit must have exactly one parent, the planned commit.";
        var diff = await git(["diff-tree", "-r", "-z", "--no-commit-id", "--no-renames", expectedSha, planCommit]);
        if (!diff.Succeeded)
            return "The plan commit's change cannot be read.";
        var fields = diff.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 2)
            return "The plan commit must change exactly one path, the plan file.";
        var meta = fields[0].Split(' ');
        var path = fields[1];
        if (path != DependencyFiles.PlanPath)
            return $"The plan commit changes {path}; only {DependencyFiles.PlanPath} is allowed.";
        if (meta.Length != 5 || meta[1] != "100644" || meta[4] is not ("A" or "M"))
            return "The plan file must be added or modified as a regular, non-executable file.";
        return null;
    }
}
