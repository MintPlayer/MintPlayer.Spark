using CodeCoverage.CustomActions;
using CodeCoverage.Dependencies.Onboarding;
using CodeCoverage.Entities;
using CodeCoverage.Forge;
using CodeCoverage.MergeQueue;
using CodeCoverage.Tests.Dependencies.Lifecycle;
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
                repositories.ToDictionary(r => r.Id!, r => r.Id == RepositoryId ? DependencyOnboardingService.Owner : null));
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
        public SparkOneShotOAuthOptions Options { get; } = new();
        public Lease Lease { get; } = new();
        public MergeQueueRulesetPlan? Applied { get; set; }

        public Fixture()
        {
            GitHubRulesetsOAuth.Configure(Options.Clients[GitHubRulesetsOAuth.ClientName] = new SparkOneShotOAuthClient(), "client-id", "client-secret");
            OAuth.AuthorizeAsync(GitHubRulesetsOAuth.ClientName, Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
                .Returns(new SparkOneShotOAuthOutcome(Lease, null));
            Queue.Provider.Returns(EForgeProvider.GitHub);
            Queue.GetRulesetStateAsync(Arg.Any<Repository>(), "master", Token, Arg.Any<CancellationToken>())
                .Returns(new ForgeRulesetState(77, [], null));
            Queue.GetBranchHeadAsync(Arg.Any<Repository>(), "master", Arg.Any<CancellationToken>()).Returns("abc123");
            Queue.GetCheckNamesOnCommitAsync(Arg.Any<Repository>(), "abc123", Arg.Any<CancellationToken>()).Returns(["build", CheckGate.QueueCheckName]);
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

        public MergeQueueRulesetSetup Setup()
            => new([Queue], OAuth, Microsoft.Extensions.Options.Options.Create(Options), Manager, NullLogger<MergeQueueRulesetSetup>.Instance);

        public SetUpRulesetsAction Action(IAsyncDocumentSessionHolder holder)
            => new(new ScriptedOnboarding(), Setup(), holder.Session, Manager);
    }

    /// <summary>Keeps the session the action reads with.</summary>
    private sealed record IAsyncDocumentSessionHolder(Raven.Client.Documents.Session.IAsyncDocumentSession Session);

    private static PersistentObject Form(int? approvals, bool? admins) => new()
    {
        Id = null, Name = "SetUpRulesets", ObjectTypeId = FormType,
        Attributes =
        [
            new PersistentObjectAttribute { Name = nameof(MergeQueueRulesetChoices.RequiredApprovals), Value = approvals },
            new PersistentObjectAttribute { Name = nameof(MergeQueueRulesetChoices.AdminsMayMergeByPullRequest), Value = admins },
        ],
    };

    private static CustomActionArgs Selected(params string[] ids)
        => new() { SelectedItems = [.. ids.Select(id => new QueryResultItem { Id = id, Values = [] })] };

    private async Task<IAsyncDocumentSessionHolder> SeedAsync()
    {
        var h = new DependencyHarness(GetDocumentStore());
        await h.SeedAsync(enabled: false);
        await h.StoreAsync(new Repository { GitHubId = 920001, Name = "tool", FullName = "other-org/tool", OwnerLogin = "other-org", DefaultBranch = "main" }, Foreign);
        return new IAsyncDocumentSessionHolder(h.Store.OpenAsyncSession());
    }

    [Fact]
    public async Task The_admins_token_reads_and_applies_after_one_form_and_one_confirmation()
    {
        var holder = await SeedAsync();
        var f = new Fixture();
        f.Manager.Retry.Result.Returns(
            new RetryResult { Option = "onboarding.plan", PersistentObject = Form(0, true) },
            new RetryResult { Option = "rulesets.apply" });
        var args = Selected(RepositoryId, Foreign);

        await f.Action(holder).ExecuteAsync(args);

        await f.OAuth.Received(1).AuthorizeAsync(GitHubRulesetsOAuth.ClientName,
            Arg.Is<IReadOnlyCollection<string>>(s => s.SequenceEqual(new[] { "repo" })), Arg.Any<CancellationToken>());
        await f.Queue.Received(1).GetRulesetStateAsync(Arg.Is<Repository>(r => r.Id == RepositoryId), "master", Token, Arg.Any<CancellationToken>());
        f.Manager.Retry.Received(1).Action("rulesets.chooseTitle", Arg.Any<string[]>(), "onboarding.plan", Arg.Any<PersistentObject>(),
            "rulesets.chooseMessage", true);
        f.Manager.Retry.Received(1).Action("rulesets.confirmTitle", Arg.Any<string[]>(), "rulesets.apply", null,
            Arg.Is<string>(m => m.StartsWith("<ul><li>acme/widget<ul><li>Create ruleset")), true);

        f.Applied.Should().NotBeNull();
        var ruleset2 = f.Applied!.Steps.OfType<CreateRulesetStep>().Single(s => s.Definition.Name == MergeQueueRulesetPlanner.ReviewsAndChecksName);
        ruleset2.Definition.Rules.Single(r => r.Type == "pull_request").Parameters!["required_approving_review_count"]!.GetValue<int>()
            .Should().Be(0, "the form's answer");
        ruleset2.Definition.Rules.Single(r => r.Type == "required_status_checks").Parameters!["required_status_checks"]!.ToJsonString()
            .Should().Contain("\"build\"").And.NotContain(CheckGate.QueueCheckName, "the head's checks, never the queue's own");
        f.Lease.Disposed.Should().Be(1, "the token is revoked when the action ends");

        var result = (SetUpRulesetsResult)args.Result!;
        result.Applied.Should().ContainSingle().Which.FullName.Should().Be("acme/widget");
        result.NotApplied.Should().ContainSingle().Which.Should().Be(new RulesetRepositoryResult(Foreign, "other-org/tool", "rulesets.reason.notManaged", []));
        f.Manager.Client.Received(1).Notify(Arg.Any<string>(), NotificationKind.Success, Arg.Any<TimeSpan?>());
        f.Manager.Client.Received(1).Notify(Arg.Any<string>(), NotificationKind.Warning, Arg.Any<TimeSpan?>());
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
}
