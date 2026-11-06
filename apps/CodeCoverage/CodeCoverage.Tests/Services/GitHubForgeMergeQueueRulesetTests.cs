using System.Net;
using System.Text.Json.Nodes;
using CodeCoverage.Entities;
using CodeCoverage.Forge;
using CodeCoverage.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Octokit;
using Raven.Client.Documents.Session;
using Xunit;
using Account = CodeCoverage.Entities.Account;
using Repository = CodeCoverage.Entities.Repository;

namespace CodeCoverage.Tests.Services;

/// <summary>
/// #554 f2: the GitHub half of the merge queue's rulesets. Reads and writes run on the signed-in admin's own token, a
/// one-shot OAuth token the caller revokes; the App holds no Administration and mints nothing here.
/// </summary>
public class GitHubForgeMergeQueueRulesetTests
{
    private const long InstallationId = 4242;
    private const long AppId = 1_234_567;
    private const string Repo = "/repos/acme/widget";
    /// <summary>A fixture value, not credential-shaped.</summary>
    private const string UserToken = "stub-user-token";
    private static readonly string AccountId = Account.DocumentId(EForgeProvider.GitHub, 77);

    private static Repository Target() => new()
    {
        Id = Repository.DocumentId(EForgeProvider.GitHub, 910040),
        GitHubId = 910040, Name = "widget", FullName = "acme/widget", OwnerLogin = "acme", Account = AccountId,
    };

    private static GitHubForgeMergeQueue Queue(StubGitHub github)
    {
        var session = Substitute.For<IAsyncDocumentSession>();
        session.LoadAsync<Account>(AccountId, Arg.Any<CancellationToken>())
            .Returns(new Account { GitHubId = 77, Login = "acme", InstallationId = InstallationId });
        return new GitHubForgeMergeQueue(github, new SingleClientHttpFactory(github.Rest), session, NullLogger<GitHubForgeMergeQueue>.Instance);
    }

    private static StubGitHub WithApp(StubGitHub? github = null) => (github ?? new StubGitHub())
        .On(HttpMethod.Get, "/app", HttpStatusCode.OK, $$"""{"id":{{AppId}},"slug":"coverageproduction","name":"CoverageProduction"}""");

    /// <summary>Every request to the repository carried the user's token, and no installation token was minted.</summary>
    private static void OnlyTheUsersToken(StubGitHub github)
    {
        github.Rest.Requests.Where(r => r.Uri!.AbsolutePath.StartsWith(Repo)).Select(r => r.AuthorizationParameter).Distinct()
            .Should().Equal([UserToken], "the App holds no Administration (#554 redesign)");
        github.InstallationClients.Should().BeEmpty("no installation token is involved");
    }

    /// <summary>ng-bootstrap's classic protection on master, 2026-10-30 (#554's table), in GitHub's documented shape.</summary>
    private const string LockedWithOneReview = """
        {"url":"https://api.github.com/repos/acme/widget/branches/master/protection",
         "required_status_checks":{"strict":true,"contexts":["build"],"checks":[{"context":"build","app_id":15368},{"context":"test","app_id":15368}]},
         "required_pull_request_reviews":{"dismiss_stale_reviews":false,"require_code_owner_reviews":false,"require_last_push_approval":false,"required_approving_review_count":1},
         "required_signatures":{"enabled":false},"enforce_admins":{"enabled":false},"required_linear_history":{"enabled":false},
         "allow_force_pushes":{"enabled":false},"allow_deletions":{"enabled":false},"block_creations":{"enabled":false},
         "required_conversation_resolution":{"enabled":false},"lock_branch":{"enabled":true},"allow_fork_syncing":{"enabled":false}}
        """;

    /// <summary>A ruleset made with the UI's "Always allow" for the App (D16), disabled.</summary>
    private static readonly string BotRuleset = $$$"""
        {"id":26211467,"name":"merge-queue: restrict updates","target":"branch","source_type":"Repository","source":"acme/widget",
         "enforcement":"disabled","conditions":{"ref_name":{"exclude":[],"include":["~DEFAULT_BRANCH"]}},
         "rules":[{"type":"update"},{"type":"deletion"},{"type":"non_fast_forward"}],
         "bypass_actors":[{"actor_id":{{{AppId}}},"actor_type":"Integration","bypass_mode":"always"}],"current_user_can_bypass":"always"}
        """;

    [Fact]
    public async Task The_state_is_read_with_the_users_token()
    {
        var github = WithApp()
            .On(HttpMethod.Get, $"{Repo}/rulesets", HttpStatusCode.OK, """[{"id":26211467,"name":"merge-queue: restrict updates"}]""")
            .On(HttpMethod.Get, $"{Repo}/rulesets/26211467", HttpStatusCode.OK, BotRuleset)
            .On(HttpMethod.Get, $"{Repo}/branches/master/protection", HttpStatusCode.OK, LockedWithOneReview);

        var state = (await Queue(github).GetRulesetStateAsync(Target(), "master", UserToken))!;

        state.AppId.Should().Be(AppId);
        state.RulesetsAvailable.Should().BeTrue();
        var ruleset = state.Rulesets.Should().ContainSingle().Which;
        ruleset.Id.Should().Be(26211467);
        ruleset.Definition.Enforcement.Should().Be("disabled");
        ruleset.Definition.Include.Should().Equal(["~DEFAULT_BRANCH"]);
        ruleset.Definition.Rules.Select(r => r.Type).Should().Equal(["update", "deletion", "non_fast_forward"]);
        ruleset.Definition.BypassActors.Should().Equal([new ForgeRulesetActor(AppId, "Integration", "always")]);

        var classic = state.Classic!;
        classic.LockBranch.Should().BeTrue();
        classic.RequiredApprovals.Should().Be(1);
        classic.RequiredChecks.Should().Equal(["build", "test"]);
        classic.HasPushRestrictions.Should().BeFalse();

        OnlyTheUsersToken(github);
    }

    [Fact]
    public async Task No_classic_protection_and_a_plan_without_rulesets_read_as_such()
    {
        var github = WithApp()
            .On(HttpMethod.Get, $"{Repo}/rulesets", HttpStatusCode.Forbidden,
                """{"message":"Upgrade to GitHub Pro or make this repository public to enable this feature.","status":"403"}""")
            .On(HttpMethod.Get, $"{Repo}/branches/main/protection", HttpStatusCode.NotFound, """{"message":"Branch not protected","status":"404"}""");

        var state = (await Queue(github).GetRulesetStateAsync(Target(), "main", UserToken))!;

        state.RulesetsAvailable.Should().BeFalse();
        state.Rulesets.Should().BeEmpty();
        state.Classic.Should().BeNull();
        MergeQueueRulesetPlanner.Plan(state, new MergeQueueRulesetChoices(0, ["build"])).Blockers.Should().ContainSingle()
            .Which.Should().StartWith("This repository's plan has no rulesets");
    }

    [Fact]
    public async Task Apply_creates_both_rulesets_before_it_deletes_the_classic_protection()
    {
        var posted = new List<string>();
        var github = WithApp()
            .On(HttpMethod.Post, $"{Repo}/rulesets", (_, body) =>
            {
                posted.Add(body!);
                return StubHttpMessageHandler.Json(HttpStatusCode.Created, $$"""{"id":{{100 + posted.Count}}}""");
            })
            .On(HttpMethod.Delete, $"{Repo}/branches/master/protection", (_, _) => new HttpResponseMessage(HttpStatusCode.NoContent));
        var state = new ForgeRulesetState(AppId, [], GitHubForgeMergeQueue.ReadClassicProtection(System.Text.Json.JsonDocument.Parse(LockedWithOneReview).RootElement));
        var plan = MergeQueueRulesetPlanner.Plan(state, new MergeQueueRulesetChoices(0, ["build", "test"]));

        var applied = await Queue(github).ApplyRulesetPlanAsync(Target(), "master", plan, UserToken);

        github.Calls.Where(c => c.StartsWith("POST /repos") || c.StartsWith("DELETE /repos")).Should().Equal([
            "POST /repos/acme/widget/rulesets",
            "POST /repos/acme/widget/rulesets",
            "DELETE /repos/acme/widget/branches/master/protection",
        ]);
        var first = JsonNode.Parse(posted[0])!;
        first["name"]!.GetValue<string>().Should().Be(MergeQueueRulesetPlanner.RestrictUpdatesName);
        first["bypass_actors"]!.ToJsonString().Should().Be($$"""[{"actor_id":{{AppId}},"actor_type":"Integration","bypass_mode":"exempt"}]""");
        first["conditions"]!["ref_name"]!["include"]!.ToJsonString().Should().Be("""["~DEFAULT_BRANCH"]""");
        JsonNode.Parse(posted[1])!["bypass_actors"]!.AsArray().Count.Should().Be(0, "the App must not bypass ruleset 2 (O22)");

        applied.Select(a => a.Url).Should().Equal([
            "https://github.com/acme/widget/rules/101",
            "https://github.com/acme/widget/rules/102",
            "https://github.com/acme/widget/settings/branches",
        ]);
        OnlyTheUsersToken(github);
        github.Calls.Should().NotContain(c => c.Contains("/installation/token"), "the caller revokes its own token, not the adapter");
    }

    [Fact]
    public async Task An_update_puts_the_whole_ruleset_and_links_it()
    {
        string? put = null;
        var github = WithApp()
            .On(HttpMethod.Put, $"{Repo}/rulesets/26211467", (_, body) =>
            {
                put = body;
                return StubHttpMessageHandler.Json(HttpStatusCode.OK, """{"id":26211467}""");
            });
        var existing = GitHubForgeMergeQueue.ReadRulesetDefinition(System.Text.Json.JsonDocument.Parse(BotRuleset).RootElement);
        var plan = new MergeQueueRulesetPlan([new UpdateRulesetStep(26211467, existing with { Enforcement = "active" }, ["enforcement: disabled → active"])], []);

        var applied = await Queue(github).ApplyRulesetPlanAsync(Target(), "master", plan, UserToken);

        JsonNode.Parse(put!)!["enforcement"]!.GetValue<string>().Should().Be("active");
        applied.Should().ContainSingle().Which.Should().Be(new ForgeRulesetApplied(
            "Updated ruleset \"merge-queue: restrict updates\": enforcement: disabled → active.", "https://github.com/acme/widget/rules/26211467"));
    }

    /// <summary>
    /// A person without admin on the repository, or an organization that has not approved the OAuth app: GitHub's own
    /// message is kept, and no App permission is named, because accepting one would not help.
    /// </summary>
    [Fact]
    public async Task A_refused_step_keeps_githubs_message_and_names_no_app_permission()
    {
        var github = WithApp()
            .On(HttpMethod.Post, $"{Repo}/rulesets", HttpStatusCode.Forbidden, """{"message":"Must have admin rights to Repository.","status":"403"}""");
        var plan = new MergeQueueRulesetPlan([new CreateRulesetStep(new ForgeRulesetDefinition(
            MergeQueueRulesetPlanner.RestrictUpdatesName, "branch", "active", ["~DEFAULT_BRANCH"], [], [], []))], []);

        var refused = await Queue(github).Invoking(q => q.ApplyRulesetPlanAsync(Target(), "master", plan, UserToken))
            .Should().ThrowAsync<ForgeAccessDeniedException>();

        refused.Which.Message.Should().Contain("Must have admin rights to Repository.");
        refused.Which.RequiredPermission.Should().BeNull();
    }

    [Fact]
    public async Task A_plan_with_blockers_or_nothing_to_do_changes_nothing()
    {
        var github = WithApp();
        var queue = Queue(github);

        await queue.Invoking(q => q.ApplyRulesetPlanAsync(Target(), "master", new MergeQueueRulesetPlan([new DeleteClassicProtectionStep([])], ["blocked"]), UserToken))
            .Should().ThrowAsync<InvalidOperationException>();
        await queue.Invoking(q => q.ApplyRulesetPlanAsync(Target(), "master", new MergeQueueRulesetPlan([], []), UserToken))
            .Should().ThrowAsync<InvalidOperationException>();
        github.Rest.Requests.Should().BeEmpty("a plan that will not run sends nothing");
    }
}
