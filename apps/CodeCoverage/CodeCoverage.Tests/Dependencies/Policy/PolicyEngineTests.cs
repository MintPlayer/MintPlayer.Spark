using CodeCoverage.Dependencies;
using CodeCoverage.Dependencies.Policy;
using CodeCoverage.Entities;
using CodeCoverage.Git.Dependencies;
using CodeCoverage.LookupReferences;
using Xunit;
using static CodeCoverage.Tests.Dependencies.Policy.DependencyTestKit;

namespace CodeCoverage.Tests.Dependencies.Policy;

/// <summary>The policy engine on synthetic records: one rule per case (PRD §13.10 R1, R6, R10, R17, Q9, Q15, Q16, D-C1,
/// D-S12a, gap A).</summary>
public class PolicyEngineTests
{
    [Fact]
    public void A_patch_within_the_threshold_is_planned_and_may_auto_merge()
    {
        var outcome = Evaluate(Lookup(Record("chalk", "4.1.0", Candidate(EUpdateType.Patch, "4.1.2"))));

        var group = outcome.Groups.Should().ContainSingle().Which;
        group.Group.Should().Be("chalk");
        group.Level.Should().Be(EUpdateLevel.Patch);
        group.AutoMerge.Should().BeTrue();
        outcome.AutoMergeable.Should().BeTrue();
    }

    [Fact]
    public void A_major_over_the_threshold_waits_for_approval_and_the_minor_below_it_ships()
    {
        var outcome = Evaluate(Lookup(Record("express", "4.17.0",
            Candidate(EUpdateType.Major, "5.1.0"),
            Candidate(EUpdateType.Minor, "4.22.0"))));

        Planned(outcome).Should().Equal(["express: express@4.22.0"]);
        var waiting = outcome.AwaitingApproval.Should().ContainSingle().Which;
        waiting.Target.Should().Be("5.1.0");
        waiting.Detail.Should().Contain("approve express");
    }

    [Fact]
    public void Of_two_majors_the_newest_is_the_one_awaiting_approval()
    {
        var outcome = Evaluate(Lookup(Record("serilog", "2.12.0",
            Candidate(EUpdateType.Major, "3.1.1"),
            Candidate(EUpdateType.Major, "4.4.0"))));

        outcome.AwaitingApproval.Should().ContainSingle().Which.Target.Should().Be("4.4.0");
    }

    [Fact]
    public void A_major_labelled_minor_is_caught_by_the_floor()
    {
        // Gap D: a forged label. The leading segment differs, so it is a major whatever the report says (R17).
        var outcome = Evaluate(Lookup(Record("left-pad", "1.3.0", Candidate(EUpdateType.Minor, "2.0.0"))));

        outcome.Groups.Should().BeEmpty();
        outcome.AwaitingApproval.Should().ContainSingle().Which.Level.Should().Be(EUpdateLevel.Major);
    }

    [Fact]
    public void A_release_younger_than_the_cooldown_waits_and_says_until_when()
    {
        var released = Now.AddDays(-1);
        var outcome = Evaluate(Lookup(Record("chalk", "4.1.0", Candidate(EUpdateType.Patch, "4.1.2", released.ToString("O")))));

        outcome.Groups.Should().BeEmpty();
        var entry = outcome.Available.Should().ContainSingle().Which;
        entry.Reason.Should().Be(EDependencyReportReason.Cooldown);
        entry.Until.Should().Be(released.AddDays(3));
    }

    private static LookupRecord ActionPin(string repository, string tag, string workflow, string released)
        => Record(repository, tag, Candidate(EUpdateType.Digest, tag, released) with { Digest = "1d2c066310fa3fa1e24efa9efeb9d2a0c42606fa" }) with
        {
            Ecosystem = "github-actions", Manager = "github-actions", Datasource = "github-tags",
            PackageFile = workflow, LockFiles = [], CurrentDigest = "bfd709a9d1e1c3c7c9a2f7a0c2b4e6d8f0a1b2c3",
        };

    /// <summary>
    /// P2 (production re-test 2026-10-17): the bot's own action pin waited three days after every release, so onboarded
    /// repositories ran the previous action (without R31) meanwhile. It is the bot's runtime: never cooled down. Any
    /// other action released an hour ago still waits.
    /// </summary>
    [Fact]
    public void The_bots_own_action_pin_skips_the_cooldown_and_other_actions_do_not()
    {
        var released = Now.AddHours(-1).ToString("O");
        var outcome = Evaluate(Lookup(
            ActionPin("MintPlayer/MintPlayer.Spark", "dependency-update-v1", ".github/workflows/dependency-lookup.yml", released),
            ActionPin("MintPlayer/MintPlayer.Spark", "dependency-update-v1", ".github/workflows/dependency-apply.yml", released),
            ActionPin("acme/some-action", "v1", ".github/workflows/ci.yml", released)));

        outcome.Groups.SelectMany(g => g.Items).Select(i => i.Record.PackageFile).Should()
            .BeEquivalentTo([".github/workflows/dependency-lookup.yml", ".github/workflows/dependency-apply.yml"]);
        outcome.Available.Should().ContainSingle().Which.DepName.Should().Be("acme/some-action");
    }

    [Fact]
    public void Without_a_releaseTimestamp_the_cooldown_counts_from_the_first_time_the_bot_saw_it()
    {
        var lookup = Lookup(Record("node", "20-alpine", Candidate(EUpdateType.Minor, "20.20.2", released: null)) with { Ecosystem = "docker", Datasource = "docker", Manager = "dockerfile", PackageFile = "Dockerfile", LockFiles = [] });

        var first = Evaluate(lookup);
        first.Groups.Should().BeEmpty("D-S12a: first seen now, so three days to go");
        first.Available.Should().ContainSingle().Which.Until.Should().Be(Now.AddDays(3));
        first.NextState.FirstSeen.Should().ContainSingle(s => s.DepName == "node" && s.Version == "20.20.2");

        Evaluate(lookup, state: first.NextState, now: Now.AddDays(2)).Groups.Should().BeEmpty();
        Evaluate(lookup, state: first.NextState, now: Now.AddDays(3)).Groups.Should().ContainSingle();
    }

    [Fact]
    public void A_releaseTimestamp_in_the_future_is_read_as_unknown()
    {
        var outcome = Evaluate(Lookup(Record("chalk", "4.1.0", Candidate(EUpdateType.Patch, "4.1.2", Now.AddYears(1).ToString("O")))));

        outcome.Available.Should().ContainSingle().Which.Until.Should().Be(Now.AddDays(3));
    }

    [Fact]
    public void While_the_newest_patch_cools_down_an_older_patch_of_the_same_level_ships()
    {
        var outcome = Evaluate(Lookup(Record("chalk", "4.1.0",
            Candidate(EUpdateType.Patch, "4.1.2", Now.AddHours(-2).ToString("O")),
            Candidate(EUpdateType.Patch, "4.1.1"))));

        Planned(outcome).Should().Equal(["chalk: chalk@4.1.1"]);
        outcome.Available.Should().ContainSingle(e => e.Target == "4.1.2" && e.Reason == EDependencyReportReason.Cooldown);
    }

    [Fact]
    public void A_group_moves_as_one_so_a_member_in_its_cooldown_holds_back_the_others()
    {
        var outcome = Evaluate(Lookup(
            Record("@nx/js", "22.7.12", Candidate(EUpdateType.Patch, "22.7.13", group: "nx monorepo")),
            Record("nx", "22.7.12", Candidate(EUpdateType.Patch, "22.7.13", Now.AddHours(-1).ToString("O"), group: "nx monorepo"))));

        outcome.Groups.Should().BeEmpty("D-C1: related packages always move together");
        outcome.Available.Should().ContainSingle(e => e.DepName == "nx" && e.Group == "nx monorepo");
    }

    [Fact]
    public void One_dependency_in_many_package_files_is_one_group()
    {
        var outcome = Evaluate(Lookup(
            Record("vitest", "5.0.3", Candidate(EUpdateType.Patch, "5.0.5")),
            Record("vitest", "5.0.3", Candidate(EUpdateType.Patch, "5.0.5")) with { PackageFile = "apps/x/package.json", LockFiles = ["apps/x/package-lock.json"] }));

        outcome.Groups.Should().ContainSingle().Which.Items.Select(i => i.Record.PackageFile).Should().Equal(["package.json", "apps/x/package.json"]);
    }

    [Fact]
    public void A_configured_group_beats_the_lookups_grouping()
    {
        var config = Config("dependencies:\n  rules:\n    - match: [\"vitest\", \"@vitest/*\"]\n      group: vitest\n");

        var outcome = Evaluate(Lookup(
            Record("vitest", "5.0.3", Candidate(EUpdateType.Patch, "5.0.5", group: "renovate-vitest")),
            Record("@vitest/coverage-v8", "5.0.3", Candidate(EUpdateType.Patch, "5.0.5"))), config);

        outcome.Groups.Should().ContainSingle().Which.Group.Should().Be("vitest");
    }

    [Fact]
    public void Ignored_excluded_and_skipped_records_are_listed_as_ignored_and_never_planned()
    {
        var config = Config("dependencies:\n  ignore: [\"left-pad\"]\n  exclude: [\"samples/**\"]\n");

        var outcome = Evaluate(Lookup(
            Record("left-pad", "1.0.0", Candidate(EUpdateType.Patch, "1.0.1")),
            Record("chalk", "4.1.0", Candidate(EUpdateType.Patch, "4.1.2")) with { PackageFile = "samples/a/package.json", LockFiles = [] },
            Record("debug", "4.0.0", Candidate(EUpdateType.Patch, "4.0.1")) with { SkipReason = "unsupported-version" }), config);

        outcome.Groups.Should().BeEmpty();
        outcome.Ignored.Select(e => e.Reason).Should().BeEquivalentTo([EDependencyReportReason.Ignored, EDependencyReportReason.Excluded, EDependencyReportReason.Skipped]);
    }

    [Fact]
    public void A_file_hold_holds_until_the_rule_is_removed()
    {
        var config = Config("dependencies:\n  rules:\n    - match: chalk\n      hold: \"waiting for ESM\"\n");

        var outcome = Evaluate(Lookup(Record("chalk", "4.1.0", Candidate(EUpdateType.Patch, "4.1.2"))), config);

        outcome.Groups.Should().BeEmpty();
        outcome.Held.Should().ContainSingle().Which.Detail.Should().Be("waiting for ESM");
    }

    [Fact]
    public void Above_an_enforced_account_rule_nothing_is_planned_and_nothing_waits_for_approval()
    {
        var config = Config("dependencies:\n  autoMerge: { \"@types/*\": major }\n", AccountRule("@types/*", EDependencyThreshold.Patch));

        var outcome = Evaluate(Lookup(Record("@types/node", "24.19.1",
            Candidate(EUpdateType.Minor, "24.20.0"),
            Candidate(EUpdateType.Patch, "24.19.2"))), config);

        Planned(outcome).Should().Equal(["@types/node: @types/node@24.19.2"]);
        outcome.Held.Should().ContainSingle(e => e.Reason == EDependencyReportReason.AboveAccountRule && e.Target == "24.20.0");
        outcome.AwaitingApproval.Should().BeEmpty("no comment lifts an enforced ceiling (§7.5)");
    }

    [Fact]
    public void AllowMajor_plans_a_major_without_approval_but_never_auto_merges_it()
    {
        var config = Config("dependencies:\n  rules:\n    - match: typescript\n      allowMajor: true\n");

        var outcome = Evaluate(Lookup(Record("typescript", "6.0.3", Candidate(EUpdateType.Major, "7.0.2"))), config);

        var group = outcome.Groups.Should().ContainSingle().Which;
        group.Level.Should().Be(EUpdateLevel.Major);
        group.AutoMerge.Should().BeFalse();
        outcome.AutoMergeable.Should().BeFalse();
    }

    [Fact]
    public void Angular_is_never_planned_without_approval_by_default()
    {
        var outcome = Evaluate(Lookup(Record("@angular/core", "22.2.2", Candidate(EUpdateType.Patch, "22.2.3"))));

        outcome.Groups.Should().BeEmpty();
        outcome.AwaitingApproval.Should().ContainSingle().Which.DepName.Should().Be("@angular/core");
    }

    [Fact]
    public void An_actions_major_waits_for_approval_and_its_minor_auto_merges()
    {
        var checkout = Record("actions/checkout", "v4.1.1",
            Candidate(EUpdateType.Major, "v7.0.1"),
            Candidate(EUpdateType.Minor, "v4.4.0")) with
        {
            Ecosystem = "github-actions",
            Manager = "github-actions",
            Datasource = "github-tags",
            PackageFile = ".github/workflows/ci.yml",
            LockFiles = [],
        };

        var outcome = Evaluate(Lookup(checkout));

        outcome.Groups.Should().ContainSingle().Which.AutoMerge.Should().BeTrue();
        outcome.AwaitingApproval.Should().ContainSingle().Which.Target.Should().Be("v7.0.1");
    }

    [Fact]
    public void A_version_red_elsewhere_in_the_fleet_is_held_and_an_older_one_ships()
    {
        var fleet = new DependencyFleetState
        {
            RedVersions = [new DependencyFleetRed { Ecosystem = "npm", DepName = "chalk", Version = "4.1.2", Repository = "Repositories/github/2" }],
        };

        var outcome = Evaluate(Lookup(Record("chalk", "4.1.0",
            Candidate(EUpdateType.Patch, "4.1.2"),
            Candidate(EUpdateType.Patch, "4.1.1"))), fleet: fleet);

        Planned(outcome).Should().Equal(["chalk: chalk@4.1.1"]);

        var onlyRed = Evaluate(Lookup(Record("chalk", "4.1.0", Candidate(EUpdateType.Patch, "4.1.2"))), fleet: fleet);
        onlyRed.Groups.Should().BeEmpty();
        onlyRed.Held.Should().ContainSingle().Which.Reason.Should().Be(EDependencyReportReason.FleetRed);
    }

    [Fact]
    public void A_confirmed_own_release_skips_the_cooldown()
    {
        var fleet = new DependencyFleetState
        {
            OwnReleases = [new DependencyOwnRelease { Ecosystem = "npm", DepName = "@mintplayer/ng-bootstrap", Version = "22.18.1", ProducerRepository = "Repositories/github/3" }],
        };
        var fresh = Now.AddMinutes(-30).ToString("O");

        var outcome = Evaluate(Lookup(Record("@mintplayer/ng-bootstrap", "22.18.0", Candidate(EUpdateType.Patch, "22.18.1", fresh))), fleet: fleet);

        outcome.Groups.Should().ContainSingle();
        Evaluate(Lookup(Record("@mintplayer/ng-bootstrap", "22.18.0", Candidate(EUpdateType.Patch, "22.18.1", fresh)))).Groups
            .Should().BeEmpty("a look-alike that is not confirmed own gets no bypass (gap E)");
    }

    [Fact]
    public void A_security_fix_skips_the_cooldown_and_a_security_run_carries_only_fixes_and_auto_mergeable_groups()
    {
        var fresh = Now.AddMinutes(-30).ToString("O");
        var lookup = Lookup(
            Record("lodash", "4.17.20", Candidate(EUpdateType.Patch, "4.17.21", fresh)),
            Record("chalk", "4.1.0", Candidate(EUpdateType.Patch, "4.1.2")),
            Record("typescript", "6.0.3", Candidate(EUpdateType.Major, "7.0.2")));
        var config = Config("dependencies:\n  rules:\n    - match: typescript\n      allowMajor: true\n");
        DependencySecurityFix[] fixes = [new("npm", "lodash", "GHSA-xxxx")];

        var outcome = Evaluate(lookup, config, mode: EDependencyRunMode.Security, security: fixes);

        Planned(outcome).Should().Equal(["lodash: lodash@4.17.21", "chalk: chalk@4.1.2"]);
        outcome.Groups[0].Security.Should().BeTrue();
        outcome.Available.Should().ContainSingle(e => e.DepName == "typescript" && e.Reason == EDependencyReportReason.NotInSecurityRun);
    }

    [Fact]
    public void A_record_whose_paths_leave_the_repository_is_never_planned()
    {
        var outcome = Evaluate(Lookup(
            Record("chalk", "4.1.0", Candidate(EUpdateType.Patch, "4.1.2")) with { PackageFile = "../outside/package.json" },
            Record("debug", "4.0.0", Candidate(EUpdateType.Patch, "4.0.1")) with { LockFiles = ["/etc/package-lock.json"] }));

        outcome.Groups.Should().BeEmpty();
        outcome.Held.Should().HaveCount(2);
        outcome.Held.Should().OnlyContain(e => e.Reason == EDependencyReportReason.NeedsHuman);
    }

    [Theory]
    [InlineData(EUpdateType.Replacement)]
    [InlineData(EUpdateType.Rollback)]
    public void A_replacement_or_rollback_always_goes_to_a_human(EUpdateType type)
    {
        var outcome = Evaluate(Lookup(Record("request", "2.88.0", Candidate(type, "2.88.2"))), Config("dependencies:\n  autoMerge: { default: major }\n"));

        outcome.Groups.Should().BeEmpty();
        outcome.Held.Should().ContainSingle().Which.Reason.Should().Be(EDependencyReportReason.NeedsHuman);
    }

    [Fact]
    public void A_lockfile_only_update_is_planned_with_its_value_unchanged_and_without_a_lockfile_it_is_nothing()
    {
        var withLock = Record("@types/node", "^24.19.1", Candidate(EUpdateType.Patch, "24.19.2", value: "^24.19.1")) with { CurrentVersion = "24.19.1" };

        var planned = Evaluate(Lookup(withLock));
        var item = planned.Groups.Should().ContainSingle().Which.Items.Single().ToPlanItem();
        item.CurrentValue.Should().Be("^24.19.1");
        item.NewValue.Should().Be("^24.19.1");
        item.NewVersion.Should().Be("24.19.2");

        var withoutLock = Evaluate(Lookup(withLock with { LockFiles = [] }));
        withoutLock.Groups.Should().BeEmpty();
        withoutLock.Held.Should().BeEmpty();
        withoutLock.Available.Should().BeEmpty();
    }

    [Fact]
    public void An_npm_integrity_hash_is_never_planned_as_a_digest()
    {
        var record = Record("chalk", "4.1.0", Candidate(EUpdateType.Patch, "4.1.2") with { Digest = "sha512-abc" });

        var item = Evaluate(Lookup(record)).Groups.Single().Items.Single().ToPlanItem();

        item.NewDigest.Should().BeNull("npm's newDigest is an integrity hash, not a pin (S12)");
        item.CurrentDigest.Should().BeNull();
    }

    [Fact]
    public void Groups_are_ordered_security_then_by_level_then_by_name_and_the_plan_validates()
    {
        var config = Config("dependencies:\n  autoMerge: { default: minor }\n");
        var outcome = Evaluate(Lookup(
            Record("zod", "3.22.4", Candidate(EUpdateType.Minor, "3.23.0")),
            Record("chalk", "4.1.0", Candidate(EUpdateType.Patch, "4.1.2")),
            Record("Serilog", "3.0.1", Candidate(EUpdateType.Minor, "3.1.0")) with { Ecosystem = "nuget", Datasource = "nuget", Manager = "nuget", PackageFile = "src/App/App.csproj", LockFiles = [] },
            Record("lodash", "4.17.20", Candidate(EUpdateType.Minor, "4.18.1"))),
            config, security: [new DependencySecurityFix("npm", "lodash")]);

        outcome.Groups.Select(g => g.Group).Should().Equal(["lodash", "chalk", "Serilog", "zod"]);

        var json = DependencyContractReader.WritePlan(outcome.ToPlan("plan-1", Sha, new PlanOptions { ConsistencyCheck = true }));
        DependencyContractReader.ReadPlan(json).Document!.Groups.Select(g => g.Group).Should().Equal(["lodash", "chalk", "Serilog", "zod"]);
    }

    [Fact]
    public void The_engine_never_changes_the_state_it_was_given()
    {
        var state = new DependencyUpdateState { Repository = "Repositories/github/1" };

        var outcome = Evaluate(Lookup(Record("chalk", "4.1.0", Candidate(EUpdateType.Patch, "4.1.2"))), state: state);

        state.FirstSeen.Should().BeEmpty();
        state.Slots.Should().Be(0);
        outcome.NextState.FirstSeen.Should().ContainSingle();
        outcome.NextState.Slots.Should().Be(1);
    }

    [Fact]
    public void The_file_lengthens_the_cooldown_but_never_shortens_it()
    {
        var lookup = Lookup(Record("chalk", "4.1.0", Candidate(EUpdateType.Patch, "4.1.2", Now.AddDays(-5).ToString("O"))));

        Evaluate(lookup, Config("dependencies:\n  cooldown: 7d\n")).Groups.Should().BeEmpty();
        Evaluate(lookup, Config("dependencies:\n  cooldown: 0\n")).Groups.Should().ContainSingle();
        Evaluate(Lookup(Record("chalk", "4.1.0", Candidate(EUpdateType.Patch, "4.1.2", Now.AddDays(-1).ToString("O")))), Config("dependencies:\n  cooldown: 0\n"))
            .Groups.Should().BeEmpty("the owner's three days still stand");
    }
}
