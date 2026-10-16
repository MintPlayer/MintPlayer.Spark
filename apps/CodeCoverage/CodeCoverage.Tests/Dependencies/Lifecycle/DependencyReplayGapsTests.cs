using CodeCoverage.Dependencies;
using CodeCoverage.Dependencies.Lifecycle;
using CodeCoverage.Dependencies.Policy;
using CodeCoverage.Entities;
using CodeCoverage.Git.Dependencies;
using CodeCoverage.Tests.Dependencies.Policy;
using Xunit;
using static CodeCoverage.Tests.Dependencies.Policy.DependencyTestKit;

namespace CodeCoverage.Tests.Dependencies.Lifecycle;

/// <summary>
/// D-N1: a human fix on the rolling branch keeps the versions it was written against. The previous build planned chalk
/// 4.1.2; a person then pushed a code fix; now chalk 4.1.3 is out.
/// </summary>
public class DependencyFixPinsTests
{
    private static readonly DependencyPlan Previous = Evaluate(Lookup(Record("chalk", "4.1.0", Candidate(EUpdateType.Patch, "4.1.2")))).ToPlan("1-1-1", Sha);

    private static LookupDocument Later(string current = "4.1.0", params LookupCandidate[] candidates)
        => Lookup(Record("chalk", current, candidates.Length > 0 ? candidates : [Candidate(EUpdateType.Patch, "4.1.3")]));

    [Fact]
    public void A_fix_is_any_change_beyond_the_previous_plans_manifests_and_lockfiles()
    {
        DependencyFixPins.HumanFixPresent(Previous, ["package.json", "package-lock.json"]).Should().BeFalse("a manifest value set by hand is Q11's human hold");
        DependencyFixPins.HumanFixPresent(Previous, [".mintplayer/dependency-plan.json"]).Should().BeFalse();
        DependencyFixPins.HumanFixPresent(Previous, ["package.json", "src/app.ts"]).Should().BeTrue();
        DependencyFixPins.HumanFixPresent(Previous, null).Should().BeTrue("an unreadable compare is treated as a fix");
    }

    [Fact]
    public void A_group_that_moved_on_is_planned_again_at_the_version_the_fix_was_made_against()
    {
        var lookup = Later();
        var outcome = Evaluate(lookup);
        Planned(outcome).Should().Equal(["chalk: chalk@4.1.3"]);

        var (pinned, pins) = DependencyFixPins.Apply(outcome, Previous, lookup, new DependencyUpdateState());

        Planned(pinned).Should().Equal(["chalk: chalk@4.1.2"]);
        pinned.Groups.Single().AutoMerge.Should().BeFalse("a person changed code on this pull request, so a person merges it");
        var pin = pins.Should().ContainSingle().Which;
        pin.Version.Should().Be("4.1.2");
        pin.Wanted.Should().Be("4.1.3");
        DependencyContract.Serialize(pinned.ToPlan("1-2-1", Sha).Groups.Single().Items.Single())
            .Should().Be(DependencyContract.Serialize(Previous.Groups.Single().Items.Single()), "the pinned item is the previous plan's, token for token");
    }

    [Fact]
    public void A_group_the_policy_no_longer_plans_is_still_kept()
    {
        var lookup = Later(candidates: Candidate(EUpdateType.Patch, "4.1.3", released: Now.AddHours(-1).ToString("O")));
        var outcome = Evaluate(lookup);
        outcome.Groups.Should().BeEmpty("4.1.3 is still cooling down");

        var (pinned, pins) = DependencyFixPins.Apply(outcome, Previous, lookup, new DependencyUpdateState());

        Planned(pinned).Should().Equal(["chalk: chalk@4.1.2"]);
        pins.Single().Wanted.Should().BeNull();
    }

    [Fact]
    public void Nothing_moves_when_the_plan_is_the_same()
    {
        var lookup = Lookup(Record("chalk", "4.1.0", Candidate(EUpdateType.Patch, "4.1.2")));
        var outcome = Evaluate(lookup);

        var (pinned, pins) = DependencyFixPins.Apply(outcome, Previous, lookup, new DependencyUpdateState());

        pins.Should().BeEmpty();
        pinned.Should().BeSameAs(outcome);
    }

    [Fact]
    public void A_persons_hold_wins_over_a_pin()
    {
        var lookup = Later();
        var state = new DependencyUpdateState
        {
            Holds = [new DependencyHold { Id = "command:chalk", Kind = EDependencyHoldKind.Command, DepNames = ["chalk"], By = "octocat" }],
        };

        DependencyFixPins.Apply(Evaluate(lookup), Previous, lookup, state).Pins.Should().BeEmpty();
    }

    [Fact]
    public void A_dependency_that_moved_on_the_default_branch_is_not_pinned()
    {
        var lookup = Later(current: "4.1.2");

        DependencyFixPins.Apply(Evaluate(lookup), Previous, lookup, new DependencyUpdateState()).Pins.Should().BeEmpty(
            "the base no longer holds the value the pin would edit");
    }
}

/// <summary>D-N2: the relock plan regenerates the lockfiles a replay took from the bot's side.</summary>
public class DependencyRelockPlanTests
{
    private static DependencyPlan Plan() => Evaluate(Lookup(
        Record("chalk", "4.1.0", Candidate(EUpdateType.Patch, "4.1.2")),
        Record("lodash", "4.17.20", Candidate(EUpdateType.Patch, "4.17.21")))).ToPlan("7-3-1", Sha);

    private const string Replayed = "1111111111111111111111111111111111111111";

    [Fact]
    public void Each_lockfile_gets_its_owners_as_lockfile_only_updates_at_the_planned_versions()
    {
        var relock = DependencyRelockPlan.Build(Plan(), ["package-lock.json"], new HashSet<(string, string)>(), "7-3-1-relock", Replayed)!;

        relock.ExpectedSha.Should().Be(Replayed);
        var group = relock.Groups.Should().ContainSingle().Which;
        group.Group.Should().Be("relock:package-lock.json");
        group.Items.Select(i => $"{i.DepName} {i.CurrentValue}->{i.NewValue} {i.NewVersion}").Should().BeEquivalentTo(["chalk 4.1.2->4.1.2 4.1.2", "lodash 4.17.21->4.17.21 4.17.21"]);
        DependencyContractReader.WritePlan(relock).Should().Contain("\"relock:package-lock.json\"", "the relock plan is a schema-valid plan");
    }

    [Fact]
    public void A_dependency_changed_by_hand_is_not_pinned_back()
    {
        var relock = DependencyRelockPlan.Build(Plan(), ["package-lock.json"], new HashSet<(string, string)> { ("chalk", "package.json") }, "7-3-1-relock", Replayed)!;

        relock.Groups.Single().Items.Select(i => i.DepName).Should().Equal(["lodash"]);
    }

    [Fact]
    public void A_lockfile_whose_every_owner_was_changed_by_hand_cannot_be_regenerated()
        => DependencyRelockPlan.Build(Plan(), ["package-lock.json"], new HashSet<(string, string)> { ("chalk", "package.json"), ("lodash", "package.json") },
            "7-3-1-relock", Replayed).Should().BeNull();

    [Fact]
    public void The_relock_commit_carries_the_trailer_a_later_replay_skips()
    {
        var relock = DependencyRelockPlan.Build(Plan(), ["package-lock.json"], new HashSet<(string, string)>(), "7-3-1-relock", Replayed)!;

        var message = DependencyUpdateEngine.RelockMessage(relock);

        RollingBranchReplay.IsRelockCommit(message).Should().BeTrue();
        message.Should().Contain("- package-lock.json");
    }
}

/// <summary>D-N3: build vs test, from the failing checks' names only.</summary>
public class DependencyCheckFailureTests
{
    [Theory]
    [InlineData(ECheckFailureKind.Build, "build (ubuntu-latest)", "lint")]
    [InlineData(ECheckFailureKind.Test, "Unit tests", "e2e")]
    [InlineData(ECheckFailureKind.Both, "build-and-test")]
    [InlineData(ECheckFailureKind.Both, "build", "test")]
    [InlineData(ECheckFailureKind.Unknown, "ci", "CodeQL")]
    public void The_failing_checks_names_say_which_kind(ECheckFailureKind expected, params string[] checks)
        => DependencyCheckFailure.Classify(checks).Should().Be(expected);

    [Fact]
    public void An_unknown_kind_adds_nothing_to_the_comment()
        => DependencyCheckFailure.Describe(ECheckFailureKind.Unknown).Should().BeEmpty();
}
