using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using CodeCoverage.Entities;
using CodeCoverage.Forge;
using CodeCoverage.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Raven.Client.Documents.Session;
using Xunit;

namespace CodeCoverage.Tests.Services;

/// <summary>
/// <see cref="GitHubForgeMergeQueue"/> over <see cref="StubGitHub"/>, replaying the responses measured live on
/// 2026-10-14 (merge-queue PRD D13–D19; the spike notes <c>spikes_live.md</c> and <c>spikes_app.md</c>). Where a
/// body below says "live", its shape (and for the merge-async envelopes, its text) is what GitHub answered the
/// spike; the rest follow docs.github.com/rest. Owner, repository and logins are neutral placeholders.
/// </summary>
public class GitHubForgeMergeQueueTests
{
    private const long InstallationId = 4242;
    private const string Head = "4a5b6c7d8e9f00112233445566778899aabbccdd";
    private const string MergeSha = "85b3ffa3488c6c6fcffb1576054a5fec946ef318";
    private const string Uuid = "d45ff22b-73cf-44a4-9eb0-cbf81c955578";
    private const string Repo = "/repos/acme/widget";

    private static readonly string AccountId = Account.DocumentId(EForgeProvider.GitHub, 77);

    private static Repository Target(bool installed = true) => new()
    {
        Id = Entities.Repository.DocumentId(EForgeProvider.GitHub, 910040),
        GitHubId = 910040, Name = "widget", FullName = "acme/widget", OwnerLogin = "acme",
        Account = installed ? AccountId : null,
    };

    private static GitHubForgeMergeQueue Queue(StubGitHub github, long? installationId = InstallationId)
    {
        var session = Substitute.For<IAsyncDocumentSession>();
        session.LoadAsync<Account>(AccountId, Arg.Any<CancellationToken>())
            .Returns(new Account { GitHubId = 77, Login = "acme", InstallationId = installationId });
        return new GitHubForgeMergeQueue(github, new SingleClientHttpFactory(github.Rest), session, NullLogger<GitHubForgeMergeQueue>.Instance);
    }

    private static HttpResponseMessage WithHeader(HttpResponseMessage response, string name, string value)
    {
        response.Headers.TryAddWithoutValidation(name, value);
        return response;
    }

    private static async Task<T> Throws<T>(Func<Task> act) where T : Exception
        => (await act.Should().ThrowAsync<T>()).Which;

    // ── Roles ────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("""{"permission":"admin","role_name":"admin"}""", EForgeRepositoryRole.Admin)]
    [InlineData("""{"permission":"write","role_name":"maintain"}""", EForgeRepositoryRole.Maintain)]
    [InlineData("""{"permission":"write","role_name":"write"}""", EForgeRepositoryRole.Write)]
    [InlineData("""{"permission":"read","role_name":"triage"}""", EForgeRepositoryRole.Triage)]
    [InlineData("""{"permission":"read","role_name":"read"}""", EForgeRepositoryRole.Read)]
    [InlineData("""{"permission":"none","role_name":"none"}""", EForgeRepositoryRole.None)]
    // A custom role carries its own name; its base permission decides.
    [InlineData("""{"permission":"write","role_name":"release-manager"}""", EForgeRepositoryRole.Write)]
    public async Task The_role_is_read_from_role_name_first(string payload, EForgeRepositoryRole expected)
    {
        var body = payload.Replace("}", ""","user":{"login":"octocat","id":1,"type":"User"}}""");
        var github = new StubGitHub().On(HttpMethod.Get, $"{Repo}/collaborators/octocat/permission", HttpStatusCode.OK, body);

        (await Queue(github).GetActorRoleAsync(Target(), "octocat")).Should().Be(expected);
    }

    [Fact]
    public async Task An_unknown_user_has_no_role()
    {
        var github = new StubGitHub();
        (await Queue(github).GetActorRoleAsync(Target(), "ghost")).Should().Be(EForgeRepositoryRole.None);
    }

    [Theory]
    [InlineData("a/b")]
    [InlineData("../admin")]
    [InlineData("dependabot[bot]")]
    [InlineData("")]
    public async Task A_login_github_cannot_have_is_none_without_asking(string login)
    {
        var github = new StubGitHub();
        (await Queue(github).GetActorRoleAsync(Target(), login)).Should().Be(EForgeRepositoryRole.None);
        github.Calls.Should().BeEmpty();
    }

    // ── Branch rules ─────────────────────────────────────────────────────────────────────────────

    /// <summary>The two rulesets of O9 as `GET /rules/branches/main` answered the App (live, D16).</summary>
    private const string TwoRulesets = """
        [
          {"type":"update","ruleset_source_type":"Repository","ruleset_source":"acme/widget","ruleset_id":25066850,
           "parameters":{"update_allows_fetch_and_merge":false}},
          {"type":"pull_request","ruleset_source_type":"Repository","ruleset_source":"acme/widget","ruleset_id":25066861,
           "parameters":{"required_approving_review_count":1,"dismiss_stale_reviews_on_push":true,"required_reviewers":[],
             "require_code_owner_review":false,"require_last_push_approval":true,"required_review_thread_resolution":false,
             "require_extra_approval_for_unattributed_changes":true,"allowed_merge_methods":["merge","squash","rebase"]}},
          {"type":"required_status_checks","ruleset_source_type":"Repository","ruleset_source":"acme/widget","ruleset_id":25066861,
           "parameters":{"strict_required_status_checks_policy":true,"do_not_enforce_on_create":false,"required_status_checks":[{"context":"ci"}]}}
        ]
        """;

    private static string Ruleset(long id, string name, string bypass) => $$"""
        {"id":{{id}},"name":"{{name}}","target":"branch","source_type":"Repository","source":"acme/widget",
         "enforcement":"active","current_user_can_bypass":"{{bypass}}","node_id":"RRS_lACqUmVwb3NpdG9yeQ",
         "conditions":{"ref_name":{"include":["~DEFAULT_BRANCH"],"exclude":[] } } }
        """;

    private const string UnprotectedBranch = """
        {"name":"main","commit":{"sha":"4a5b6c7d8e9f00112233445566778899aabbccdd","url":"https://api.github.com/repos/acme/widget/commits/4a5b6c7"},
         "protected":false,"protection":{"enabled":false,"required_status_checks":{"enforcement_level":"off","contexts":[],"checks":[]}},
         "protection_url":"https://api.github.com/repos/acme/widget/branches/main/protection"}
        """;

    [Fact]
    public async Task Rulesets_merge_into_one_answer_with_each_bypass()
    {
        var github = new StubGitHub()
            .On(HttpMethod.Get, $"{Repo}/rules/branches/main", HttpStatusCode.OK, TwoRulesets)
            .On(HttpMethod.Get, $"{Repo}/rulesets/25066850", HttpStatusCode.OK, Ruleset(25066850, "mq-1-restrict-updates", "exempt"))
            .On(HttpMethod.Get, $"{Repo}/rulesets/25066861", HttpStatusCode.OK, Ruleset(25066861, "mq-2-reviews-checks", "never"))
            .On(HttpMethod.Get, $"{Repo}/branches/main", HttpStatusCode.OK, UnprotectedBranch);

        var rules = await Queue(github).GetBranchRulesAsync(Target(), "main");

        rules.RulesetsAvailable.Should().BeTrue();
        rules.RequiredChecks.Should().Equal(["ci"]);
        rules.RequiredApprovals.Should().Be(1);
        rules.DismissStaleReviewsOnPush.Should().BeTrue();
        rules.RequireLastPushApproval.Should().BeTrue();
        rules.RequireUpToDateBranch.Should().BeTrue();
        rules.HasNativeMergeQueue.Should().BeFalse();
        rules.ClassicProtection.Should().Be(EForgeClassicProtection.None);
        rules.Rulesets.Should().HaveCount(2);
        rules.Rulesets[0].Id.Should().Be(25066850);
        rules.Rulesets[0].Name.Should().Be("mq-1-restrict-updates");
        rules.Rulesets[0].RuleTypes.Should().Equal(["update"]);
        rules.Rulesets[0].Bypass.Should().Be(EForgeRulesetBypass.Exempt);
        rules.Rulesets[1].Bypass.Should().Be(EForgeRulesetBypass.Never);
        rules.Rulesets[1].RuleTypes.Should().Equal(["pull_request", "required_status_checks"]);
    }

    [Fact]
    public async Task A_native_merge_queue_rule_is_reported()
    {
        var github = new StubGitHub()
            .On(HttpMethod.Get, $"{Repo}/rules/branches/main", HttpStatusCode.OK, """
                [{"type":"merge_queue","ruleset_source_type":"Repository","ruleset_source":"acme/widget","ruleset_id":7,
                  "parameters":{"merge_method":"SQUASH","grouping_strategy":"ALLGREEN","max_entries_to_build":5}}]
                """)
            .On(HttpMethod.Get, $"{Repo}/rulesets/7", HttpStatusCode.OK, Ruleset(7, "queue", "never"))
            .On(HttpMethod.Get, $"{Repo}/branches/main", HttpStatusCode.OK, UnprotectedBranch);

        (await Queue(github).GetBranchRulesAsync(Target(), "main")).HasNativeMergeQueue.Should().BeTrue();
    }

    [Fact]
    public async Task A_ruleset_that_cannot_be_read_has_an_unknown_bypass_not_never()
    {
        var github = new StubGitHub()
            .On(HttpMethod.Get, $"{Repo}/rules/branches/main", HttpStatusCode.OK, TwoRulesets)
            .On(HttpMethod.Get, $"{Repo}/rulesets/25066850", HttpStatusCode.OK, Ruleset(25066850, "mq-1-restrict-updates", "exempt"))
            .On(HttpMethod.Get, $"{Repo}/branches/main", HttpStatusCode.OK, UnprotectedBranch);

        var rules = await Queue(github).GetBranchRulesAsync(Target(), "main");

        rules.Rulesets[1].Bypass.Should().Be(EForgeRulesetBypass.Unknown);
    }

    /// <summary>D15: a private repository on the Free plan answers rules with 403 "Upgrade" (live, spikes_live.md S5).</summary>
    [Fact]
    public async Task A_plan_without_rulesets_reads_as_no_rules()
    {
        var github = new StubGitHub()
            .On(HttpMethod.Get, $"{Repo}/rules/branches/main", HttpStatusCode.Forbidden, """
                {"message":"Upgrade to GitHub Pro or make this repository public to enable this feature.","documentation_url":"https://docs.github.com/rest/repos/rules#get-rules-for-a-branch","status":"403"}
                """)
            .On(HttpMethod.Get, $"{Repo}/branches/main", HttpStatusCode.OK, UnprotectedBranch);

        var rules = await Queue(github).GetBranchRulesAsync(Target(), "main");

        rules.RulesetsAvailable.Should().BeFalse();
        rules.RequiredChecks.Should().BeEmpty();
        rules.Rulesets.Should().BeEmpty();
    }

    [Fact]
    public async Task Any_other_refusal_of_the_rules_is_an_access_failure()
    {
        var github = new StubGitHub()
            .On(HttpMethod.Get, $"{Repo}/rules/branches/main", (_, _) => WithHeader(StubHttpMessageHandler.Json(HttpStatusCode.Forbidden,
                """{"message":"Resource not accessible by integration","documentation_url":"https://docs.github.com/rest","status":"403"}"""),
                "X-Accepted-GitHub-Permissions", "metadata=read"));

        var thrown = await Throws<ForgeAccessDeniedException>(() => Queue(github).GetBranchRulesAsync(Target(), "main"));
        thrown.RequiredPermission.Should().Be("metadata=read");
    }

    [Fact]
    public async Task Classic_protection_without_administration_gives_the_checks_only()
    {
        var github = new StubGitHub()
            .On(HttpMethod.Get, $"{Repo}/rules/branches/main", HttpStatusCode.OK, "[]")
            .On(HttpMethod.Get, $"{Repo}/branches/main", HttpStatusCode.OK, """
                {"name":"main","protected":true,
                 "protection":{"enabled":true,"required_status_checks":{"enforcement_level":"non_admins","contexts":["build"],"checks":[{"context":"build","app_id":15368}]}}}
                """)
            .On(HttpMethod.Get, $"{Repo}/branches/main/protection", HttpStatusCode.Forbidden,
                """{"message":"Resource not accessible by integration","documentation_url":"https://docs.github.com/rest","status":"403"}""");

        var rules = await Queue(github).GetBranchRulesAsync(Target(), "main");

        rules.ClassicProtection.Should().Be(EForgeClassicProtection.ChecksOnly);
        rules.RequiredChecks.Should().Equal(["build"]);
    }

    [Fact]
    public async Task Readable_classic_protection_adds_its_reviews()
    {
        var github = new StubGitHub()
            .On(HttpMethod.Get, $"{Repo}/rules/branches/release/1.x", HttpStatusCode.OK, "[]")
            .On(HttpMethod.Get, $"{Repo}/branches/release/1.x", HttpStatusCode.OK, """{"name":"release/1.x","protected":true,"protection":{"enabled":true}}""")
            .On(HttpMethod.Get, $"{Repo}/branches/release/1.x/protection", HttpStatusCode.OK, """
                {"required_status_checks":{"strict":true,"contexts":["test"],"checks":[{"context":"test","app_id":null}]},
                 "required_pull_request_reviews":{"dismiss_stale_reviews":false,"require_code_owner_reviews":false,"required_approving_review_count":2,"require_last_push_approval":false},
                 "enforce_admins":{"enabled":false}}
                """);

        var rules = await Queue(github).GetBranchRulesAsync(Target(), "release/1.x");

        rules.ClassicProtection.Should().Be(EForgeClassicProtection.Read);
        rules.RequiredApprovals.Should().Be(2);
        rules.RequiredChecks.Should().Equal(["test"]);
        rules.RequireUpToDateBranch.Should().BeTrue();
    }

    // ── Checks ───────────────────────────────────────────────────────────────────────────────────

    private static string CheckRun(long id, string name, string status, string? conclusion, long suite = 11) => $$"""
        {"id":{{id}},"name":"{{name}}","head_sha":"{{Head}}","status":"{{status}}","conclusion":{{(conclusion is null ? "null" : $"\"{conclusion}\"")}},
         "details_url":"https://github.com/acme/widget/actions/runs/1/job/{{id}}","html_url":"https://github.com/acme/widget/runs/{{id}}",
         "app":{"id":15368,"slug":"github-actions","name":"GitHub Actions"},"check_suite":{"id":{{suite}} } }
        """;

    private static StubGitHub WithChecks(StubGitHub github, string runs, string statuses = "[]", string suites = """[{"id":11,"status":"completed","conclusion":"success","app":{"slug":"github-actions"},"latest_check_runs_count":1,"head_sha":"4a5b6c7d8e9f00112233445566778899aabbccdd"}]""")
        => github
            .On(HttpMethod.Get, $"{Repo}/commits/{Head}/check-runs?filter=latest&per_page=100&page=1", HttpStatusCode.OK, $$"""{"total_count":1,"check_runs":{{runs}}}""")
            .On(HttpMethod.Get, $"{Repo}/commits/{Head}/status?per_page=100&page=1", HttpStatusCode.OK, $$"""{"state":"success","sha":"{{Head}}","total_count":1,"statuses":{{statuses}}}""")
            .On(HttpMethod.Get, $"{Repo}/commits/{Head}/check-suites?per_page=100&page=1", HttpStatusCode.OK, $$"""{"total_count":1,"check_suites":{{suites}}}""");

    [Fact]
    public async Task Check_runs_statuses_and_suites_are_read_together()
    {
        var github = WithChecks(new StubGitHub(),
            $"[{CheckRun(1, "ci", "completed", "success")},{CheckRun(2, "regenerate", "in_progress", null)}]",
            """[{"context":"GitGuardian Security Checks","state":"failure","target_url":"https://dashboard.gitguardian.com/x","description":"1 secret"}]""");

        var state = await Queue(github).GetCheckStateAsync(Target(), Head);

        state.CheckRuns.Should().HaveCount(2);
        state.CheckRuns[0].Should().Be(new ForgeCheckRun(1, "ci", "completed", "success", 11, "github-actions", "https://github.com/acme/widget/actions/runs/1/job/1"));
        state.CheckRuns[1].Conclusion.Should().BeNull();
        state.Statuses.Should().Equal([new ForgeCommitStatus("GitGuardian Security Checks", "failure", "https://dashboard.gitguardian.com/x")]);
        state.Suites.Should().Equal([new ForgeCheckSuite(11, "completed", "success", "github-actions", 1)]);
    }

    [Fact]
    public async Task A_full_page_is_followed_by_the_next()
    {
        var firstPage = string.Join(",", Enumerable.Range(1, 100).Select(i => CheckRun(i, $"job-{i:000}", "completed", "success")));
        var github = WithChecks(new StubGitHub(), $"[{firstPage}]")
            .On(HttpMethod.Get, $"{Repo}/commits/{Head}/check-runs?filter=latest&per_page=100&page=2", HttpStatusCode.OK,
                $$"""{"total_count":101,"check_runs":[{{CheckRun(101, "job-101", "completed", "success")}}]}""");

        var state = await Queue(github).GetCheckStateAsync(Target(), Head);

        state.CheckRuns.Should().HaveCount(101);
    }

    [Fact]
    public async Task The_expected_minimum_set_is_every_name_once_without_our_own_check()
    {
        var github = WithChecks(new StubGitHub(),
            $"[{CheckRun(1, "ci", "completed", "success")},{CheckRun(2, CheckGate.QueueCheckName, "completed", "success")},{CheckRun(3, "build", "completed", "failure")}]",
            """[{"context":"ci","state":"success","target_url":null},{"context":"GitGuardian Security Checks","state":"success","target_url":null}]""");

        (await Queue(github).GetCheckNamesOnCommitAsync(Target(), Head))
            .Should().Equal(["GitGuardian Security Checks", "build", "ci"]);
    }

    [Fact]
    public async Task A_sha_that_is_not_one_is_refused_before_any_call()
    {
        var github = new StubGitHub();
        await Throws<ArgumentException>(() => Queue(github).GetCheckStateAsync(Target(), "../../user"));
        github.Calls.Should().BeEmpty();
    }

    // ── Dispatch and runs ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_dispatch_asks_for_the_run_and_returns_it()
    {
        string? posted = null;
        var github = new StubGitHub().On(HttpMethod.Post, $"{Repo}/actions/workflows/regenerate.yml/dispatches", (_, body) =>
        {
            posted = body;
            // Live (spikes_live.md S2a).
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"workflow_run_id":38538777260,"run_url":"https://api.github.com/repos/acme/widget/actions/runs/38538777260","html_url":"https://github.com/acme/widget/actions/runs/38538777260"}
                """);
        });

        var dispatch = await Queue(github).DispatchWorkflowAsync(Target(), "regenerate.yml", "mq/main/pr-1",
            new Dictionary<string, string> { ["expected_sha"] = Head });

        dispatch.Should().Be(new ForgeWorkflowDispatch(EForgeDispatchOutcome.Dispatched, 38538777260, "https://github.com/acme/widget/actions/runs/38538777260", null));
        using var json = JsonDocument.Parse(posted!);
        json.RootElement.GetProperty("ref").GetString().Should().Be("mq/main/pr-1");
        json.RootElement.GetProperty("return_run_details").GetBoolean().Should().BeTrue();
        json.RootElement.GetProperty("inputs").GetProperty("expected_sha").GetString().Should().Be(Head);
    }

    [Theory]
    // Live: a workflow only on the mq/ ref is never registered (S2 c1)...
    [InlineData(HttpStatusCode.NotFound, """{"message":"Not Found","documentation_url":"https://docs.github.com/rest/actions/workflows#create-a-workflow-dispatch-event","status":"404"}""", EForgeDispatchOutcome.WorkflowNotFound, "Not Found")]
    // ...and one whose file at the ref lost its trigger is refused (S2 c2).
    [InlineData(HttpStatusCode.UnprocessableEntity, """{"message":"Workflow does not have 'workflow_dispatch' trigger","documentation_url":"https://docs.github.com/rest/actions/workflows#create-a-workflow-dispatch-event","status":"422"}""", EForgeDispatchOutcome.Refused, "Workflow does not have 'workflow_dispatch' trigger")]
    public async Task A_dispatch_GitHub_cannot_run_is_an_outcome(HttpStatusCode status, string body, EForgeDispatchOutcome outcome, string message)
    {
        var github = new StubGitHub().On(HttpMethod.Post, $"{Repo}/actions/workflows/regenerate.yml/dispatches", status, body);

        var dispatch = await Queue(github).DispatchWorkflowAsync(Target(), "regenerate.yml", "mq/main/pr-1", new Dictionary<string, string>());

        dispatch.Outcome.Should().Be(outcome);
        dispatch.Message.Should().Be(message);
        dispatch.RunId.HasValue.Should().BeFalse();
    }

    [Fact]
    public async Task A_dispatch_without_run_details_is_dispatched_but_uncorrelated()
    {
        var github = new StubGitHub().On(HttpMethod.Post, $"{Repo}/actions/workflows/regenerate.yml/dispatches",
            (_, _) => new HttpResponseMessage(HttpStatusCode.NoContent));

        var dispatch = await Queue(github).DispatchWorkflowAsync(Target(), "regenerate.yml", "mq/main/pr-1", new Dictionary<string, string>());

        dispatch.Should().Be(new ForgeWorkflowDispatch(EForgeDispatchOutcome.Dispatched, null, null, null));
    }

    [Fact]
    public async Task A_dispatch_refused_for_a_permission_names_it()
    {
        var github = new StubGitHub().On(HttpMethod.Post, $"{Repo}/actions/workflows/regenerate.yml/dispatches",
            (_, _) => WithHeader(StubHttpMessageHandler.Json(HttpStatusCode.Forbidden,
                """{"message":"Resource not accessible by integration","documentation_url":"https://docs.github.com/rest","status":"403"}"""),
                "X-Accepted-GitHub-Permissions", "actions=write"));

        var thrown = await Throws<ForgeAccessDeniedException>(() =>
            Queue(github).DispatchWorkflowAsync(Target(), "regenerate.yml", "mq/main/pr-1", new Dictionary<string, string>()));

        thrown.RequiredPermission.Should().Be("actions=write");
    }

    [Fact]
    public async Task A_rate_limit_is_retryable_not_a_refusal()
    {
        var github = new StubGitHub().On(HttpMethod.Get, $"{Repo}/actions/runs/1",
            (_, _) => WithHeader(StubHttpMessageHandler.Json(HttpStatusCode.Forbidden,
                """{"message":"API rate limit exceeded for installation ID 4242.","documentation_url":"https://docs.github.com/rest/overview/resources-in-the-rest-api#rate-limiting"}"""),
                "x-ratelimit-remaining", "0"));

        var thrown = await Throws<ForgeRequestException>(() => Queue(github).GetWorkflowRunAsync(Target(), 1));
        thrown.StatusCode.Should().Be(429);
    }

    [Theory]
    [InlineData("../ci.yml")]
    [InlineData("ci.sh")]
    [InlineData("sub/ci.yml")]
    public async Task A_workflow_that_is_not_a_file_name_is_refused(string file)
    {
        var github = new StubGitHub();
        await Throws<ArgumentException>(() =>
            Queue(github).DispatchWorkflowAsync(Target(), file, "mq/main/pr-1", new Dictionary<string, string>()));
        github.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task A_run_reports_the_sha_it_built()
    {
        var github = new StubGitHub().On(HttpMethod.Get, $"{Repo}/actions/runs/38538867631", HttpStatusCode.OK, """
            {"id":38538867631,"name":"regenerate","head_branch":"mq/main/pr-1","head_sha":"f4fe1f2c0ffee0000000000000000000000000aa",
             "path":".github/workflows/regenerate.yml","event":"workflow_dispatch","status":"completed","conclusion":"success",
             "html_url":"https://github.com/acme/widget/actions/runs/38538867631","pull_requests":[]}
            """);

        var run = await Queue(github).GetWorkflowRunAsync(Target(), 38538867631);

        run.Should().Be(new ForgeWorkflowRun(38538867631, "completed", "success", "f4fe1f2c0ffee0000000000000000000000000aa", "mq/main/pr-1",
            "https://github.com/acme/widget/actions/runs/38538867631"));
        run!.IsCompleted.Should().BeTrue();
    }

    [Fact]
    public async Task A_missing_run_is_null()
        => (await Queue(new StubGitHub()).GetWorkflowRunAsync(Target(), 1)).Should().BeNull();

    // ── Branch heads and re-runs (M5) ────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_branch_head_is_read_from_its_one_ref()
    {
        var github = new StubGitHub().On(HttpMethod.Get, $"{Repo}/git/ref/heads/release/2.x", HttpStatusCode.OK, $$$"""
            {"ref":"refs/heads/release/2.x","node_id":"REF_x","url":"https://api.github.com/repos/acme/widget/git/refs/heads/release/2.x",
             "object":{"sha":"{{{Head}}}","type":"commit","url":"https://api.github.com/repos/acme/widget/git/commits/{{{Head}}}"}}
            """);

        (await Queue(github).GetBranchHeadAsync(Target(), "release/2.x")).Should().Be(Head);
    }

    [Fact]
    public async Task A_missing_branch_has_no_head()
        => (await Queue(new StubGitHub()).GetBranchHeadAsync(Target(), "gone")).Should().BeNull();

    [Fact]
    public async Task An_actions_check_reruns_its_job_and_another_apps_check_is_rerequested()
    {
        var github = new StubGitHub()
            .On(HttpMethod.Post, $"{Repo}/actions/jobs/501/rerun", HttpStatusCode.Created, "{}")
            .On(HttpMethod.Post, $"{Repo}/check-runs/502/rerequest", HttpStatusCode.Created, "{}");
        var queue = Queue(github);

        (await queue.RerunCheckAsync(Target(), new ForgeCheckRun(501, "build", "completed", "failure", 11, "github-actions", null))).Should().BeTrue();
        (await queue.RerunCheckAsync(Target(), new ForgeCheckRun(502, "scan", "completed", "failure", 12, "scanner", null))).Should().BeTrue();
    }

    [Fact]
    public async Task A_check_that_cannot_be_rerun_is_false_and_a_missing_permission_throws()
    {
        var github = new StubGitHub()
            .On(HttpMethod.Post, $"{Repo}/actions/jobs/503/rerun", HttpStatusCode.UnprocessableEntity, """{"message":"This job is too old to re-run."}""")
            .On(HttpMethod.Post, $"{Repo}/actions/jobs/504/rerun", (_, _) => WithHeader(
                new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("""{"message":"Resource not accessible by integration"}""") },
                "X-Accepted-GitHub-Permissions", "actions=write"));
        var queue = Queue(github);

        (await queue.RerunCheckAsync(Target(), new ForgeCheckRun(503, "build", "completed", "failure", 11, "github-actions", null))).Should().BeFalse();
        (await Throws<ForgeAccessDeniedException>(() => queue.RerunCheckAsync(Target(), new ForgeCheckRun(504, "build", "completed", "failure", 11, "github-actions", null))))
            .RequiredPermission.Should().Be("actions=write");
    }

    // ── Artifacts ────────────────────────────────────────────────────────────────────────────────

    private static byte[] Zip(params (string Name, byte[] Content)[] entries)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                using var stream = zip.CreateEntry(name).Open();
                stream.Write(content);
            }
        }
        return buffer.ToArray();
    }

    private static string Artifacts(long size, bool expired = false) => $$"""
        {"total_count":1,"artifacts":[{"id":5,"node_id":"MDg6QXJ0aWZhY3Q1","name":"merge-queue-patch","size_in_bytes":{{size}},
          "url":"https://api.github.com/repos/acme/widget/actions/artifacts/5",
          "archive_download_url":"https://api.github.com/repos/acme/widget/actions/artifacts/5/zip",
          "expired":{{(expired ? "true" : "false")}},"digest":"sha256:0d1f","created_at":"2026-10-14T08:58:10Z","expires_at":"2027-01-12T08:58:01Z"}]}
        """;

    private static StubGitHub WithArtifact(byte[] zip, bool expired = false)
        => new StubGitHub()
            .On(HttpMethod.Get, $"{Repo}/actions/runs/38538777260/artifacts?name=merge-queue-patch&per_page=100&page=1", HttpStatusCode.OK, Artifacts(zip.Length, expired))
            // Live (S2d): the archive URL redirects to signed blob storage.
            .On(HttpMethod.Get, $"{Repo}/actions/artifacts/5/zip", (_, _) =>
            {
                var redirect = new HttpResponseMessage(HttpStatusCode.Found);
                redirect.Headers.Location = new Uri("https://blob.example.net/artifact-5.zip?sig=x");
                return redirect;
            })
            .On(HttpMethod.Get, "/artifact-5.zip", (_, _) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(zip) });

    [Fact]
    public async Task The_artifacts_one_file_is_returned_and_the_blob_gets_no_credential()
    {
        var patch = Encoding.UTF8.GetBytes("diff --git a/App_Data/modelHashes.json b/App_Data/modelHashes.json\n");
        var github = WithArtifact(Zip(("patch.txt", patch)));

        var download = await Queue(github).DownloadArtifactAsync(Target(), 38538777260, "merge-queue-patch", 1024 * 1024);

        download.Outcome.Should().Be(EForgeArtifactOutcome.Ok, download.Detail);
        download.FileName.Should().Be("patch.txt");
        download.Content.Should().Equal(patch);
        var requests = github.Rest.Requests.ToList();
        requests.Single(r => r.Uri!.Host == "blob.example.net").AuthorizationParameter.Should().BeNull();
        requests.Single(r => r.Uri!.AbsolutePath.EndsWith("/zip")).AuthorizationParameter.Should().Be(StubGitHub.InstallationToken);
    }

    [Fact]
    public async Task An_artifact_declared_too_large_is_never_downloaded()
    {
        var github = new StubGitHub()
            .On(HttpMethod.Get, $"{Repo}/actions/runs/1/artifacts?name=merge-queue-patch&per_page=100&page=1", HttpStatusCode.OK, Artifacts(5_000_000));

        var download = await Queue(github).DownloadArtifactAsync(Target(), 1, "merge-queue-patch", 2 * 1024 * 1024);

        download.Outcome.Should().Be(EForgeArtifactOutcome.TooLarge);
        github.Calls.Should().NotContain(call => call.Contains("/zip"));
    }

    [Fact]
    public async Task An_archive_larger_than_it_was_declared_is_cut_off()
    {
        var big = new byte[64 * 1024];
        Random.Shared.NextBytes(big);
        var zip = Zip(("patch.txt", big));
        var github = new StubGitHub()
            .On(HttpMethod.Get, $"{Repo}/actions/runs/1/artifacts?name=merge-queue-patch&per_page=100&page=1", HttpStatusCode.OK, Artifacts(100))
            .On(HttpMethod.Get, $"{Repo}/actions/artifacts/5/zip", (_, _) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(zip) });

        (await Queue(github).DownloadArtifactAsync(Target(), 1, "merge-queue-patch", 16 * 1024)).Outcome.Should().Be(EForgeArtifactOutcome.TooLarge);
    }

    [Fact]
    public async Task An_expired_artifact_is_reported_as_such()
    {
        var github = WithArtifact(Zip(("patch.txt", [1])), expired: true);
        (await Queue(github).DownloadArtifactAsync(Target(), 38538777260, "merge-queue-patch", 1024)).Outcome.Should().Be(EForgeArtifactOutcome.Expired);
    }

    [Fact]
    public async Task A_run_without_the_artifact_is_not_found()
    {
        var github = new StubGitHub()
            .On(HttpMethod.Get, $"{Repo}/actions/runs/1/artifacts?name=merge-queue-patch&per_page=100&page=1", HttpStatusCode.OK, """{"total_count":0,"artifacts":[]}""");
        (await Queue(github).DownloadArtifactAsync(Target(), 1, "merge-queue-patch", 1024)).Outcome.Should().Be(EForgeArtifactOutcome.NotFound);
    }

    [Fact]
    public void A_zip_bomb_is_refused_while_inflating()
    {
        // 8 MiB of zeros compresses to a few KiB: the archive passes its cap, the file does not.
        var zip = Zip(("patch.txt", new byte[8 * 1024 * 1024]));
        zip.Length.Should().BeLessThan(64 * 1024);

        GitHubForgeMergeQueue.ReadSingleFile(zip, 64 * 1024).Outcome.Should().Be(EForgeArtifactOutcome.TooLarge);
    }

    [Fact]
    public void An_archive_of_two_files_is_invalid_and_directories_do_not_count()
    {
        GitHubForgeMergeQueue.ReadSingleFile(Zip(("a.txt", [1]), ("b.txt", [2])), 1024).Outcome.Should().Be(EForgeArtifactOutcome.Invalid);
        GitHubForgeMergeQueue.ReadSingleFile(Zip(("out/", []), ("out/patch.txt", [7])), 1024).Content.Should().Equal([(byte)7]);
    }

    [Fact]
    public void Bytes_that_are_not_a_zip_are_invalid()
        => GitHubForgeMergeQueue.ReadSingleFile(Encoding.UTF8.GetBytes("<html>proxy error</html>"), 1024)
            .Outcome.Should().Be(EForgeArtifactOutcome.Invalid);

    // ── Workflow file identity (D12) ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_files_blob_sha_is_read_at_a_ref()
    {
        var github = new StubGitHub().On(HttpMethod.Get, $"{Repo}/contents/.github/workflows/regenerate.yml?ref=mq%2Fmain%2Fpr-1", HttpStatusCode.OK, """
            {"name":"regenerate.yml","path":".github/workflows/regenerate.yml","sha":"3d21ec53a331a6f037a91c368710b99387d012c1","size":512,
             "type":"file","encoding":"base64","content":"bmFtZTogcmVnZW5lcmF0ZQo=",
             "url":"https://api.github.com/repos/acme/widget/contents/.github/workflows/regenerate.yml?ref=mq/main/pr-1"}
            """);

        (await Queue(github).GetFileBlobShaAsync(Target(), "mq/main/pr-1", ".github/workflows/regenerate.yml"))
            .Should().Be("3d21ec53a331a6f037a91c368710b99387d012c1");
    }

    [Fact]
    public async Task A_directory_or_a_missing_file_has_no_blob()
    {
        var github = new StubGitHub().On(HttpMethod.Get, $"{Repo}/contents/.github/workflows?ref=main", HttpStatusCode.OK, """[{"name":"ci.yml","type":"file","sha":"aa"}]""");
        var queue = Queue(github);

        (await queue.GetFileBlobShaAsync(Target(), "main", ".github/workflows")).Should().BeNull();
        (await queue.GetFileBlobShaAsync(Target(), "main", ".github/workflows/gone.yml")).Should().BeNull();
    }

    [Fact]
    public async Task A_path_that_climbs_is_refused()
        => await Throws<ArgumentException>(() => Queue(new StubGitHub()).GetFileBlobShaAsync(Target(), "main", ".github/../secrets"));

    // ── The merge (D13, D17) ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_merge_request_is_a_direct_squash_that_never_bypasses_and_names_its_api_version()
    {
        string? posted = null;
        var github = new StubGitHub().On(HttpMethod.Put, $"{Repo}/pulls/1/merge-async", (_, body) =>
        {
            posted = body;
            // Live (spikes_app.md h1-retry).
            return StubHttpMessageHandler.Json(HttpStatusCode.Accepted, $$"""
                {"status":"pending","details":{"message":"Merge request enqueued.","uuid":"{{Uuid}}","merge_method":"squash","merge_action":"direct_merge","expected_head_sha":"{{Head}}","bypass_rules":false } }
                """);
        });

        var request = await Queue(github).RequestMergeAsync(Target(), 1, Head, EForgeMergeMethod.Squash, "Add widget (#1)", "body");

        request.Should().Be(new ForgeMergeRequest(EForgeMergeState.Pending, Uuid, null, "Merge request enqueued."));
        using var json = JsonDocument.Parse(posted!);
        json.RootElement.GetProperty("sha").GetString().Should().Be(Head);
        json.RootElement.GetProperty("merge_method").GetString().Should().Be("squash");
        json.RootElement.GetProperty("merge_action").GetString().Should().Be("direct_merge");
        json.RootElement.GetProperty("bypass_rules").GetBoolean().Should().BeFalse();
        json.RootElement.GetProperty("commit_title").GetString().Should().Be("Add widget (#1)");
        github.ApiVersions.Single(v => v.Call == $"PUT {Repo}/pulls/1/merge-async").ApiVersion.Should().Be(GitHubForgeMergeQueue.MergeAsyncApiVersion);
    }

    [Fact]
    public async Task A_stale_head_is_its_own_state()
    {
        // Live (spikes_live.md S5): synchronous, nothing queued.
        var github = new StubGitHub().On(HttpMethod.Put, $"{Repo}/pulls/1/merge-async", HttpStatusCode.BadRequest,
            """{"status":"failed","details":{"message":"Pull request head branch was modified."}}""");

        (await Queue(github).RequestMergeAsync(Target(), 1, Head, EForgeMergeMethod.Squash, null, null))
            .Should().Be(new ForgeMergeRequest(EForgeMergeState.HeadModified, null, null, "Pull request head branch was modified."));
    }

    [Fact]
    public async Task A_repeat_of_a_merged_request_answers_merged_with_the_commit()
    {
        // Live (spikes_live.md S5): repeating is safe.
        var github = new StubGitHub().On(HttpMethod.Put, $"{Repo}/pulls/1/merge-async", HttpStatusCode.OK,
            $$"""{"status":"merged","details":{"message":"Pull request is already merged.","sha":"{{MergeSha}}" } }""");

        (await Queue(github).RequestMergeAsync(Target(), 1, Head, EForgeMergeMethod.Squash, null, null))
            .Should().Be(new ForgeMergeRequest(EForgeMergeState.Merged, null, MergeSha, "Pull request is already merged."));
    }

    [Fact]
    public async Task A_request_already_pending_returns_its_id()
    {
        // 409 per the docs: "a merge request is already pending", with its uuid. Not reproduced live.
        var github = new StubGitHub().On(HttpMethod.Put, $"{Repo}/pulls/1/merge-async", HttpStatusCode.Conflict,
            $$"""{"status":"pending","details":{"message":"A merge request is already pending.","uuid":"{{Uuid}}" } }""");

        var request = await Queue(github).RequestMergeAsync(Target(), 1, Head, EForgeMergeMethod.Squash, null, null);

        request.State.Should().Be(EForgeMergeState.Pending);
        request.RequestId.Should().Be(Uuid);
    }

    [Theory]
    // Live (spikes_app.md st1): rule failures arrive here, after a 202.
    [InlineData("""{"status":"failed","details":{"message":"Repository rule violations found\n\nRequired status check \"ci\" is queued.\n\n"}}""",
        EForgeMergeState.Failed, null, "Repository rule violations found\n\nRequired status check \"ci\" is queued.\n\n")]
    [InlineData("""{"status":"merged","details":{"message":"Pull request was merged.","sha":"85b3ffa3488c6c6fcffb1576054a5fec946ef318"}}""",
        EForgeMergeState.Merged, MergeSha, "Pull request was merged.")]
    public async Task The_status_of_a_request_is_polled_by_its_id(string body, EForgeMergeState state, string? sha, string message)
    {
        var github = new StubGitHub().On(HttpMethod.Get, $"{Repo}/pulls/1/merge-async/{Uuid}", HttpStatusCode.OK, body);

        var status = await Queue(github).GetMergeRequestStatusAsync(Target(), 1, Uuid);

        status.Should().Be(new ForgeMergeRequest(state, null, sha, message));
        github.ApiVersions.Single().ApiVersion.Should().Be(GitHubForgeMergeQueue.MergeAsyncApiVersion);
    }

    [Fact]
    public async Task A_still_pending_poll_keeps_its_id()
    {
        var github = new StubGitHub().On(HttpMethod.Get, $"{Repo}/pulls/1/merge-async/{Uuid}", HttpStatusCode.OK,
            """{"status":"pending","details":{"message":"Merge request enqueued."}}""");

        (await Queue(github).GetMergeRequestStatusAsync(Target(), 1, Uuid)).RequestId.Should().Be(Uuid);
    }

    [Fact]
    public async Task An_unknown_status_word_is_never_read_as_a_merge()
    {
        var github = new StubGitHub().On(HttpMethod.Get, $"{Repo}/pulls/1/merge-async/{Uuid}", HttpStatusCode.OK,
            """{"status":"reticulating","details":{"message":"?"}}""");

        (await Queue(github).GetMergeRequestStatusAsync(Target(), 1, Uuid)).State.Should().Be(EForgeMergeState.Failed);
    }

    [Fact]
    public async Task A_request_id_that_is_not_one_is_refused()
        => await Throws<ArgumentException>(() => Queue(new StubGitHub()).GetMergeRequestStatusAsync(Target(), 1, "../../x"));

    // ── Reviews ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Reviews_carry_their_commit_and_reviewer()
    {
        var github = new StubGitHub().On(HttpMethod.Get, $"{Repo}/pulls/1/reviews?per_page=100&page=1", HttpStatusCode.OK, $$"""
            [{"id":80,"node_id":"PRR_kwDOA","user":{"login":"reviewer","id":3,"type":"User"},"body":"","state":"APPROVED",
              "commit_id":"{{Head}}","submitted_at":"2026-10-14T09:20:00Z","author_association":"OWNER"},
             {"id":81,"user":{"login":"reviewer","id":3,"type":"User"},"body":"","state":"DISMISSED","commit_id":"0f1e2d3c4b5a69788796a5b4c3d2e1f00f1e2d3c",
              "submitted_at":"2026-10-14T09:10:00Z","author_association":"OWNER"}]
            """);

        var reviews = await Queue(github).GetReviewsAsync(Target(), 1);

        reviews.Should().HaveCount(2);
        reviews[0].Should().Be(new ForgeReview(80, "reviewer", EForgeReviewState.Approved, Head, new DateTimeOffset(2026, 10, 14, 9, 20, 0, TimeSpan.Zero)));
        reviews[1].State.Should().Be(EForgeReviewState.Dismissed);
    }

    // ── Feedback ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_queue_check_is_created_under_its_name_with_a_details_link()
    {
        string? posted = null;
        var github = new StubGitHub().On(HttpMethod.Post, $"{Repo}/check-runs", (_, body) =>
        {
            posted = body;
            return StubHttpMessageHandler.Json(HttpStatusCode.Created, $$"""
                {"id":42,"name":"Merge queue","head_sha":"{{Head}}","status":"queued","conclusion":null,"html_url":"https://github.com/acme/widget/runs/42"}
                """);
        });

        var id = await Queue(github).PublishQueueCheckAsync(Target(), Head,
            new ForgeQueueCheck(EForgeQueueCheckState.Queued, "Position 2 of 3", "Waiting for #4.", "https://coverage.example.com/github/acme/widget/queue"), null);

        id.Should().Be(42);
        using var json = JsonDocument.Parse(posted!);
        json.RootElement.GetProperty("name").GetString().Should().Be(CheckGate.QueueCheckName);
        json.RootElement.GetProperty("status").GetString().Should().Be("queued");
        json.RootElement.GetProperty("details_url").GetString().Should().Be("https://coverage.example.com/github/acme/widget/queue");
        json.RootElement.TryGetProperty("conclusion", out var conclusion).Should().BeFalse(conclusion.ToString());
    }

    [Fact]
    public async Task A_details_link_that_is_not_https_is_refused()
        => await Throws<ArgumentException>(() => Queue(new StubGitHub()).PublishQueueCheckAsync(Target(), Head,
            new ForgeQueueCheck(EForgeQueueCheckState.Failure, "t", "s", "javascript:alert(1)"), null));

    [Fact]
    public async Task A_label_that_is_already_gone_is_not_an_error()
    {
        var github = new StubGitHub()
            .On(HttpMethod.Delete, $"{Repo}/issues/1/labels/merge-queue", HttpStatusCode.NotFound,
                """{"message":"Label does not exist","documentation_url":"https://docs.github.com/rest/issues/labels#remove-a-label-from-an-issue","status":"404"}""")
            .On(HttpMethod.Post, $"{Repo}/issues/1/labels", HttpStatusCode.OK, """[{"id":1,"node_id":"LA_1","name":"merge-queue","color":"ededed","default":false}]""")
            .On(HttpMethod.Post, $"{Repo}/issues/1/comments", HttpStatusCode.Created,
                """{"id":99,"node_id":"IC_1","body":"Queued.","user":{"login":"coverage[bot]","id":5,"type":"Bot"},"html_url":"https://github.com/acme/widget/pull/1#issuecomment-99"}""");
        var queue = Queue(github);

        (await queue.RemoveLabelAsync(Target(), 1, "merge-queue")).Should().BeFalse();
        await queue.AddLabelAsync(Target(), 1, "merge-queue");
        (await queue.PostCommentAsync(Target(), 1, "Queued.")).Should().Be(99);
    }

    // ── The writer's credential (O3) ─────────────────────────────────────────────────────────────

    private static ForgePullRequest Pull(long? headRepositoryId, string? headFullName, bool maintainerCanModify)
        => new(1, Head, "feature", headRepositoryId, "main", 910040, "main", true)
        {
            HeadRepositoryFullName = headFullName,
            MaintainerCanModify = maintainerCanModify,
        };

    [Fact]
    public async Task A_same_repository_pull_request_pushes_to_the_repository()
    {
        var github = new StubGitHub();
        var push = await Queue(github).GetPushAccessAsync(Target(), Pull(910040, "acme/widget", false));

        push.RefusalReason.Should().BeNull();
        push.Access!.CloneUrl.Should().Be("https://github.com/acme/widget.git");
        push.Access.Username.Should().Be("x-access-token");
        push.Access.Password.Should().Be(StubGitHub.InstallationToken);
        github.InstallationClients.Should().Equal(InstallationId);
    }

    [Fact]
    public async Task A_fork_that_allows_maintainer_edits_pushes_to_the_fork_with_the_base_installation()
    {
        var push = await Queue(new StubGitHub()).GetPushAccessAsync(Target(), Pull(777, "contributor/widget", true));

        push.Access!.CloneUrl.Should().Be("https://github.com/contributor/widget.git");
        push.Access.InstallationKey.Should().Be($"github:{InstallationId}");
        push.Access.ToString().Should().NotContain(StubGitHub.InstallationToken);
    }

    [Fact]
    public async Task A_fork_that_does_not_allow_maintainer_edits_is_refused_without_a_token()
    {
        var github = new StubGitHub();
        var push = await Queue(github).GetPushAccessAsync(Target(), Pull(777, "contributor/widget", false));

        push.Access.Should().BeNull();
        push.RefusalReason.Should().Contain("Allow edits by maintainers");
        github.InstallationClients.Should().BeEmpty();
    }

    [Fact]
    public async Task A_deleted_fork_is_refused()
        => (await Queue(new StubGitHub()).GetPushAccessAsync(Target(), Pull(null, null, true)))
            .RefusalReason.Should().Contain("no longer exists");

    [Fact]
    public async Task A_fork_name_github_cannot_have_is_refused()
        => (await Queue(new StubGitHub()).GetPushAccessAsync(Target(), Pull(777, "evil.example.com/x/../y", true)))
            .Access.Should().BeNull();

    // ── No installation ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Without_an_installation_every_call_is_an_access_failure_and_nothing_is_sent()
    {
        var github = new StubGitHub();
        var queue = Queue(github, installationId: null);

        await Throws<ForgeAccessDeniedException>(() => queue.GetActorRoleAsync(Target(), "octocat"));
        await Throws<ForgeAccessDeniedException>(() => queue.RequestMergeAsync(Target(), 1, Head, EForgeMergeMethod.Squash, null, null));
        await Throws<ForgeAccessDeniedException>(() => queue.AddLabelAsync(Target(), 1, "merge-queue"));
        (await queue.GetPushAccessAsync(Target(), Pull(910040, "acme/widget", false))).Access.Should().BeNull();
        github.Calls.Should().BeEmpty();
    }
}
