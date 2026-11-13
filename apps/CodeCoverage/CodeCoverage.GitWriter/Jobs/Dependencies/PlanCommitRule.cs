using CodeCoverage.Git;

namespace CodeCoverage.GitWriter.Jobs.Dependencies;

/// <summary>
/// The plan commit on the bot-owned dispatch ref (docs/code-coverage/dependency-updates-PRD.md R14): exactly
/// <c>expected_sha</c> plus ONE commit that adds or modifies <see cref="DependencyFiles.PlanPath"/>, as a regular file, and
/// at most the root <c>global.json</c>'s <c>sdk.version</c>, moved to the plan's SDK (#658, <see cref="GlobalJsonPreset"/>).
/// Anything else (another path, a <c>.github/</c> touch, a mode, a merge, any other <c>global.json</c> change) is refused
/// before dispatch: the dispatched workflow's own definition must stay the default branch's (provenance).
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
        if (fields.Length is not (2 or 4))
            return "The plan commit must change the plan file, and at most the root global.json's SDK version.";

        var changes = new Dictionary<string, string[]>(StringComparer.Ordinal);
        for (var i = 0; i < fields.Length; i += 2)
            changes[fields[i + 1]] = fields[i].Split(' ');
        if (!changes.TryGetValue(DependencyFiles.PlanPath, out var plan))
            return $"The plan commit changes {string.Join(", ", changes.Keys)}; only {DependencyFiles.PlanPath} is allowed.";
        if (plan.Length != 5 || plan[1] != "100644" || plan[4] is not ("A" or "M"))
            return "The plan file must be added or modified as a regular, non-executable file.";
        if (changes.Count == 1)
            return null;

        if (!changes.TryGetValue(GlobalJsonPreset.Path, out var sdk))
            return $"The plan commit changes {string.Join(", ", changes.Keys.Where(k => k != DependencyFiles.PlanPath))}; besides the plan file only the root {GlobalJsonPreset.Path} is allowed.";
        if (sdk.Length != 5 || sdk[0] != ":100644" || sdk[1] != "100644" || sdk[4] != "M")
            return $"The plan commit may only modify an existing, regular {GlobalJsonPreset.Path}.";
        var before = await git(["cat-file", "blob", sdk[2]]);
        var after = await git(["cat-file", "blob", sdk[3]]);
        var planText = await git(["cat-file", "blob", plan[3]]);
        if (!before.Succeeded || !after.Succeeded || !planText.Succeeded)
            return $"The plan commit's {GlobalJsonPreset.Path} cannot be read.";
        if (GlobalJsonPreset.Preset(before.StandardOutput, planText.StandardOutput) != after.StandardOutput)
            return $"The plan commit may change {GlobalJsonPreset.Path} only by moving sdk.version to the SDK of the plan's .NET platform group.";
        return null;
    }
}
