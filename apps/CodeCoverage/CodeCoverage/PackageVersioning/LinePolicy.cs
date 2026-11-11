using CodeCoverage.Forge;
using CodeCoverage.Versioning.Releases;

namespace CodeCoverage.PackageVersioning;

/// <summary>
/// The "Line policy" check (segment E, resilience PRD Q1b): what may land on a release line. A <b>current</b> line, the
/// newest of its ecosystem (D15, <see cref="ReleaseLinePattern.CurrentLines"/>), takes only the App's own backports of
/// default-branch commits; a fix goes to the default branch first. A <b>past</b> line takes people's pull requests too
/// (version-specific fixes). Ruleset 3 requires the check, bound to the App, so the rule needs no ruleset edit when a
/// platform upgrade cuts a new line: the new line becomes current and the old one past, judged per ecosystem.
/// </summary>
public static class LinePolicy
{
    public const string CheckName = MergeQueueRulesetPlanner.LinePolicyCheckName;

    /// <param name="line">The pull request's base, a branch the line pattern matches.</param>
    /// <param name="current">That line is the newest of its ecosystem.</param>
    /// <param name="appsBackport">The App opened the pull request as a backport onto this line.</param>
    public static ForgeVersioningCheck Check(ReleaseLine line, string branch, bool current, bool appsBackport, string defaultBranch)
    {
        if (!current)
            return new(CheckName, true, "success", $"{line} is a past line",
                $"`{branch}` is not the newest {line.Ecosystem} line, so it takes version-specific fixes from people as well as backports.", null);
        if (appsBackport)
            return new(CheckName, true, "success", "A backport onto the current line",
                $"`{branch}` is the current {line.Ecosystem} line. It takes only the App's backports of `{defaultBranch}` commits, and this is one.", null);
        return new(CheckName, true, "failure", $"{line} takes only backports",
            $"`{branch}` is the current {line.Ecosystem} line: everything on it comes from `{defaultBranch}`, so a change of its own would "
            + $"be lost, or block the line, on the next backport. Open this pull request against `{defaultBranch}` instead; once it merges it "
            + "reaches this line by backport (or `/release`, where the line is promoted).", null);
    }
}
