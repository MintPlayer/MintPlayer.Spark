using CodeCoverage.Dependencies;
using CodeCoverage.Dependencies.Policy;
using CodeCoverage.Entities;
using CodeCoverage.Git.Dependencies;
using Xunit;
using static CodeCoverage.Tests.Dependencies.Policy.DependencyTestKit;

namespace CodeCoverage.Tests.Dependencies.Policy;

/// <summary>
/// #653: a blocker hold whose blocker waits in its own group. The shape is SpaServices' (2026-11-13): the
/// <c>MintPlayer.SourceGenerators</c> group was applied alone, NuGet refused it with NU1605 on
/// <c>MintPlayer.SourceGenerators.Attributes</c> (SP1 in <c>docs/dependency_blocker_hold_PRD.md</c>), and Attributes' own
/// 10.20.1 → 13.1.0 waited for an approval in a separate group.
/// </summary>
public class BlockerHoldTests
{
    private const string Generators = "MintPlayer.SourceGenerators";
    private const string Attributes = "MintPlayer.SourceGenerators.Attributes";

    private static LookupRecord NuGet(string depName, string current, params LookupCandidate[] candidates)
        => Record(depName, current, candidates) with
        {
            Ecosystem = "nuget",
            Manager = "nuget",
            Datasource = "nuget",
            PackageFile = "MintPlayer.AspNetCore.SpaServices.Xsrf/MintPlayer.AspNetCore.SpaServices.Xsrf.csproj",
            LockFiles = [],
            DepType = "nuget",
        };

    private static LookupDocument SpaServices(string attributesCurrent = "10.20.1", bool attributesCandidate = true) => Lookup(
        NuGet(Generators, "10.22.0", Candidate(EUpdateType.Major, "13.1.0")),
        attributesCandidate
            ? NuGet(Attributes, attributesCurrent, Candidate(EUpdateType.Major, "13.1.0"))
            : NuGet(Attributes, attributesCurrent));

    /// <summary>The REAL apply result's shape: the Generators group alone, unresolvable, NU1605 attributed to Attributes.</summary>
    private static DependencyUpdateState HeldOnAttributes(LookupDocument lookup)
    {
        var state = new DependencyUpdateState { Repository = "Repositories/github/204431316" };
        var plan = new DependencyPlan
        {
            PlanId = "p",
            ExpectedSha = Sha,
            Groups = [new PlanGroup
            {
                Group = Generators,
                Items = [new PlanItem
                {
                    DepName = Generators, Datasource = "nuget", Manager = "nuget",
                    PackageFile = "MintPlayer.AspNetCore.SpaServices.Xsrf/MintPlayer.AspNetCore.SpaServices.Xsrf.csproj",
                    CurrentValue = "10.22.0", NewValue = "13.1.0", NewVersion = "13.1.0", UpdateType = "major",
                }],
            }],
        };
        var result = new ApplyResult
        {
            PlanId = "p",
            ExpectedSha = Sha,
            Groups = [new ApplyGroupResult
            {
                Group = Generators,
                Status = EApplyGroupStatus.Unresolvable,
                Blockers = [new ApplyBlocker
                {
                    DepName = Attributes,
                    Version = "13.1.0",
                    Code = "NU1605",
                    Message = "Warning As Error: Detected package downgrade: MintPlayer.SourceGenerators.Attributes from 13.1.0 to 10.20.1.",
                }],
            }],
        };
        DependencyHolds.RecordApplyResult(state, plan, result, lookup, Now).Should().ContainSingle().Which.Kind.Should().Be(EDependencyHoldKind.Blocker);
        return state;
    }

    [Fact]
    public void Issue653_D1_a_blocker_hold_lifts_when_its_blockers_current_version_moves_to_one_it_had_already_seen()
    {
        var state = HeldOnAttributes(SpaServices());

        // Attributes has nothing to offer and still sits at 10.20.1: the block stands.
        var standing = Evaluate(SpaServices(attributesCandidate: false), state: state);
        standing.LiftedHolds.Should().BeEmpty();
        standing.NextState.Holds.Should().ContainSingle(h => h.Group == Generators);

        // A person moved Attributes to 13.1.0, a version the lookup already offered when the hold was taken.
        var moved = Evaluate(SpaServices(attributesCurrent: "13.1.0", attributesCandidate: false), Config("dependencies:\n  autoMerge: { default: major }\n"), standing.NextState);
        moved.LiftedHolds.Should().ContainSingle().Which.Group.Should().Be(Generators);
        Planned(moved).Should().Equal([$"{Generators}: {Generators}@13.1.0"]);
    }

    [Fact]
    public void Issue653_D2_a_held_group_joins_its_blockers_group_and_one_approval_moves_both()
    {
        var state = HeldOnAttributes(SpaServices());

        var outcome = Evaluate(SpaServices(), state: state);
        outcome.Groups.Should().BeEmpty("both are majors over the default threshold");
        outcome.Held.Should().BeEmpty("the held group no longer stands alone");
        outcome.AwaitingApproval.Select(e => $"{e.Group}: {e.DepName}").Should().BeEquivalentTo([$"{Attributes}: {Generators}", $"{Attributes}: {Attributes}"]);
        outcome.NextState.Holds.Should().ContainSingle(h => h.Group == Generators, "the hold is what keeps the two together");

        DependencyHolds.Approve(outcome.NextState, outcome, Attributes, string.Empty, "alice", null, Now).Should().NotBeNull();
        var approved = Evaluate(SpaServices(), state: outcome.NextState);
        Planned(approved).Should().Equal([$"{Attributes}: {Attributes}@13.1.0, {Generators}@13.1.0"]);
    }

    [Fact]
    public void Issue653_D2_a_merged_hold_does_not_run_out_its_retry_slots_and_split_the_group_again()
    {
        var next = HeldOnAttributes(SpaServices());
        for (var slot = 0; slot <= DependencyHolds.RetrySlots + 1; slot++)
        {
            var outcome = Evaluate(SpaServices(), state: next, now: Now.AddDays(7 * slot));
            outcome.AwaitingApproval.Should().OnlyContain(e => e.Group == Attributes, $"still one group in slot {slot}");
            next = outcome.NextState;
        }
    }

    [Fact]
    public void Issue653_D2_a_hold_written_before_653_still_merges_and_does_not_lift_on_the_missing_current()
    {
        // Production holds have no recorded current: no lift for that reason alone (SP3), and the merge applies.
        var state = new DependencyUpdateState
        {
            Repository = "Repositories/github/204431316",
            Holds = [new DependencyHold
            {
                Id = $"blocker:{Generators}",
                Kind = EDependencyHoldKind.Blocker,
                Group = Generators,
                DepNames = [Generators],
                Reason = $"the resolver refused it: blocked by {Attributes} (R2)",
                Blockers = [new DependencyBlockerSeen { DepName = Attributes, SeenVersions = ["10.20.1", "13.1.0"] }],
                CreatedAtUtc = Now.UtcDateTime,
            }],
        };

        var outcome = Evaluate(SpaServices(), state: state);
        outcome.LiftedHolds.Should().BeEmpty();
        outcome.AwaitingApproval.Should().OnlyContain(e => e.Group == Attributes).And.HaveCount(2);
    }
}
