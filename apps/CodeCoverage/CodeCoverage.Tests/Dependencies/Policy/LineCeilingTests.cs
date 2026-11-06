using CodeCoverage.Dependencies;
using CodeCoverage.Dependencies.Policy;
using CodeCoverage.Git.Dependencies;
using Xunit;
using static CodeCoverage.Tests.Dependencies.Policy.DependencyTestKit;

namespace CodeCoverage.Tests.Dependencies.Policy;

/// <summary>
/// #568 D13 (coverage_backport_PRD §8.4): a release line's run never takes a major, so a platform-anchored package stays
/// under the line's major. <c>dependencies.lines</c> sets the rest: patch (default), all (up to a minor), security (the
/// security fixes only). A security fix may go up to a minor on any line; one that needs a major is held for a human.
/// </summary>
public class LineCeilingTests
{
    private static LookupDocument Express()
        => Lookup(Record("express", "4.17.0",
            Candidate(EUpdateType.Major, "5.0.1"), Candidate(EUpdateType.Minor, "4.21.2"), Candidate(EUpdateType.Patch, "4.17.3")));

    [Fact]
    public void A_patch_line_takes_the_patch_and_holds_the_minor_and_the_major()
    {
        var outcome = Evaluate(Express(), line: EDependencyLineUpdates.Patch);

        Planned(outcome).Should().Equal(["express: express@4.17.3"]);
        // Like a hold, a passed-over candidate is not listed when a lower one ships: no "a minor exists" on every line's pull request.
        outcome.Held.Should().BeEmpty();
    }

    [Fact]
    public void A_patch_line_with_only_a_minor_on_offer_lists_it_as_above_the_ceiling()
    {
        var outcome = Evaluate(Lookup(Record("express", "4.17.0", Candidate(EUpdateType.Minor, "4.21.2"))), line: EDependencyLineUpdates.Patch);

        outcome.Groups.Should().BeEmpty();
        outcome.Held.Should().ContainSingle(e => e.Reason == EDependencyReportReason.AboveLineCeiling && e.Target == "4.21.2")
            .Which.Detail.Should().Contain("patches only");
    }

    [Fact]
    public void An_all_line_takes_the_minor_but_never_the_major()
    {
        var outcome = Evaluate(Express(), line: EDependencyLineUpdates.All);

        Planned(outcome).Should().Equal(["express: express@4.21.2"]);
        Planned(Evaluate(Lookup(Record("express", "4.17.0", Candidate(EUpdateType.Major, "5.0.1"))), line: EDependencyLineUpdates.All))
            .Should().BeEmpty();
    }

    [Fact]
    public void A_security_line_takes_only_the_security_fixes_up_to_a_minor()
    {
        var lookup = Lookup(
            Record("lodash", "4.16.0", Candidate(EUpdateType.Minor, "4.17.21")),
            Record("chalk", "4.1.0", Candidate(EUpdateType.Patch, "4.1.2")));
        DependencySecurityFix[] fixes = [new("npm", "lodash", "GHSA-xxxx")];

        var outcome = Evaluate(lookup, mode: EDependencyRunMode.Security, security: fixes, line: EDependencyLineUpdates.Security);

        Planned(outcome).Should().Equal(["lodash: lodash@4.17.21"]);
        outcome.Held.Should().ContainSingle(e => e.DepName == "chalk" && e.Reason == EDependencyReportReason.AboveLineCeiling);
    }

    [Fact]
    public void A_security_fix_that_needs_a_major_is_held_for_a_human()
    {
        var lookup = Lookup(Record("lodash", "3.10.1", Candidate(EUpdateType.Major, "4.17.21")));
        DependencySecurityFix[] fixes = [new("npm", "lodash", "GHSA-xxxx")];

        var outcome = Evaluate(lookup, mode: EDependencyRunMode.Security, security: fixes, line: EDependencyLineUpdates.Patch);

        outcome.Groups.Should().BeEmpty();
        outcome.Held.Should().ContainSingle(e => e.Reason == EDependencyReportReason.AboveLineCeiling)
            .Which.Detail.Should().Contain("needs a major");
    }

    [Fact]
    public void The_default_branch_has_no_ceiling()
    {
        Planned(Evaluate(Express(), line: null)).Should().NotEqual(["express: express@4.17.3"]);
    }
}
