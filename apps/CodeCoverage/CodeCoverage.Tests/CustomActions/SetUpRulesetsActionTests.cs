using CodeCoverage.CustomActions;
using CodeCoverage.Dependencies.Onboarding;
using CodeCoverage.Entities;
using CodeCoverage.Forge;
using CodeCoverage.MergeQueue;
using CodeCoverage.Tests.CommitGraph;
using CodeCoverage.Tests.Dependencies.Lifecycle;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Actions;
using MintPlayer.Spark.Abstractions.ClientOperations;
using MintPlayer.Spark.Abstractions.Retry;
using MintPlayer.Spark.Authorization.OneShotOAuth;
using NSubstitute;
using Xunit;
using static CodeCoverage.Tests.Dependencies.Lifecycle.DependencyHarness;

namespace CodeCoverage.Tests.CustomActions;

/// <summary>
/// #554 f3: "Set up rulesets" on several repositories. The admin's one-shot token reads every repository, one form and one
/// confirmation follow, and only the confirmed apply changes anything; the App's credentials are never used for it.
/// </summary>
public class SetUpRulesetsActionTests : CoverageRavenTest
{
    private const string Token = "stub-user-token";

    /// <summary>App_Data/Model/SetUpRulesets.json's id.</summary>
    private static readonly Guid FormType = Guid.Parse("c5980da3-b622-46cb-b65e-e6fcbbbd48e7");
    private static readonly string Foreign = Repository.DocumentId(EForgeProvider.GitHub, 920001);

    private sealed class ScriptedOnboarding : IDependencyOnboardingService
    {
        public Task<DependencyOnboardingResponse> ListAsync(IReadOnlyDictionary<string, string?> query, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyDictionary<string, string?>> CheckAsync(IReadOnlyList<Repository> repositories, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyDictionary<string, string?>>(
                repositories.ToDictionary(r => r.Id.Value, r => string.Equals(r.Id.Value, RepositoryId, StringComparison.OrdinalIgnoreCase) ? DependencyOnboardingService.Owner : null));
    }

    private sealed class Lease : ISparkOneShotOAuthLease
    {
        public int Disposed { get; private set; }
        public string AccessToken => Token;
        public string? GrantedScope => "repo";
        public ValueTask DisposeAsync() { Disposed++; return ValueTask.CompletedTask; }
    }

    private sealed class Fixture
    {
        public IManager Manager { get; } = Substitute.For<IManager>();
        public ISparkOneShotOAuth OAuth { get; } = Substitute.For<ISparkOneShotOAuth>();
        public IForgeMergeQueue Queue { get; } = Substitute.For<IForgeMergeQueue>();
        /// <summary>The App's reads: coverage.yml on the default branch (none unless a test says so).</summary>
        public IForgeIntegration Forge { get; } = Substitute.For<IForgeIntegration>();
        public SparkOneShotOAuthOptions Options { get; } = new();
        public Lease Lease { get; } = new();
        public MergeQueueRulesetPlan? Applied { get; set; }

        public Fixture()
        {
            Forge.Provider.Returns(EForgeProvider.GitHub);
            Forge.GetFileContentAsync(Arg.Any<Repository>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((string?)null);
            GitHubRulesetsOAuth.Configure(Options.Clients[GitHubRulesetsOAuth.ClientName] = new SparkOneShotOAuthClient(), "client-id", "client-secret");
            OAuth.AuthorizeAsync(GitHubRulesetsOAuth.ClientName, Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
                .Returns(new SparkOneShotOAuthOutcome(Lease, null));
            Queue.Provider.Returns(EForgeProvider.GitHub);
            Queue.GetRulesetStateAsync(Arg.Any<Repository>(), "master", Token, Arg.Any<CancellationToken>())
                .Returns(new ForgeRulesetState(77, [], null));
            Queue.ApplyRulesetPlanAsync(Arg.Any<Repository>(), "master", Arg.Any<MergeQueueRulesetPlan>(), Token, Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    Applied = call.Arg<MergeQueueRulesetPlan>();
                    return Task.FromResult<IReadOnlyList<ForgeRulesetApplied>>([new ForgeRulesetApplied("Created ruleset.", "https://github.com/acme/widget/rules/1")]);
                });
            Manager.GetPersistentObjectAsync(FormType, Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(_ => Form(null, null));
            Manager.GetTranslatedMessage(Arg.Any<string>(), Arg.Any<object[]>()).Returns(call => call.ArgAt<string>(0));
        }

        public MutableTime Time { get; } = new(new DateTimeOffset(2026, 11, 27, 9, 0, 0, TimeSpan.Zero));

        public MergeQueueRulesetSetup Setup(Raven.Client.Documents.Session.IAsyncDocumentSession session)
            => new([Queue], OAuth, Microsoft.Extensions.Options.Options.Create(Options), Manager, NullLogger<MergeQueueRulesetSetup>.Instance,
                new SingleForgeResolver(Forge), new RulesetNotices(session, Time), session, Time);

        public SetUpRulesetsAction Action(IAsyncDocumentSessionHolder holder)
            => new(new ScriptedOnboarding(), Setup(holder.Session), holder.Session, Manager);
    }

    /// <summary>Keeps the session the action reads with.</summary>
    private sealed record IAsyncDocumentSessionHolder(Raven.Client.Documents.Session.IAsyncDocumentSession Session);

    private static PersistentObject Form(int? approvals, bool? admins, object? checks = null, object? lineChecks = null, object? summaryJob = null) => new()
    {
        Id = null, Name = "SetUpRulesets", ObjectTypeId = FormType,
        Attributes =
        [
            new PersistentObjectAttribute { Name = nameof(MergeQueueRulesetChoices.RequiredApprovals), Value = approvals },
            new PersistentObjectAttribute { Name = nameof(MergeQueueRulesetChoices.AdminsMayMergeByPullRequest), Value = admins },
            new PersistentObjectAttribute { Name = nameof(MergeQueueRulesetChoices.RequiredChecks), Value = checks },
            new PersistentObjectAttribute
            {
                Name = nameof(MergeQueueRulesetChoices.SummaryJob),
                Value = summaryJob,
            },
            new PersistentObjectAttribute { Name = nameof(MergeQueueRulesetChoices.LineRequiredChecks), Value = lineChecks },
        ],
    };

    /// <summary>A RequiredChecks answer as the wire carries it: a JSON array of the picked rows' ids.</summary>
    private static JsonElement Wire(params string[] ids) => JsonSerializer.SerializeToElement(ids);

    private static CustomActionArgs Selected(params string[] ids)
        => new() { SelectedItems = [.. ids.Select(id => new QueryResultItem { Id = id, Values = [] })] };

    private async Task<IAsyncDocumentSessionHolder> SeedAsync()
    {
        var h = new DependencyHarness(GetDocumentStore());
        await h.SeedAsync(enabled: false);
        await h.StoreAsync(new Repository { GitHubId = 920001, Name = "tool", FullName = "other-org/tool", OwnerLogin = "other-org", DefaultBranch = "main" }, Foreign);
        return new IAsyncDocumentSessionHolder(h.Store.OpenAsyncSession());
    }

    /// <summary>
    /// The release lines of coverage.yml get ruleset 1 too (#554, PRD §8.2; owner 2026-11-06), and ruleset 3 with their own
    /// checks (R2, docs/release_lines_recovery_plan.md); ruleset 2 stays on the default branch.
    /// </summary>
    [Fact]
    public async Task The_release_lines_in_coverage_yml_get_ruleset_1_and_ruleset_3()
    {
        var holder = await SeedAsync();
        var f = new Fixture();
        f.Forge.GetFileContentAsync(Arg.Is<Repository>(r => string.Equals(r.Id.Value, RepositoryId, StringComparison.OrdinalIgnoreCase)), "master", "coverage.yml", Arg.Any<CancellationToken>())
            .Returns("versioning:\n  lines: {}\n");
        f.Manager.Retry.Result.Returns(
            new RetryResult { Option = "onboarding.plan", PersistentObject = Form(0, false, Wire("build"), Wire("build", "e2e")) },
            new RetryResult { Option = "rulesets.apply" });

        await f.Action(holder).ExecuteAsync(Selected(RepositoryId));

        var created = f.Applied!.Steps.OfType<CreateRulesetStep>().ToDictionary(s => s.Definition.Name, s => s.Definition);
        created[MergeQueueRulesetPlanner.RestrictUpdatesName].Include.Should().Equal(["~DEFAULT_BRANCH", "refs/heads/release/*-*.x"]);
        created[MergeQueueRulesetPlanner.ReviewsAndChecksName].Include.Should().Equal(["~DEFAULT_BRANCH"]);
        var line = created[MergeQueueRulesetPlanner.LineChecksName];
        line.Include.Should().Equal(["refs/heads/release/*-*.x"]);
        line.Rules.Single().Parameters!["required_status_checks"]!.AsArray().Select(c => c!["context"]!.GetValue<string>())
            .Should().Equal([MergeQueueRulesetPlanner.LinePolicyCheckName, "build", "e2e"], "the lines' own answer, not ruleset 2's, and the line policy");
    }

    [Fact]
    public async Task The_admins_token_reads_and_applies_after_one_form_and_one_confirmation()
    {
        var holder = await SeedAsync();
        var f = new Fixture();
        f.Manager.Retry.Result.Returns(
            new RetryResult { Option = "onboarding.plan", PersistentObject = Form(0, true, Wire("build", CheckGate.QueueCheckName)) },
            new RetryResult { Option = "rulesets.apply" });
        var args = Selected(RepositoryId, Foreign);

        await f.Action(holder).ExecuteAsync(args);

        await f.OAuth.Received(1).AuthorizeAsync(GitHubRulesetsOAuth.ClientName,
            Arg.Is<IReadOnlyCollection<string>>(s => s.SequenceEqual(new[] { "repo" })), Arg.Any<CancellationToken>());
        await f.Queue.Received(1).GetRulesetStateAsync(Arg.Is<Repository>(r => string.Equals(r.Id.Value, RepositoryId, StringComparison.OrdinalIgnoreCase)), "master", Token, Arg.Any<CancellationToken>());
        f.Manager.Retry.Received(1).Action("rulesets.chooseTitle", Arg.Any<string[]>(), "onboarding.plan", Arg.Any<PersistentObject>(),
            "rulesets.chooseMessage", true);
        f.Manager.Retry.Received(1).Action("rulesets.confirmTitle", Arg.Any<string[]>(), "rulesets.apply", null,
            Arg.Is<string>(m => m.StartsWith("<ul><li>acme/widget<ul><li>Create ruleset")), true);

        f.Applied.Should().NotBeNull();
        var ruleset2 = f.Applied!.Steps.OfType<CreateRulesetStep>().Single(s => s.Definition.Name == MergeQueueRulesetPlanner.ReviewsAndChecksName);
        ruleset2.Definition.Rules.Single(r => r.Type == "pull_request").Parameters!["required_approving_review_count"]!.GetValue<int>()
            .Should().Be(0, "the form's answer");
        ruleset2.Definition.Rules.Single(r => r.Type == "required_status_checks").Parameters!["required_status_checks"]!.ToJsonString()
            .Should().Contain("\"build\"").And.NotContain(CheckGate.QueueCheckName, "the picked checks, never the queue's own");
        f.Lease.Disposed.Should().Be(1, "the token is revoked when the action ends");

        var result = (SetUpRulesetsResult)args.Result!;
        result.Applied.Should().ContainSingle().Which.FullName.Should().Be("acme/widget");
        result.NotApplied.Should().ContainSingle().Which.Should().Be(new RulesetRepositoryResult(Foreign.ToLowerInvariant(), "other-org/tool", "rulesets.reason.notManaged", []));
        f.Manager.Client.Received(1).Notify(Arg.Any<string>(), NotificationKind.Success, Arg.Any<TimeSpan?>());
        f.Manager.Client.Received(1).Notify(Arg.Any<string>(), NotificationKind.Warning, Arg.Any<TimeSpan?>());
    }

    /// <summary>
    /// One prompt per repository (#554, owner 2026-11-07): its parent is that repository, so the RequiredChecks picker
    /// offers that repository's pull-request checks, and each plan takes its own answers.
    /// </summary>
    [Fact]
    public async Task Each_repository_gets_its_own_prompt_and_its_own_answers()
    {
        var f = new Fixture();
        RulesetRead Read(string id, string fullName)
        {
            var repository = new Repository { Id = Id<Repository>.FromTrusted(id), FullName = fullName, OwnerLogin = "acme", Name = fullName[5..], DefaultBranch = "master" };
            var state = new ForgeRulesetState(77, [], null);
            return new RulesetRead(new RulesetTarget(repository, f.Queue, "master"), state, MergeQueueRulesetPlanner.Defaults(state));
        }
        var prompted = new List<PersistentObject>();
        f.Manager.Retry.When(r => r.Action(Arg.Any<string>(), Arg.Any<string[]>(), Arg.Any<string?>(), Arg.Any<PersistentObject?>(), Arg.Any<string?>(), Arg.Any<bool>()))
            .Do(call => prompted.Add(call.ArgAt<PersistentObject>(3)));
        f.Manager.Retry.Result.Returns(
            new RetryResult { Option = "onboarding.plan", PersistentObject = Form(1, false, Wire("build")) },
            new RetryResult { Option = "onboarding.plan", PersistentObject = Form(2, true, Wire("lint", "test")) });

        using var store = GetDocumentStore();
        using var session = store.OpenAsyncSession();
        var planned = await f.Setup(session).ChooseAsync([Read("Repositories/github/1", "acme/one"), Read("Repositories/github/2", "acme/two")], CancellationToken.None);

        prompted.Select(p => p.Parent?.Id).Should().Equal("repositories/github/1", "repositories/github/2");
        prompted.Should().OnlyContain(p => p.Parent!.Name == nameof(Repository));
        // No ruleset and no classic protection: nothing is guessed from the default branch's head.
        ((string[])prompted[0][nameof(MergeQueueRulesetChoices.RequiredChecks)].Value!).Should().BeEmpty();
        f.Manager.Received(1).GetTranslatedMessage("rulesets.chooseTitle", Arg.Is<object[]>(a => a.Length == 1 && (string)a[0] == "acme/two"));

        string Checks(RulesetPlanned p) => p.Plan.Steps.OfType<CreateRulesetStep>()
            .Single(s => s.Definition.Name == MergeQueueRulesetPlanner.ReviewsAndChecksName)
            .Definition.Rules.Single(r => r.Type == "required_status_checks").Parameters!["required_status_checks"]!.ToJsonString();
        planned!.Select(p => p.Target.Repository.FullName).Should().Equal("acme/one", "acme/two");
        Checks(planned[0]).Should().Contain("\"build\"").And.NotContain("lint");
        Checks(planned[1]).Should().Contain("\"lint\"").And.Contain("\"test\"").And.NotContain("build");
    }

    [Fact]
    public async Task An_onboarding_that_turns_on_release_lines_gives_the_App_the_line_checks_bypass_before_it_merges()
    {
        // The merge cuts the lines with the App's token (onboarding_line_cuts_PRD D4); without the bypass SpaServices'
        // release/dotnet-10.x cut answered 422 (2026-11-12). Promoting or not makes no difference any more.
        var f = new Fixture();
        var repository = new Repository { Id = Id<Repository>.FromTrusted("Repositories/github/1"), FullName = "acme/one", OwnerLogin = "acme", Name = "one", DefaultBranch = "master" };
        var state = new ForgeRulesetState(77, [], null);
        f.Manager.Retry.Result.Returns(new RetryResult { Option = "onboarding.plan", PersistentObject = Form(0, false, Wire("build"), Wire("build")) });

        using var store = GetDocumentStore();
        using var session = store.OpenAsyncSession();
        var planned = await f.Setup(session).ChooseAsync([new RulesetRead(new RulesetTarget(repository, f.Queue, "master"), state, MergeQueueRulesetPlanner.Defaults(state))],
            CancellationToken.None, releaseLinesComing: true);

        planned!.Single().Plan.Steps.OfType<CreateRulesetStep>().Single(s => s.Definition.Name == MergeQueueRulesetPlanner.LineChecksName)
            .Definition.BypassActors.Should().Equal([new ForgeRulesetActor(77, "Integration", "exempt")]);
    }

    [Fact]
    public async Task A_cancelled_confirmation_changes_nothing_and_ends_the_token()
    {
        var holder = await SeedAsync();
        var f = new Fixture();
        f.Manager.Retry.Result.Returns(
            new RetryResult { Option = "onboarding.plan", PersistentObject = Form(1, false) },
            new RetryResult { Option = RetryResult.CancelOption });

        await f.Action(holder).ExecuteAsync(Selected(RepositoryId));

        await f.Queue.DidNotReceiveWithAnyArgs().ApplyRulesetPlanAsync(default!, default!, default!, default!, default);
        f.Lease.Disposed.Should().Be(1);
    }

    [Fact]
    public async Task Without_the_oauth_app_nothing_is_authorized()
    {
        var holder = await SeedAsync();
        var f = new Fixture();
        f.Options.Clients[GitHubRulesetsOAuth.ClientName].ClientId = string.Empty;

        await f.Action(holder).ExecuteAsync(Selected(RepositoryId));

        await f.OAuth.DidNotReceiveWithAnyArgs().AuthorizeAsync(default!, default!, default);
        f.Manager.Client.Received(1).Notify("rulesets.notConfigured", NotificationKind.Error, Arg.Any<TimeSpan?>());
    }

    [Fact]
    public async Task Only_unmanaged_repositories_never_open_the_popup()
    {
        var holder = await SeedAsync();
        var f = new Fixture();
        var args = Selected(Foreign);

        await f.Action(holder).ExecuteAsync(args);

        await f.OAuth.DidNotReceiveWithAnyArgs().AuthorizeAsync(default!, default!, default);
        ((SetUpRulesetsResult)args.Result!).NotApplied.Should().ContainSingle();
    }

    [Theory]
    [InlineData(SparkOneShotOAuthErrors.PopupBlocked, "rulesets.error.popupBlocked")]
    [InlineData(SparkOneShotOAuthErrors.Denied, "rulesets.error.denied")]
    public async Task A_failed_authorization_says_why_and_reads_nothing(string error, string message)
    {
        var holder = await SeedAsync();
        var f = new Fixture();
        f.OAuth.AuthorizeAsync(default!, default!, default).ReturnsForAnyArgs(new SparkOneShotOAuthOutcome(null, error));

        await f.Action(holder).ExecuteAsync(Selected(RepositoryId));

        await f.Queue.DidNotReceiveWithAnyArgs().GetRulesetStateAsync(default!, default!, default!, default);
        f.Manager.Client.Received(1).Notify(message, NotificationKind.Error, Arg.Any<TimeSpan?>());
    }

    [Fact]
    public async Task A_refused_read_is_reported_and_the_token_still_ends()
    {
        var holder = await SeedAsync();
        var f = new Fixture();
        f.Queue.GetRulesetStateAsync(default!, default!, default!, default)
            .ReturnsForAnyArgs<ForgeRulesetState?>(_ => throw new ForgeAccessDeniedException("Must have admin rights to Repository."));
        var args = Selected(RepositoryId);

        await f.Action(holder).ExecuteAsync(args);

        ((SetUpRulesetsResult)args.Result!).NotApplied.Should().ContainSingle().Which.Message.Should().Be("Must have admin rights to Repository.");
        f.Manager.Retry.DidNotReceiveWithAnyArgs().Action(default!, default!);
        f.Lease.Disposed.Should().Be(1);
    }

    private const long GitHubActions = 15368;

    /// <summary>A required check as GitHub stores it on a ruleset, pinned to <paramref name="app"/> when given.</summary>
    private static JsonObject Required(string context, long? app = null)
    {
        var check = new JsonObject { ["context"] = context };
        if (app is { } id)
            check["integration_id"] = id;
        return check;
    }

    /// <summary>The state with ruleset 2 requiring <paramref name="checks"/>, and ruleset 1 as the plan writes it.</summary>
    private static ForgeRulesetState Requiring(params JsonObject[] checks)
    {
        var ruleset1 = new ForgeRulesetDefinition(MergeQueueRulesetPlanner.RestrictUpdatesName, "branch", "active", ["~DEFAULT_BRANCH"], [],
            [new("update"), new("deletion"), new("non_fast_forward")], [new ForgeRulesetActor(77, "Integration", "exempt")]);
        var ruleset2 = new ForgeRulesetDefinition(MergeQueueRulesetPlanner.ReviewsAndChecksName, "branch", "active", ["~DEFAULT_BRANCH"], [],
        [
            new("pull_request", new JsonObject
            {
                ["required_approving_review_count"] = 0,
                ["dismiss_stale_reviews_on_push"] = false,
                ["require_last_push_approval"] = false,
                ["require_code_owner_review"] = false,
                ["required_review_thread_resolution"] = false,
            }),
            new("required_status_checks", new JsonObject
            {
                ["strict_required_status_checks_policy"] = true,
                ["required_status_checks"] = new JsonArray([.. checks]),
            }),
        ], []);
        return new ForgeRulesetState(77, [new(1, ruleset1), new(2, ruleset2)], null);
    }

    /// <summary>Writes what the queue would have written, in its own session.</summary>
    private static async Task<string> NoticeAsync(Raven.Client.Documents.IDocumentStore store, MutableTime time, Func<IRulesetNotices, Repository, Task> raise)
    {
        using var session = store.OpenAsyncSession();
        var repository = (await session.LoadAsync<Repository>(RepositoryId))!;
        await raise(new RulesetNotices(session, time), repository);
        await session.SaveChangesAsync();
        return repository.Id.Value;
    }

    private static async Task SeeAsync(Raven.Client.Documents.IDocumentStore store, params ForgeCheck[] known)
    {
        using var session = store.OpenAsyncSession();
        (await session.LoadAsync<Repository>(RepositoryId))!.KnownChecks.AddRange(known);
        await session.SaveChangesAsync();
    }

    /// <summary>The prompted forms and their messages.</summary>
    private static List<(PersistentObject Form, string Message)> Prompts(Fixture f)
    {
        var prompts = new List<(PersistentObject, string)>();
        f.Manager.Retry.When(r => r.Action(Arg.Any<string>(), Arg.Any<string[]>(), Arg.Any<string?>(), Arg.Any<PersistentObject?>(), Arg.Any<string?>(), Arg.Any<bool>()))
            .Do(call =>
            {
                if (call.ArgAt<PersistentObject?>(3) is { } form)
                    prompts.Add((form, call.ArgAt<string>(4)));
            });
        return prompts;
    }

    private static string[] Contexts(MergeQueueRulesetPlan plan, string ruleset)
        => [.. plan.Steps.OfType<UpdateRulesetStep>().Single(s => s.Definition.Name == ruleset).Definition.Rules
            .Single(r => r.Type == "required_status_checks").Parameters!["required_status_checks"]!.AsArray().Select(c => c!["context"]!.GetValue<string>())];

    private static long? PinOf(MergeQueueRulesetPlan plan, string context)
        => plan.Steps.OfType<UpdateRulesetStep>().Single(s => s.Definition.Name == MergeQueueRulesetPlanner.ReviewsAndChecksName).Definition.Rules
            .Single(r => r.Type == "required_status_checks").Parameters!["required_status_checks"]!.AsArray().OfType<JsonObject>()
            .Single(c => c["context"]!.GetValue<string>() == context)["integration_id"]?.GetValue<long>();

    /// <summary>
    /// #714 Dec6: opened from the bell about a check that never reported, the form starts without it and says why; the
    /// confirmation names what changes (Dec10 B1), and the apply resolves the notice.
    /// </summary>
    [Fact]
    public async Task A_check_that_never_reported_is_taken_out_of_the_form_and_the_apply_resolves_the_notice()
    {
        var holder = await SeedAsync();
        var store = holder.Session.Advanced.DocumentStore;
        var f = new Fixture();
        var repositoryId = await NoticeAsync(store, f.Time, (n, r) => n.RaiseMissingCheckAsync(r, "master", ["test"], ["build", "lint"], 7));
        f.Queue.GetRulesetStateAsync(Arg.Any<Repository>(), "master", Token, Arg.Any<CancellationToken>())
            .Returns(Requiring(Required("build"), Required("test")));
        var prompts = Prompts(f);
        f.Manager.Retry.Result.Returns(
            new RetryResult
            {
                Option = "onboarding.plan",
                PersistentObject = Form(0, false, Wire("build")),
            },
            new RetryResult { Option = "rulesets.apply" });

        await f.Action(holder).ExecuteAsync(Selected(RepositoryId));

        var (form, message) = prompts.Should().ContainSingle().Which;
        ((string[])form[nameof(MergeQueueRulesetChoices.RequiredChecks)].Value!).Should().Equal(["build"]);
        f.Manager.Received(1).GetTranslatedMessage("rulesets.prefill.removed", Arg.Is<object[]>(a => (string)a[0] == "test" && (int)a[1] == 7));
        message.Should().StartWith("rulesets.chooseMessage<br><br>rulesets.prefill.removed");
        f.Manager.Retry.Received(1).Action("rulesets.confirmTitle", Arg.Any<string[]>(), "rulesets.apply", null,
            Arg.Is<string>(m => m.Contains("required checks dropped: test")), true);
        Contexts(f.Applied!, MergeQueueRulesetPlanner.ReviewsAndChecksName).Should().Equal(["build"]);

        using var check = store.OpenAsyncSession();
        (await check.LoadAsync<RulesetNotice>(RulesetNotice.DocumentId(repositoryId, ERulesetNoticeKind.MissingCheck)))!.IsOpen()
            .Should().BeFalse("the managers confirmed the rulesets they want");
    }

    /// <summary>#714 Dec4, Dec6: a check a refused merge showed pinned to the wrong App is pinned to the App that reports it now.</summary>
    [Fact]
    public async Task A_pin_mismatch_is_pinned_to_the_App_that_reports_the_check_now()
    {
        var holder = await SeedAsync();
        var store = holder.Session.Advanced.DocumentStore;
        var f = new Fixture();
        await NoticeAsync(store, f.Time, (n, r) => n.RaisePinMismatchAsync(r, "master", new Dictionary<string, long> { ["build"] = GitHubActions }, 7));
        f.Queue.GetRulesetStateAsync(Arg.Any<Repository>(), "master", Token, Arg.Any<CancellationToken>())
            .Returns(Requiring(Required("build", 999)));
        f.Manager.Retry.Result.Returns(
            new RetryResult
            {
                Option = "onboarding.plan",
                PersistentObject = Form(0, false, Wire("build")),
            },
            new RetryResult { Option = "rulesets.apply" });

        await f.Action(holder).ExecuteAsync(Selected(RepositoryId));

        f.Manager.Received(1).GetTranslatedMessage("rulesets.prefill.repinned", Arg.Is<object[]>(a => (string)a[0] == "build (App 15368)"));
        PinOf(f.Applied!, "build").Should().Be(GitHubActions);
        f.Manager.Retry.Received(1).Action("rulesets.confirmTitle", Arg.Any<string[]>(), "rulesets.apply", null,
            Arg.Is<string>(m => m.Contains("&quot;build&quot; pinned: App 999 → App 15368")), true);
    }

    /// <summary>
    /// #714 Dec6: a summary job that never reported is left empty and asked for again, with what did report; the job picked
    /// then stands alone beside the App's own checks (Dec3), each pinned to the App that reports it (Dec4).
    /// </summary>
    [Fact]
    public async Task A_lost_summary_job_is_asked_for_again_and_the_new_one_is_pinned()
    {
        var holder = await SeedAsync();
        var store = holder.Session.Advanced.DocumentStore;
        var f = new Fixture();
        await SeeAsync(store, new ForgeCheck
        {
            Id = "pull-request",
            Name = "pull-request",
            Kind = EForgeCheckKind.CheckRun,
            AppSlug = "github-actions",
            AppId = GitHubActions,
            LastSeenUtc = f.Time.GetUtcNow().UtcDateTime,
            OnPullRequest = true,
        });
        await NoticeAsync(store, f.Time, (n, r) => n.RaiseMissingCheckAsync(r, "master", ["ci"], ["pull-request", "build"], 7));
        f.Queue.GetRulesetStateAsync(Arg.Any<Repository>(), "master", Token, Arg.Any<CancellationToken>())
            .Returns(Requiring(Required("ci"), Required("coverage/patch")));
        var prompts = Prompts(f);
        f.Manager.Retry.Result.Returns(
            new RetryResult
            {
                Option = "onboarding.plan",
                PersistentObject = Form(0, false, Wire("coverage/patch", "build"), summaryJob: JsonSerializer.SerializeToElement("pull-request")),
            },
            new RetryResult { Option = "rulesets.apply" });

        await f.Action(holder).ExecuteAsync(Selected(RepositoryId));

        var summaryField = prompts.Should().ContainSingle().Which.Form[nameof(MergeQueueRulesetChoices.SummaryJob)];
        summaryField.Value.Should().BeNull("nothing is guessed");
        summaryField.IsRequired.Should().BeTrue();
        f.Manager.Received(1).GetTranslatedMessage("rulesets.prefill.summaryJobLost",
            Arg.Is<object[]>(a => (string)a[0] == "ci" && (int)a[1] == 7 && (string)a[2] == "build, pull-request"));
        Contexts(f.Applied!, MergeQueueRulesetPlanner.ReviewsAndChecksName).Should().Equal(["coverage/patch", "pull-request"], "build is a job the summary job waits for");
        PinOf(f.Applied!, "coverage/patch").Should().Be(77, "the App's own check, pinned to the App");
        PinOf(f.Applied!, "pull-request").Should().Be(GitHubActions);
    }

    /// <summary>
    /// #714 Dec10 B2 and Dec11: opened by hand on rules with one check per job, the form offers the summary job and marks the
    /// required names no pull request reported in 14 days; it removes nothing.
    /// </summary>
    [Fact]
    public async Task The_form_offers_the_summary_job_and_marks_checks_not_seen_lately()
    {
        var holder = await SeedAsync();
        var store = holder.Session.Advanced.DocumentStore;
        var f = new Fixture();
        var now = f.Time.GetUtcNow().UtcDateTime;
        await SeeAsync(store,
            new ForgeCheck
            {
                Id = "build",
                Name = "build",
                LastSeenUtc = now.AddDays(-1),
                OnPullRequest = true,
            },
            new ForgeCheck
            {
                Id = "test",
                Name = "test",
                LastSeenUtc = now.AddDays(-20),
                OnPullRequest = true,
            });
        f.Queue.GetRulesetStateAsync(Arg.Any<Repository>(), "master", Token, Arg.Any<CancellationToken>())
            .Returns(Requiring(Required("build"), Required("test"), Required("lint")));
        var prompts = Prompts(f);
        f.Manager.Retry.Result.Returns(new RetryResult { Option = RetryResult.CancelOption });

        await f.Action(holder).ExecuteAsync(Selected(RepositoryId));

        ((string[])prompts.Should().ContainSingle().Which.Form[nameof(MergeQueueRulesetChoices.RequiredChecks)].Value!)
            .Should().Equal(["build", "lint", "test"], "a mark removes nothing");
        f.Manager.Received(1).GetTranslatedMessage("rulesets.summaryJobOffer", Arg.Any<object[]>());
        f.Manager.Received(1).GetTranslatedMessage("rulesets.stale.notSeenSince", Arg.Is<object[]>(a => (string)a[0] == "test" && (string)a[1] == "2026-11-07"));
        f.Manager.Received(1).GetTranslatedMessage("rulesets.stale.neverSeen", Arg.Is<object[]>(a => (string)a[0] == "lint"));
        f.Manager.DidNotReceive().GetTranslatedMessage("rulesets.stale.notSeenSince", Arg.Is<object[]>(a => (string)a[0] == "build"));
        await f.Queue.DidNotReceiveWithAnyArgs().ApplyRulesetPlanAsync(default!, default!, default!, default!, default);
    }
}
