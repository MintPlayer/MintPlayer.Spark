using CodeCoverage.Dependencies;
using CodeCoverage.Dependencies.Policy;
using CodeCoverage.Entities;
using CodeCoverage.Git.Dependencies;
using CodeCoverage.LookupReferences;
using CodeCoverage.Tests.CommitGraph;
using Xunit;
using static CodeCoverage.Tests.Dependencies.Policy.DependencyTestKit;

namespace CodeCoverage.Tests.Dependencies.Policy;

/// <summary>
/// R6 approvals, bound per gap E to the group, its dependency set, each target major and the base <c>coverage.yml</c> blob
/// (§13.11.3 #15: "after a newer major, a group-membership change … → expired"), R9's migrate hook and R19's notes.
/// </summary>
public class ApprovalPolicyTests
{
    private const string Blob = "3b18e512dba79e4c8300dd08aeb37f8e728b8dad";

    private static DependencyPolicyOutcome Approved(LookupDocument lookup, string group, DependencyPolicyConfig? config = null, MutableTime? clock = null)
    {
        var now = clock?.GetUtcNow() ?? Now;
        var first = Evaluate(lookup, config, now: now, configBlob: Blob);
        DependencyHolds.Approve(first.NextState, first, group, Blob, "alice", 1, now).Should().NotBeNull();
        return first;
    }

    [Fact]
    public void An_approved_major_is_planned_and_never_auto_merges()
    {
        var lookup = Lookup(Record("express", "4.17.0", Candidate(EUpdateType.Major, "5.1.0")));
        var first = Approved(lookup, "express");

        var outcome = Evaluate(lookup, state: first.NextState, configBlob: Blob);

        var group = outcome.Groups.Should().ContainSingle().Which;
        group.Approved.Should().BeTrue();
        group.AutoMerge.Should().BeFalse();
        group.TargetMajors.Should().Equal(["express@5"]);
        outcome.ExpiredApprovals.Should().BeEmpty();
    }

    [Fact]
    public void An_approval_expires_when_a_newer_major_appears()
    {
        var first = Approved(Lookup(Record("express", "4.17.0", Candidate(EUpdateType.Major, "5.1.0"))), "express");

        var outcome = Evaluate(Lookup(Record("express", "4.17.0", Candidate(EUpdateType.Major, "5.1.0"), Candidate(EUpdateType.Major, "6.0.0"))),
            state: first.NextState, configBlob: Blob);

        outcome.Groups.Should().BeEmpty();
        outcome.AwaitingApproval.Should().ContainSingle().Which.Target.Should().Be("6.0.0");
        outcome.ExpiredApprovals.Should().ContainSingle().Which.TargetMajors.Should().Equal(["express@5"]);
        outcome.NextState.Approvals.Should().BeEmpty();
    }

    [Fact]
    public void An_approval_expires_when_the_base_coverage_yml_changes()
    {
        var lookup = Lookup(Record("express", "4.17.0", Candidate(EUpdateType.Major, "5.1.0")));
        var first = Approved(lookup, "express");

        var outcome = Evaluate(lookup, state: first.NextState, configBlob: "another-blob");

        outcome.Groups.Should().BeEmpty();
        outcome.ExpiredApprovals.Should().ContainSingle();
    }

    [Fact]
    public void An_approval_expires_when_the_group_gains_a_member()
    {
        var two = Lookup(
            Record("nx", "22.7.12", Candidate(EUpdateType.Major, "23.3.0", group: "nx")),
            Record("@nx/js", "22.7.12", Candidate(EUpdateType.Major, "23.3.0", group: "nx")));
        var first = Approved(two, "nx");
        first.NextState.Approvals.Single().DepNames.Should().Equal(["@nx/js", "nx"]);

        var three = Lookup([.. two.Records, Record("@nx/angular", "22.7.12", Candidate(EUpdateType.Major, "23.3.0", group: "nx"))]);
        var outcome = Evaluate(three, state: first.NextState, configBlob: Blob);

        outcome.Groups.Should().BeEmpty();
        outcome.ExpiredApprovals.Should().ContainSingle();
        outcome.AwaitingApproval.Select(e => e.DepName).Should().BeEquivalentTo(["nx", "@nx/js", "@nx/angular"]);
    }

    [Fact]
    public void An_approval_survives_a_hold_on_its_group()
    {
        var lookup = Lookup(Record("express", "4.17.0", Candidate(EUpdateType.Major, "5.1.0")));
        var first = Approved(lookup, "express");
        DependencyHolds.AddCommandHold(first.NextState, "express", isGroup: true, "bob", Now);

        var held = Evaluate(lookup, state: first.NextState, configBlob: Blob);
        held.Groups.Should().BeEmpty();
        held.ExpiredApprovals.Should().BeEmpty("the group still offers exactly what was approved");

        DependencyHolds.RemoveCommandHold(held.NextState, "express");
        Evaluate(lookup, state: held.NextState, configBlob: Blob).Groups.Should().ContainSingle().Which.Approved.Should().BeTrue();
    }

    [Fact]
    public void Nothing_above_an_enforced_ceiling_can_be_approved()
    {
        var config = Config(null, AccountRule("express", EDependencyThreshold.Minor));
        var lookup = Lookup(Record("express", "4.17.0", Candidate(EUpdateType.Major, "5.1.0")));
        var outcome = Evaluate(lookup, config, configBlob: Blob);

        outcome.AwaitingApproval.Should().BeEmpty();
        DependencyHolds.Approve(outcome.NextState, outcome, "express", Blob, "alice", 1, Now).Should().BeNull();
    }

    [Fact]
    public void An_approved_major_with_a_migrate_rule_carries_its_workflow_and_never_auto_merges()
    {
        var config = Config("dependencies:\n  migrate:\n    - match: [\"nx\", \"@nx/*\"]\n      onMajor: nx-migrate.yml\n");
        var lookup = Lookup(
            Record("nx", "22.7.12", Candidate(EUpdateType.Major, "23.3.0", group: "nx")),
            Record("@nx/js", "22.7.12", Candidate(EUpdateType.Major, "23.3.0", group: "nx")));
        var first = Approved(lookup, "nx", config);

        var outcome = Evaluate(lookup, config, first.NextState, configBlob: Blob);

        var group = outcome.Groups.Should().ContainSingle().Which;
        group.MigrateWorkflow.Should().Be("nx-migrate.yml");
        group.AutoMerge.Should().BeFalse();
    }

    [Fact]
    public void A_migrations_extra_bump_that_hits_a_hold_gets_a_blocking_note()
    {
        // S17: nx migrate --include=all bumped typescript ~5.9.2 -> 6.0.3 beside the nx group (R19).
        var result = DependencyContractReader.ReadMigrateResult(ContractFixture("valid", "migrate-result.nx-22-to-23.json")).Document!;
        var state = new DependencyUpdateState();
        var fleet = new DependencyFleetState();

        DependencyMigrationNotes.Check(result, Config(), state, fleet).Should().BeEmpty("nothing holds typescript 6.0.3");

        fleet.RedVersions.Add(new DependencyFleetRed { Ecosystem = "npm", DepName = "typescript", Version = "6.0.3", Repository = "Repositories/github/2" });
        DependencyMigrationNotes.Check(result, Config(), state, fleet).Should().ContainSingle()
            .Which.Reason.Should().StartWith("held version introduced by migration");

        DependencyMigrationNotes.Check(result, Config(null, AccountRule("typescript", EDependencyThreshold.Minor)), state, new DependencyFleetState())
            .Should().ContainSingle("5 -> 6 is above the enforced ceiling");
    }
}
