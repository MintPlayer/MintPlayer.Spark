namespace CodeCoverage.GitWriter.Jobs.Dependencies;

/// <summary>
/// The <c>migrate</c> patch profile (docs/code-coverage/dependency-updates-PRD.md R9 refined by R18): a migration
/// rewrites source, so the patch may touch the whole tree, add, delete and rename files, EXCEPT a deny-list:
/// <list type="bullet">
/// <item><c>.github/**</c> (any case), <c>.git</c> components, <c>.gitmodules</c>, <c>.gitattributes</c> and paths git
/// would quote (<see cref="PatchValidator.IsSafePath"/>);</item>
/// <item><c>coverage.yml</c>, the configuration the bot reads;</item>
/// <item>the bot's plan path, and all of <c>.mintplayer/</c> (R14);</item>
/// <item>symlinks, mode changes, executable files (any mode but 100644), submodules, binaries and copies
/// (<see cref="PatchValidator"/>'s shape rules).</item>
/// </list>
/// Everything else may change, <c>package.json</c>, the lockfile, <c>tsconfig*.json</c>, <c>angular.json</c>,
/// <c>nx.json</c> and <c>project.json</c> included (S17: real Angular 21→22 and Nx 22→23 migrations touch them all). The PR
/// is marked "contains code migrations" and never auto-merges (R9).
/// </summary>
public static class MigratePatchValidator
{
    public static PatchVerdict Validate(ReadOnlySpan<byte> patch, long maxBytes)
        => PatchValidator.Parse(patch, path =>
        {
            if (DependencyFiles.IsPlanPath(path))
                return PatchVerdict.Reject(DependencyPatchReasons.PlanPath, "The bot's plan file (R14) is never part of a patch.", path);
            if (!PatchValidator.IsSafePath(path))
                return PatchVerdict.Reject(PatchReasons.UnsafePath, null, path);
            if (DependencyFiles.IsCoverageConfig(path))
                return PatchVerdict.Reject(DependencyPatchReasons.DeniedPath, "A migration never edits coverage.yml.", path);
            return null;
        }, maxBytes, files: null);
}
