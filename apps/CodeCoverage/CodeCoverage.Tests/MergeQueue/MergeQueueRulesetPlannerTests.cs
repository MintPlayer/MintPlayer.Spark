using System.Text.Json.Nodes;
using CodeCoverage.Forge;
using Xunit;
using static CodeCoverage.Forge.MergeQueueRulesetPlanner;

namespace CodeCoverage.Tests.MergeQueue;

/// <summary>
/// #554 e1: the pure plan for merge-queue.md §3's two rulesets and the classic protection they replace (move, then delete;
/// owner, 2026-11-06).
/// </summary>
public class MergeQueueRulesetPlannerTests
{
    private const long AppId = 1_234_567;

    private static readonly ForgeRulesetActor App = new(AppId, "Integration", "exempt");
    private static readonly ForgeRulesetActor AdminsByPullRequest = new(RepositoryAdminRoleId, "RepositoryRole", "pull_request");

    /// <summary>ng-bootstrap on 2026-10-30 (#554's table): Lock branch and one required review, no active ruleset.</summary>
    private static ForgeClassicProtection NgBootstrapClassic(params string[] checks) => new(
        LockBranch: true, RequiredApprovals: 1, DismissStaleReviews: false, RequireLastPushApproval: false,
        RequireCodeOwnerReviews: false, RequireConversationResolution: false, RequiredChecks: checks,
        RequiredLinearHistory: false, RequiredSignatures: false, HasPushRestrictions: false);

    /// <summary>As GitHub stores it: the update rule without parameters (read live on 2026-11-06).</summary>
    private static ForgeRulesetDefinition Ruleset1(params ForgeRulesetActor[] actors) => new(
        RestrictUpdatesName, "branch", "active", ["~DEFAULT_BRANCH"], [],
        [new("update"), new("deletion"), new("non_fast_forward")],
        actors.Length == 0 ? [App] : actors);

    [Fact]
    public void A_ruleset_created_by_the_plan_reads_back_as_configured()
    {
        // Regression: the plan sent update_allows_fetch_and_merge=false, GitHub stored {"type":"update"}, and the next
        // run reported "rule changed: update" on the ruleset it had just created.
        var created = PlanFor([], classic: null).Steps.OfType<CreateRulesetStep>().Select(s => s.Definition).ToList();
        var stored = created.Select((d, i) => new ForgeExistingRuleset(i + 1, d)).ToList();

        PlanFor(stored, classic: null).IsConfigured.Should().BeTrue();
        ((JsonNode?)created[0].Rules.Single(r => r.Type == "update").Parameters).Should().BeNull();
    }

    [Fact]
    public void An_update_rule_that_allows_fetch_and_merge_is_changed()
    {
        var allowing = Ruleset1() with
        {
            Rules = [new("update", new JsonObject { ["update_allows_fetch_and_merge"] = true }), new("deletion"), new("non_fast_forward")],
        };

        var plan = PlanFor([new(1, allowing), new(2, Ruleset2(0, ["build"]))], classic: null);

        plan.Steps.Should().ContainSingle().Which.Should().BeOfType<UpdateRulesetStep>()
            .Which.Changes.Should().Equal(["rule changed: update"]);
    }

    private static ForgeRulesetDefinition Ruleset2(int approvals, string[] checks, params ForgeRulesetActor[] actors)
    {
        List<ForgeRulesetRule> rules =
        [
            new("pull_request", new JsonObject
            {
                ["required_approving_review_count"] = approvals,
                ["dismiss_stale_reviews_on_push"] = false,
                ["require_last_push_approval"] = false,
                ["require_code_owner_review"] = false,
                ["required_review_thread_resolution"] = false,
            }),
        ];
        if (checks.Length > 0)
            rules.Add(new("required_status_checks", new JsonObject
            {
                ["strict_required_status_checks_policy"] = true,
                ["required_status_checks"] = new JsonArray([.. checks.Select(c => (JsonNode)new JsonObject { ["context"] = c })]),
            }));
        return new(ReviewsAndChecksName, "branch", "active", ["~DEFAULT_BRANCH"], [], rules, actors);
    }

    private static MergeQueueRulesetPlan PlanFor(IReadOnlyList<ForgeExistingRuleset> rulesets, ForgeClassicProtection? classic,
        int approvals = 0, string[]? checks = null, bool? admins = null)
        => Plan(new ForgeRulesetState(AppId, rulesets, classic), new MergeQueueRulesetChoices(approvals, checks ?? ["build"], admins));

    [Fact]
    public void A_locked_branch_with_a_review_gets_both_rulesets_then_loses_its_classic_protection()
    {
        var plan = PlanFor([], NgBootstrapClassic("build", "test"), approvals: 0, checks: ["build", "test"]);

        plan.Blockers.Should().BeEmpty();
        plan.Steps.Should().HaveCount(3);
        var first = plan.Steps[0].Should().BeOfType<CreateRulesetStep>().Which.Definition;
        first.Name.Should().Be(RestrictUpdatesName);
        first.BypassActors.Should().Equal([App], "the App only, exempt (D16), admins off by default");
        first.Rules.Select(r => r.Type).Should().Equal(["update", "deletion", "non_fast_forward"]);

        var second = plan.Steps[1].Should().BeOfType<CreateRulesetStep>().Which.Definition;
        second.Name.Should().Be(ReviewsAndChecksName);
        second.BypassActors.Should().BeEmpty("the App must not bypass reviews or checks (O22)");
        var pullRequest = second.Rules.Single(r => r.Type == "pull_request").Parameters!;
        pullRequest["required_approving_review_count"]!.GetValue<int>().Should().Be(0);
        var statusChecks = second.Rules.Single(r => r.Type == "required_status_checks").Parameters!;
        statusChecks["strict_required_status_checks_policy"]!.GetValue<bool>().Should().BeTrue();
        statusChecks["required_status_checks"]!.AsArray().Select(c => c!["context"]!.GetValue<string>()).Should().Equal(["build", "test"]);

        var notes = plan.Steps[2].Should().BeOfType<DeleteClassicProtectionStep>().Which.Notes;
        notes.Should().Contain(n => n.StartsWith("Lock branch: replaced by"));
        notes.Should().Contain("Required approvals: 1 → 0, in \"merge-queue: reviews and checks\".");
        notes.Should().Contain("Required checks moved to \"merge-queue: reviews and checks\": build, test.");
        plan.CanApply.Should().BeTrue();
    }

    [Fact]
    public void Rulesets_that_already_say_it_and_no_classic_protection_are_already_configured()
    {
        var plan = PlanFor([new(1, Ruleset1()), new(2, Ruleset2(0, ["build"]))], classic: null);

        plan.IsConfigured.Should().BeTrue();
        plan.CanApply.Should().BeFalse();
    }

    [Fact]
    public void The_apps_always_bypass_from_the_ui_is_repaired_to_exempt()
    {
        var plan = PlanFor([new(1, Ruleset1(App with { BypassMode = "always" })), new(2, Ruleset2(0, ["build"]))], classic: null);

        var update = plan.Steps.Should().ContainSingle().Which.Should().BeOfType<UpdateRulesetStep>().Which;
        update.Id.Should().Be(1);
        update.Changes.Should().Equal([$"bypass changed: App {AppId} (always) → exempt"]);
        update.Definition.BypassActors.Should().Equal([App]);
    }

    /// <summary>
    /// Regression (production, 2026-11-06): on a repository with the development App exempt, production's plan swapped it
    /// for its own App, which locked the development App out. Another environment's App stays; one exempt any other way, or
    /// a person, is still removed from ruleset 1.
    /// </summary>
    [Fact]
    public void Another_apps_exemption_on_ruleset_1_is_kept()
    {
        var otherApp = new ForgeRulesetActor(4_567_511, "Integration", "exempt");
        var ruleset2 = new ForgeExistingRuleset(2, Ruleset2(0, ["build"]));

        var added = PlanFor([new(1, Ruleset1(otherApp)), ruleset2], classic: null);
        var update = added.Steps.Should().ContainSingle().Which.Should().BeOfType<UpdateRulesetStep>().Which;
        update.Changes.Should().Equal([$"bypass added: App {AppId} (exempt)"]);
        update.Definition.BypassActors.Should().Equal([App, otherApp]);

        PlanFor([new(1, Ruleset1(App, otherApp)), ruleset2], classic: null).IsConfigured.Should().BeTrue();

        var always = otherApp with { BypassMode = "always" };
        var person = new ForgeRulesetActor(42, "User", "always");
        PlanFor([new(1, Ruleset1(App, always, person)), ruleset2], classic: null).Steps.Should().ContainSingle()
            .Which.Should().BeOfType<UpdateRulesetStep>().Which.Definition.BypassActors.Should().Equal([App]);
    }

    [Fact]
    public void The_app_is_removed_from_ruleset_2_and_people_stay()
    {
        var maintainer = new ForgeRulesetActor(42, "User", "always");
        var plan = PlanFor([new(1, Ruleset1()), new(2, Ruleset2(0, ["build"], App, maintainer))], classic: null);

        var update = plan.Steps.Should().ContainSingle().Which.Should().BeOfType<UpdateRulesetStep>().Which;
        update.Id.Should().Be(2);
        update.Definition.BypassActors.Should().Equal([maintainer], "people may bypass ruleset 2 as policy wants (O22)");
        update.Changes.Should().Equal([$"bypass removed: App {AppId} (exempt)"]);
    }

    [Fact]
    public void Parameters_github_adds_and_other_rules_are_kept_without_a_change()
    {
        var existing = Ruleset2(0, ["build"]);
        existing.Rules[0].Parameters!["allowed_merge_methods"] = new JsonArray("merge", "squash", "rebase");
        existing.Rules[1].Parameters!["required_status_checks"]![0]!["integration_id"] = 15368;
        existing = existing with { Rules = [.. existing.Rules, new("required_signatures")] };

        var plan = PlanFor([new(1, Ruleset1()), new(2, existing)], classic: null);

        plan.IsConfigured.Should().BeTrue();
    }

    [Fact]
    public void A_disabled_ruleset_extra_rule_and_wrong_branches_are_all_listed()
    {
        var drifted = Ruleset1() with
        {
            Enforcement = "disabled",
            Include = ["refs/heads/main"],
            Rules = [.. Ruleset1().Rules, new("pull_request", new JsonObject { ["required_approving_review_count"] = 1 })],
        };

        var plan = PlanFor([new(1, drifted), new(2, Ruleset2(0, ["build"]))], classic: null);

        var update = plan.Steps.Should().ContainSingle().Which.Should().BeOfType<UpdateRulesetStep>().Which;
        update.Changes.Should().Equal([
            "enforcement: disabled → active",
            "branches: refs/heads/main → ~DEFAULT_BRANCH",
            "rule removed: pull_request",
        ]);
    }

    [Fact]
    public void Admins_by_pull_request_are_kept_unless_the_person_says_otherwise()
    {
        var withAdmins = new ForgeExistingRuleset(1, Ruleset1(App, AdminsByPullRequest));
        var ruleset2 = new ForgeExistingRuleset(2, Ruleset2(0, ["build"]));

        PlanFor([withAdmins, ruleset2], classic: null).IsConfigured.Should().BeTrue();
        PlanFor([withAdmins, ruleset2], classic: null, admins: false).Steps.Should().ContainSingle()
            .Which.Should().BeOfType<UpdateRulesetStep>().Which.Changes.Should().Equal(["bypass removed: Repository admin role (pull_request)"]);
        PlanFor([], classic: null, admins: true).Steps[0].Should().BeOfType<CreateRulesetStep>()
            .Which.Definition.BypassActors.Should().Equal([App, AdminsByPullRequest]);
    }

    [Fact]
    public void Classic_settings_move_or_are_dropped_and_each_says_so()
    {
        var classic = NgBootstrapClassic("build", "lint") with
        {
            DismissStaleReviews = true,
            RequireLastPushApproval = true,
            RequireCodeOwnerReviews = true,
            RequiredLinearHistory = true,
        };

        var plan = PlanFor([], classic, approvals: 1, checks: ["build"]);

        var ruleset2 = plan.Steps[1].Should().BeOfType<CreateRulesetStep>().Which.Definition;
        ruleset2.Rules.Select(r => r.Type).Should().Equal(["pull_request", "required_status_checks", "required_linear_history"]);
        var pullRequest = ruleset2.Rules[0].Parameters!;
        pullRequest["require_code_owner_review"]!.GetValue<bool>().Should().BeTrue();
        pullRequest["dismiss_stale_reviews_on_push"]!.GetValue<bool>().Should().BeFalse("§3.1: the queue's rebase would dismiss every approval");
        pullRequest["require_last_push_approval"]!.GetValue<bool>().Should().BeFalse();

        var notes = plan.Steps[2].Should().BeOfType<DeleteClassicProtectionStep>().Which.Notes;
        notes.Should().Contain("Required checks dropped: lint.");
        notes.Should().Contain(n => n.StartsWith("Dismiss stale approvals: dropped"));
        notes.Should().Contain(n => n.StartsWith("Approval of the most recent push: dropped"));
        notes.Should().Contain(n => n.StartsWith("Code owner review: moved"));
        notes.Should().Contain(n => n.StartsWith("Linear history: moved"));
    }

    [Fact]
    public void What_cannot_be_carried_over_stops_the_plan()
    {
        var restricted = NgBootstrapClassic() with { HasPushRestrictions = true };
        var twice = new ForgeExistingRuleset[] { new(1, Ruleset1()), new(3, Ruleset1()) };

        PlanFor([], restricted).Blockers.Should().ContainSingle().Which.Should().StartWith("Classic protection limits pushes");
        PlanFor([], null, checks: ["build", CheckGate.QueueCheckName]).Blockers.Should().ContainSingle().Which.Should().Contain("(D62)");
        PlanFor(twice, null).Blockers.Should().ContainSingle().Which.Should().StartWith("2 rulesets are named \"merge-queue: restrict updates\"");
        PlanFor([], null, approvals: 11).Blockers.Should().ContainSingle();
        PlanFor([], restricted).CanApply.Should().BeFalse();
    }

    private const string Lines = "refs/heads/release/*-*.x";

    private static string[] ContextsOf(ForgeRulesetDefinition definition)
        => [.. definition.Rules.Single(r => r.Type == "required_status_checks").Parameters!["required_status_checks"]!.AsArray()
            .Select(c => c!["context"]!.GetValue<string>())];

    [Fact]
    public void Release_lines_get_ruleset_1_and_their_own_checks_ruleset()
    {
        var plan = Plan(new ForgeRulesetState(AppId, [], null),
            new MergeQueueRulesetChoices(0, ["build"], ReleaseLines: [Lines], LineRequiredChecks: ["build", "e2e"]));

        var created = plan.Steps.OfType<CreateRulesetStep>().ToDictionary(s => s.Definition.Name, s => s.Definition);
        created[RestrictUpdatesName].Include.Should().Equal(["~DEFAULT_BRANCH", Lines]);
        created[ReviewsAndChecksName].Include.Should().Equal(["~DEFAULT_BRANCH"], "ruleset 2's checks are the default head's");

        var line = created[LineChecksName];
        line.Include.Should().Equal([Lines]);
        line.Rules.Select(r => r.Type).Should().Equal(["required_status_checks"], "no pull_request rule: the App's backports need no review");
        line.BypassActors.Should().Equal([new ForgeRulesetActor(AppId, "Integration", "exempt")],
            "the App creates the lines (a cut answered 422 without it, SpaServices 2026-11-12) and pushes promotions; the queue waits for the checks itself");
        ContextsOf(line).Should().Equal([LinePolicyCheckName, "build", "e2e"]);
        line.Rules.Single().Parameters!["required_status_checks"]!.AsArray().OfType<JsonObject>()
            .Single(c => c["context"]!.GetValue<string>() == LinePolicyCheckName)["integration_id"]!.GetValue<long>()
            .Should().Be(AppId, "segment E: only the App can report the line policy");
        plan.CanApply.Should().BeTrue();
    }

    [Fact]
    public void An_existing_line_checks_ruleset_without_the_App_gains_its_bypass_and_keeps_its_people()
    {
        // SpaServices' ruleset 3 (27139872), created 2026-11-12 with no bypass at all: the next run adds the App.
        var person = new ForgeRulesetActor(RepositoryAdminRoleId, "RepositoryRole", "always");
        var existing = new ForgeExistingRuleset(3, new ForgeRulesetDefinition(LineChecksName, "branch", "active", [Lines], [],
            [new ForgeRulesetRule("required_status_checks", null)], [person]));
        var plan = Plan(new ForgeRulesetState(AppId, [existing], null),
            new MergeQueueRulesetChoices(0, ["build"], ReleaseLines: [Lines], LineRequiredChecks: ["build"]));

        plan.Steps.OfType<UpdateRulesetStep>().Single(s => s.Definition.Name == LineChecksName).Definition.BypassActors
            .Should().Equal([new ForgeRulesetActor(AppId, "Integration", "exempt"), person]);
    }

    [Fact]
    public void Without_lines_there_is_no_line_checks_ruleset()
    {
        PlanFor([], classic: null).Steps.OfType<CreateRulesetStep>()
            .Should().NotContain(s => s.Definition.Name == LineChecksName);
    }

    [Fact]
    public void A_new_line_checks_ruleset_takes_the_default_branch_checks_when_none_are_chosen()
    {
        var plan = Plan(new ForgeRulesetState(AppId, [], null),
            new MergeQueueRulesetChoices(0, ["build", "test"], ReleaseLines: [Lines]));

        ContextsOf(plan.Steps.OfType<CreateRulesetStep>().Single(s => s.Definition.Name == LineChecksName).Definition)
            .Should().Equal([LinePolicyCheckName, "build", "test"]);
    }

    // The Tools onboarding (2026-11-12): a repository with no protection got 1, so its solo maintainer could never merge.
    [Fact]
    public void The_approvals_start_from_ruleset_2_then_classic_protection_then_none()
    {
        Defaults(new ForgeRulesetState(AppId, [], null)).RequiredApprovals.Should().Be(0);
        Defaults(new ForgeRulesetState(AppId, [], NgBootstrapClassic("build"))).RequiredApprovals.Should().Be(1);
        Defaults(new ForgeRulesetState(AppId, [new ForgeExistingRuleset(2, Ruleset2(2, ["build"]))], NgBootstrapClassic("build")))
            .RequiredApprovals.Should().Be(2);
    }

    [Fact]
    public void An_existing_line_checks_ruleset_keeps_its_checks_when_none_are_chosen()
    {
        var created = Plan(new ForgeRulesetState(AppId, [], null),
                new MergeQueueRulesetChoices(0, ["build"], ReleaseLines: [Lines], LineRequiredChecks: ["e2e"]))
            .Steps.OfType<CreateRulesetStep>().Select(s => s.Definition).ToList();
        var stored = created.Select((d, i) => new ForgeExistingRuleset(i + 1, d)).ToList();

        var plan = Plan(new ForgeRulesetState(AppId, stored, null),
            new MergeQueueRulesetChoices(0, ["build", "test"], ReleaseLines: [Lines]));

        plan.Steps.OfType<UpdateRulesetStep>().Should().NotContain(s => s.Definition.Name == LineChecksName);
        Defaults(new ForgeRulesetState(AppId, stored, null)).LineRequiredChecks.Should().Equal(["e2e"]);
    }

    [Fact]
    public void Changed_line_checks_update_the_line_checks_ruleset()
    {
        var created = Plan(new ForgeRulesetState(AppId, [], null),
                new MergeQueueRulesetChoices(0, ["build"], ReleaseLines: [Lines], LineRequiredChecks: ["e2e"]))
            .Steps.OfType<CreateRulesetStep>().Select(s => s.Definition).ToList();
        var stored = created.Select((d, i) => new ForgeExistingRuleset(i + 1, d)).ToList();

        var plan = Plan(new ForgeRulesetState(AppId, stored, null),
            new MergeQueueRulesetChoices(0, ["build"], ReleaseLines: [Lines], LineRequiredChecks: ["e2e", "build"]));

        var update = plan.Steps.Should().ContainSingle().Which.Should().BeOfType<UpdateRulesetStep>().Which;
        update.Definition.Name.Should().Be(LineChecksName);
        ContextsOf(update.Definition).Should().Equal([LinePolicyCheckName, "build", "e2e"], "the rule lists its checks in ordinal order");
    }

    [Fact]
    public void Empty_line_checks_still_require_the_line_policy()
    {
        // Segment E: the line policy is what keeps people's pull requests off a current line, so it never goes.
        var plan = Plan(new ForgeRulesetState(AppId, [], null),
            new MergeQueueRulesetChoices(0, ["build"], ReleaseLines: [Lines], LineRequiredChecks: []));

        ContextsOf(plan.Steps.OfType<CreateRulesetStep>().Single(s => s.Definition.Name == LineChecksName).Definition)
            .Should().Equal([LinePolicyCheckName]);
    }

    [Fact]
    public void The_form_never_offers_the_line_policy_and_a_second_run_changes_nothing()
    {
        var choices = new MergeQueueRulesetChoices(0, ["build"], ReleaseLines: [Lines], LineRequiredChecks: ["e2e"]);
        var stored = Plan(new ForgeRulesetState(AppId, [], null), choices)
            .Steps.OfType<CreateRulesetStep>().Select((s, i) => new ForgeExistingRuleset(i + 1, s.Definition)).ToList();

        Defaults(new ForgeRulesetState(AppId, stored, null)).LineRequiredChecks.Should().Equal(["e2e"]);
        Plan(new ForgeRulesetState(AppId, stored, null), choices).IsConfigured.Should().BeTrue();
    }

    [Fact]
    public void A_line_policy_bound_to_another_App_is_bound_to_this_one()
    {
        var stored = Plan(new ForgeRulesetState(AppId, [], null), new MergeQueueRulesetChoices(0, ["build"], ReleaseLines: [Lines]))
            .Steps.OfType<CreateRulesetStep>().Select((s, i) => new ForgeExistingRuleset(i + 1, s.Definition)).ToList();
        var line = stored.Single(r => r.Definition.Name == LineChecksName);
        var other = (JsonObject)line.Definition.Rules.Single().Parameters!.DeepClone();
        other["required_status_checks"]!.AsArray().OfType<JsonObject>().Single(c => c["context"]!.GetValue<string>() == LinePolicyCheckName)["integration_id"] = 42;
        stored[stored.IndexOf(line)] = line with { Definition = line.Definition with { Rules = [new("required_status_checks", other)] } };

        var update = Plan(new ForgeRulesetState(AppId, stored, null), new MergeQueueRulesetChoices(0, ["build"], ReleaseLines: [Lines]))
            .Steps.OfType<UpdateRulesetStep>().Single(s => s.Definition.Name == LineChecksName);
        update.Definition.Rules.Single().Parameters!["required_status_checks"]!.AsArray().OfType<JsonObject>()
            .Single(c => c["context"]!.GetValue<string>() == LinePolicyCheckName)["integration_id"]!.GetValue<long>().Should().Be(AppId);
    }

    [Fact]
    public void A_ruleset_1_on_the_default_branch_only_is_extended_to_the_lines()
    {
        var created = PlanFor([], classic: null).Steps.OfType<CreateRulesetStep>().Select(s => s.Definition).ToList();
        var stored = created.Select((d, i) => new ForgeExistingRuleset(i + 1, d)).ToList();

        var plan = Plan(new ForgeRulesetState(AppId, stored, null),
            new MergeQueueRulesetChoices(0, ["build"], ReleaseLines: [Lines]));

        plan.Steps.OfType<UpdateRulesetStep>().Should().ContainSingle()
            .Which.Definition.Include.Should().Equal(["~DEFAULT_BRANCH", Lines]);
        plan.Steps.OfType<CreateRulesetStep>().Select(s => s.Definition.Name).Should().Equal([LineChecksName, BackportBranchesName]);
    }

    [Fact]
    public void Release_lines_get_a_backport_branches_ruleset_only_the_App_and_admins_pass()
    {
        // Segment D (Q13): what lets the App's backports merge without an approval.
        var plan = Plan(new ForgeRulesetState(AppId, [], null), new MergeQueueRulesetChoices(0, ["build"], ReleaseLines: [Lines]));

        var backports = plan.Steps.OfType<CreateRulesetStep>().Single(s => s.Definition.Name == BackportBranchesName).Definition;
        backports.Include.Should().Equal([BackportBranchesPattern]);
        backports.Rules.Select(r => r.Type).Should().Equal(["creation", "update", "deletion"]);
        backports.BypassActors.Should().Equal([new ForgeRulesetActor(AppId, "Integration", "exempt"), new ForgeRulesetActor(RepositoryAdminRoleId, "RepositoryRole", "always")]);

        var stored = plan.Steps.OfType<CreateRulesetStep>().Select((s, i) => new ForgeExistingRuleset(i + 1, s.Definition)).ToList();
        Plan(new ForgeRulesetState(AppId, stored, null), new MergeQueueRulesetChoices(0, ["build"], ReleaseLines: [Lines]))
            .IsConfigured.Should().BeTrue("a second run changes nothing");
    }

    [Fact]
    public void A_backport_branches_ruleset_loses_a_persons_bypass_and_keeps_another_App()
    {
        var other = new ForgeRulesetActor(4567511, "Integration", "exempt");
        var person = new ForgeRulesetActor(4, "RepositoryRole", "always");
        var existing = new ForgeRulesetDefinition(BackportBranchesName, "branch", "active", [BackportBranchesPattern], [],
            [new("update")], [other, person]);

        var plan = Plan(new ForgeRulesetState(AppId, [new(4, existing)], null), new MergeQueueRulesetChoices(0, ["build"], ReleaseLines: [Lines]));

        var update = plan.Steps.OfType<UpdateRulesetStep>().Single(s => s.Definition.Name == BackportBranchesName);
        update.Definition.BypassActors.Should().Equal([new ForgeRulesetActor(AppId, "Integration", "exempt"), other, new ForgeRulesetActor(RepositoryAdminRoleId, "RepositoryRole", "always")]);
        update.Definition.Rules.Select(r => r.Type).Should().Equal(["creation", "update", "deletion"]);
    }

    [Fact]
    public void Without_lines_there_is_no_backport_branches_ruleset()
    {
        PlanFor([], classic: null).Steps.OfType<CreateRulesetStep>()
            .Should().NotContain(s => s.Definition.Name == BackportBranchesName);
    }

    private const long GitHubActions = 15368;

    private static long? PinOf(ForgeRulesetDefinition definition, string context)
        => definition.Rules.Single(r => r.Type == "required_status_checks").Parameters!["required_status_checks"]!.AsArray().OfType<JsonObject>()
            .Single(c => c["context"]!.GetValue<string>() == context)["integration_id"]?.GetValue<long>();

    /// <summary>Ruleset 2 as GitHub returns it: <paramref name="checksJson"/> parsed, so its values are read from JSON text.</summary>
    private static ForgeRulesetDefinition Ruleset2FromGitHub(string checksJson) => Ruleset2(0, []) with
    {
        Rules =
        [
            .. Ruleset2(0, []).Rules,
            new("required_status_checks", (JsonObject)JsonNode.Parse($$"""{"strict_required_status_checks_policy":true,"required_status_checks":{{checksJson}}}""")!),
        ],
    };

    /// <summary>
    /// #714 Dec3, Dec4: with a summary job, rulesets 2 and 3 require the App's own checks among the picks and the job, nothing
    /// else; the App's checks are pinned to the App and the job to the App that reports it.
    /// </summary>
    [Fact]
    public void A_summary_job_stands_alone_beside_the_Apps_checks_and_each_is_pinned()
    {
        var plan = Plan(new ForgeRulesetState(AppId, [], null), new MergeQueueRulesetChoices(0, ["build", "test", "coverage/patch"],
            ReleaseLines: [Lines],
            LineRequiredChecks: ["build", "e2e"],
            SummaryJob: "pull-request",
            CheckApps: new Dictionary<string, long> { ["pull-request"] = GitHubActions, ["build"] = GitHubActions }));

        var created = plan.Steps.OfType<CreateRulesetStep>().ToDictionary(s => s.Definition.Name, s => s.Definition);
        ContextsOf(created[ReviewsAndChecksName]).Should().Equal(["coverage/patch", "pull-request"]);
        PinOf(created[ReviewsAndChecksName], "coverage/patch").Should().Be(AppId);
        PinOf(created[ReviewsAndChecksName], "pull-request").Should().Be(GitHubActions);
        ContextsOf(created[LineChecksName]).Should().Equal([LinePolicyCheckName, "pull-request"], "the lines' picks hold none of the App's checks");
        PinOf(created[LineChecksName], LinePolicyCheckName).Should().Be(AppId);
    }

    [Fact]
    public void Without_a_summary_job_the_picks_stay_and_a_name_without_a_known_App_keeps_its_pin()
    {
        var existing = Ruleset2(0, ["build", "lint"]);
        existing.Rules[1].Parameters!["required_status_checks"]![1]!["integration_id"] = 42L;

        var plan = PlanFor([new(1, Ruleset1()), new(2, existing)], classic: null, checks: ["build", "lint", "test"]);

        var update = plan.Steps.Should().ContainSingle().Which.Should().BeOfType<UpdateRulesetStep>().Which;
        ContextsOf(update.Definition).Should().Equal(["build", "lint", "test"]);
        PinOf(update.Definition, "lint").Should().Be(42);
        PinOf(update.Definition, "test").HasValue.Should().BeFalse("no App is known to report it");
    }

    /// <summary>#714 Dec10 B1: the confirmation names the checks an update adds, drops and re-pins.</summary>
    [Fact]
    public void An_update_names_the_checks_it_adds_drops_and_repins()
    {
        var existing = Ruleset2FromGitHub("""[{"context":"build","integration_id":999},{"context":"test"}]""");

        var plan = Plan(new ForgeRulesetState(AppId, [new(1, Ruleset1()), new(2, existing)], null),
            new MergeQueueRulesetChoices(0, ["build", "lint"], CheckApps: new Dictionary<string, long> { ["build"] = GitHubActions }));

        plan.Steps.Should().ContainSingle().Which.Should().BeOfType<UpdateRulesetStep>().Which.Changes.Should().Equal([
            "required checks added: lint",
            "required checks dropped: test",
            "\"build\" pinned: App 999 → App 15368",
        ]);
    }

    /// <summary>Pins GitHub already holds, read from its JSON, compare equal to the plan's: a second run changes nothing.</summary>
    [Fact]
    public void Pins_github_already_holds_are_not_changed_again()
    {
        var existing = Ruleset2FromGitHub($$"""[{"context":"build","integration_id":15368},{"context":"coverage/patch","integration_id":{{AppId}}}]""");

        Plan(new ForgeRulesetState(AppId, [new(1, Ruleset1()), new(2, existing)], null),
                new MergeQueueRulesetChoices(0, ["build", "coverage/patch"], CheckApps: new Dictionary<string, long> { ["build"] = GitHubActions }))
            .IsConfigured.Should().BeTrue();
    }

    [Fact]
    public void Only_a_strict_parameter_change_is_still_named_as_a_rule_change()
    {
        var existing = Ruleset2(0, ["build"]);
        existing.Rules[1].Parameters!["strict_required_status_checks_policy"] = false;

        PlanFor([new(1, Ruleset1()), new(2, existing)], classic: null).Steps.Should().ContainSingle()
            .Which.Should().BeOfType<UpdateRulesetStep>().Which.Changes.Should().Equal(["rule changed: required_status_checks"]);
    }

    /// <summary>#714 Dec3: the form starts with the summary job only when the rules are in that shape already.</summary>
    [Fact]
    public void The_summary_job_starts_from_rules_that_already_require_one_job()
    {
        Defaults(new ForgeRulesetState(AppId, [new(2, Ruleset2(0, ["coverage/patch", "pull-request"]))], null))
            .SummaryJob.Should().Be("pull-request");
        Defaults(new ForgeRulesetState(AppId, [new(2, Ruleset2(0, ["build", "test"]))], null))
            .SummaryJob.Should().BeNull("one check per job");
        Defaults(new ForgeRulesetState(AppId, [new(2, Ruleset2(0, ["coverage/patch"]))], null))
            .SummaryJob.Should().BeNull("no job at all");

        var lines = Plan(new ForgeRulesetState(AppId, [], null),
                new MergeQueueRulesetChoices(0, ["pull-request"], ReleaseLines: [Lines], LineRequiredChecks: ["e2e"]))
            .Steps.OfType<CreateRulesetStep>().Select((s, i) => new ForgeExistingRuleset(i + 1, s.Definition)).ToList();
        Defaults(new ForgeRulesetState(AppId, lines, null)).SummaryJob
            .Should().BeNull("ruleset 3 requires another job, which the summary-job shape would drop");
    }

    /// <summary>#714 Dec11: what counts as one check per job.</summary>
    [Theory]
    [InlineData(new[] { "build", "test" }, true)]
    [InlineData(new[] { "coverage/patch", "build" }, false)]
    [InlineData(new[] { "coverage/patch", "coverage/project", LinePolicyCheckName }, false)]
    [InlineData(new[] { "build", "build" }, false)]
    public void Rules_require_one_check_per_job_with_more_than_one_name_the_App_does_not_report(string[] required, bool perJob)
        => RequiresOneCheckPerJob(required).Should().Be(perJob);
}
