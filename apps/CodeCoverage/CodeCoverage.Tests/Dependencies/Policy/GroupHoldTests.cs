using CodeCoverage.Dependencies;
using CodeCoverage.Dependencies.Policy;
using CodeCoverage.Entities;
using CodeCoverage.Git.Dependencies;
using CodeCoverage.Tests.CommitGraph;
using Xunit;
using static CodeCoverage.Tests.Dependencies.Policy.DependencyTestKit;

namespace CodeCoverage.Tests.Dependencies.Policy;

/// <summary>
/// Holds across slots, persisted in RavenDB and driven by an injectable clock (PRD §13.11.1 "R2 blockers"): the R2 blocker
/// hold from the REAL S13 apply result, the four-slot retry, Q11's human hold, comment holds and Q15's fleet-red versions.
/// Every slot loads the state in a fresh session, evaluates, and stores the next state, as the saga (N5) will.
/// </summary>
public class GroupHoldTests : CoverageRavenTest
{
    private const string RepositoryId = "Repositories/github/1";
    private const string AccountId = "Accounts/github/7";

    private static readonly DependencyPolicyConfig AnyLevel = Config("dependencies:\n  autoMerge: { default: major }\n");

    /// <summary>The S13 npm sample as a lookup: the three planned groups, and react (the blocker) with nothing newer yet.</summary>
    private static LookupDocument Npm(params LookupCandidate[] react) => Lookup(
        Record("express", "^4.18.2", Candidate(EUpdateType.Minor, "4.19.2", value: "^4.19.2")) with { CurrentVersion = "4.18.2" },
        Record("react-dom", "17.0.2", Candidate(EUpdateType.Major, "18.2.0")),
        Record("chalk", "4.1.0", Candidate(EUpdateType.Patch, "4.1.2")),
        Record("react", "17.0.2", react));

    private async Task<DependencyPolicyOutcome> SlotAsync(Raven.Client.Documents.IDocumentStore store, LookupDocument lookup, MutableTime clock,
        bool scheduled = true)
    {
        using var session = store.OpenAsyncSession();
        var states = new DependencyStateStore(session);
        var state = await states.LoadAsync(RepositoryId);
        var fleet = await states.LoadFleetAsync(AccountId);
        var outcome = DependencyPolicyEngine.Evaluate(new DependencyPolicyInput
        {
            Lookup = lookup,
            Config = AnyLevel,
            State = state,
            Fleet = fleet,
            Now = clock.GetUtcNow(),
            IsScheduledSlot = scheduled,
        });
        await states.SaveAsync(RepositoryId, outcome.NextState);
        return outcome;
    }

    private static async Task MutateAsync(Raven.Client.Documents.IDocumentStore store, Action<DependencyUpdateState> change)
    {
        using var session = store.OpenAsyncSession();
        var states = new DependencyStateStore(session);
        var state = await states.LoadAsync(RepositoryId);
        change(state);
        await states.SaveAsync(RepositoryId, state);
    }

    [Fact]
    public async Task A_group_the_resolver_refused_is_held_until_its_blocker_shows_a_new_version_and_the_others_still_ship()
    {
        using var store = GetDocumentStore();
        var clock = new MutableTime(Now);
        var plan = DependencyContractReader.ReadPlan(ContractFixture("valid", "plan.npm-three-groups.json")).Document!;
        var result = DependencyContractReader.ReadApplyResult(ContractFixture("valid", "result.npm-three-groups.json")).Document!;

        (await SlotAsync(store, Npm(), clock)).Groups.Select(g => g.Group).Should().BeEquivalentTo(["express", "react-dom", "chalk"]);
        await MutateAsync(store, state => DependencyHolds.RecordApplyResult(state, plan, result, Npm(), clock.GetUtcNow())
            .Should().ContainSingle().Which.Kind.Should().Be(EDependencyHoldKind.Blocker));

        clock.Now = clock.Now.AddDays(7);
        var held = await SlotAsync(store, Npm(), clock);
        held.Groups.Select(g => g.Group).Should().BeEquivalentTo(["express", "chalk"], because: "R2: the other groups still ship");
        held.Held.Should().ContainSingle(e => e.DepName == "react-dom").Which.Detail.Should().Contain("react");

        clock.Now = clock.Now.AddDays(7);
        var lifted = await SlotAsync(store, Npm(Candidate(EUpdateType.Major, "18.2.0")), clock);
        lifted.LiftedHolds.Should().ContainSingle().Which.Group.Should().Be("react-dom");
        lifted.Groups.Select(g => g.Group).Should().Contain("react-dom");
    }

    [Fact]
    public async Task An_unattributed_failure_retries_on_any_change_and_always_after_four_slots()
    {
        using var store = GetDocumentStore();
        var clock = new MutableTime(Now);
        var lookup = Npm();
        var plan = new DependencyPlan { PlanId = "p", ExpectedSha = Sha, Groups = [new PlanGroup { Group = "chalk", Items = [new PlanItem { DepName = "chalk", Datasource = "npm", Manager = "npm", PackageFile = "package.json", CurrentValue = "4.1.0", NewValue = "4.1.2", NewVersion = "4.1.2", UpdateType = "patch" }] }] };
        var result = new ApplyResult { PlanId = "p", ExpectedSha = Sha, Groups = [new ApplyGroupResult { Group = "chalk", Status = EApplyGroupStatus.Failed, Message = "tool missing" }] };

        await SlotAsync(store, lookup, clock);
        await MutateAsync(store, state => DependencyHolds.RecordApplyResult(state, plan, result, lookup, clock.GetUtcNow())
            .Should().ContainSingle().Which.Kind.Should().Be(EDependencyHoldKind.Unattributed));

        (await SlotAsync(store, lookup, clock, scheduled: false)).Groups.Should().NotContain(g => g.Group == "chalk", "an out-of-slot run does not advance the clock");
        for (var slot = 1; slot <= DependencyHolds.RetrySlots; slot++)
        {
            clock.Now = clock.Now.AddDays(7);
            (await SlotAsync(store, lookup, clock)).Groups.Should().NotContain(g => g.Group == "chalk", $"held through slot {slot}");
        }

        clock.Now = clock.Now.AddDays(7);
        (await SlotAsync(store, lookup, clock)).Groups.Should().Contain(g => g.Group == "chalk", "R2: always retried after four slots");
    }

    [Fact]
    public async Task An_unattributed_hold_lifts_as_soon_as_anything_in_the_lookup_changes()
    {
        using var store = GetDocumentStore();
        var clock = new MutableTime(Now);
        var lookup = Npm();
        var result = new ApplyResult { PlanId = "p", ExpectedSha = Sha, Groups = [new ApplyGroupResult { Group = "chalk", Status = EApplyGroupStatus.Unresolvable }] };
        var plan = new DependencyPlan { PlanId = "p", ExpectedSha = Sha, Groups = [new PlanGroup { Group = "chalk" }] };
        await MutateAsync(store, state => DependencyHolds.RecordApplyResult(state, plan, result, lookup, clock.GetUtcNow()));

        (await SlotAsync(store, lookup, clock)).Groups.Should().NotContain(g => g.Group == "chalk");
        (await SlotAsync(store, Npm(Candidate(EUpdateType.Major, "18.3.0")), clock)).Groups.Should().Contain(g => g.Group == "chalk");
    }

    [Fact]
    public async Task A_humans_value_holds_the_dependency_until_a_version_newer_than_the_proposal_appears()
    {
        using var store = GetDocumentStore();
        var clock = new MutableTime(Now);
        await MutateAsync(store, state => DependencyHolds.AddHumanHold(state, "chalk", "4.1.1", "4.1.2", "alice", "0123456789abcdef", Npm(), clock.GetUtcNow()));

        var held = await SlotAsync(store, Npm(), clock);
        held.Groups.Should().NotContain(g => g.Group == "chalk");
        held.Held.Should().ContainSingle(e => e.DepName == "chalk").Which.Detail.Should().Be("chalk held at 4.1.1 (set by @alice in 0123456789ab)");

        var newer = Lookup(Record("chalk", "4.1.0", Candidate(EUpdateType.Patch, "4.1.3")));
        var lifted = await SlotAsync(store, newer, clock);
        lifted.LiftedHolds.Should().ContainSingle().Which.Kind.Should().Be(EDependencyHoldKind.Human);
        Planned(lifted).Should().Equal(["chalk: chalk@4.1.3"]);
    }

    [Fact]
    public async Task A_comment_hold_stands_until_unhold()
    {
        using var store = GetDocumentStore();
        var clock = new MutableTime(Now);
        await MutateAsync(store, state => DependencyHolds.AddCommandHold(state, "chalk", isGroup: true, "alice", clock.GetUtcNow()));

        for (var slot = 0; slot < 6; slot++)
        {
            clock.Now = clock.Now.AddDays(7);
            (await SlotAsync(store, Npm(Candidate(EUpdateType.Major, $"18.{slot}.0")), clock)).Groups.Should().NotContain(g => g.Group == "chalk");
        }

        await MutateAsync(store, state => DependencyHolds.RemoveCommandHold(state, "chalk").Should().BeTrue());
        (await SlotAsync(store, Npm(), clock)).Groups.Should().Contain(g => g.Group == "chalk");
    }

    [Fact]
    public async Task A_version_recorded_red_in_another_repository_is_held_here()
    {
        using var store = GetDocumentStore();
        var clock = new MutableTime(Now);
        using (var session = store.OpenAsyncSession())
            await new DependencyStateStore(session).RecordFleetRedAsync(AccountId, new DependencyFleetRed
            {
                Ecosystem = "npm",
                DepName = "chalk",
                Version = "4.1.2",
                Repository = "Repositories/github/2",
                AtUtc = clock.GetUtcNow().UtcDateTime,
            });

        var outcome = await SlotAsync(store, Npm(), clock);

        outcome.Groups.Should().NotContain(g => g.Group == "chalk");
        outcome.Held.Should().ContainSingle(e => e.DepName == "chalk").Which.Reason.Should().Be(EDependencyReportReason.FleetRed);
    }

    [Fact]
    public async Task The_state_documents_live_beside_their_repository_and_account()
    {
        using var store = GetDocumentStore();
        await SlotAsync(store, Npm(), new MutableTime(Now));

        using var session = store.OpenAsyncSession();
        var state = await session.LoadAsync<DependencyUpdateState>("DependencyUpdateStates/github/1");
        state.Should().NotBeNull();
        state!.Repository.Should().Be(RepositoryId);
        state.FirstSeen.Should().HaveCount(3);
        (await session.LoadAsync<DependencyFleetState>("DependencyFleetStates/github/7")).Should().NotBeNull();
    }
}
