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

    [Fact]
    public void Release_lines_get_ruleset_1_only()
    {
        var plan = Plan(new ForgeRulesetState(AppId, [], null),
            new MergeQueueRulesetChoices(0, ["build"], ReleaseLines: ["refs/heads/release/*-*.x"]));

        var created = plan.Steps.OfType<CreateRulesetStep>().ToDictionary(s => s.Definition.Name, s => s.Definition.Include);
        created[RestrictUpdatesName].Should().Equal(["~DEFAULT_BRANCH", "refs/heads/release/*-*.x"]);
        created[ReviewsAndChecksName].Should().Equal(["~DEFAULT_BRANCH"], "ruleset 2's checks are the default head's");
    }

    [Fact]
    public void A_ruleset_1_on_the_default_branch_only_is_extended_to_the_lines()
    {
        var created = PlanFor([], classic: null).Steps.OfType<CreateRulesetStep>().Select(s => s.Definition).ToList();
        var stored = created.Select((d, i) => new ForgeExistingRuleset(i + 1, d)).ToList();

        var plan = Plan(new ForgeRulesetState(AppId, stored, null),
            new MergeQueueRulesetChoices(0, ["build"], ReleaseLines: ["refs/heads/release/*-*.x"]));

        plan.Steps.Should().ContainSingle().Which.Should().BeOfType<UpdateRulesetStep>()
            .Which.Definition.Include.Should().Equal(["~DEFAULT_BRANCH", "refs/heads/release/*-*.x"]);
    }
}
