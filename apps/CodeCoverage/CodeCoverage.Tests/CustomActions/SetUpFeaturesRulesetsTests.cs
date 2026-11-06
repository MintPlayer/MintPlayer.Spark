using CodeCoverage.CustomActions;
using CodeCoverage.Dependencies.Onboarding;
using CodeCoverage.Entities;
using CodeCoverage.Forge;
using CodeCoverage.MergeQueue;
using CodeCoverage.Onboarding;
using CodeCoverage.PackageVersioning;
using CodeCoverage.Tests.Dependencies.Lifecycle;
using CodeCoverage.Versioning.Releases;
using Microsoft.Extensions.Logging.Abstractions;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Actions;
using MintPlayer.Spark.Abstractions.Retry;
using MintPlayer.Spark.Authorization.OneShotOAuth;
using NSubstitute;
using Xunit;
using static CodeCoverage.Tests.Dependencies.Lifecycle.DependencyHarness;

namespace CodeCoverage.Tests.CustomActions;

/// <summary>
/// #554 f4 (owner, 2026-11-06: "apply at confirmation"): with the merge queue picked, the onboarding chooser asks for the
/// GitHub popup and the rulesets form, shows the rulesets' plan in its confirmation, and applies them with the admin's
/// one-shot token right after the onboarding pull request opens.
/// </summary>
public class SetUpFeaturesRulesetsTests : CoverageRavenTest
{
    private const string Token = "stub-user-token";

    /// <summary>App_Data/Model/SetUpFeatures.json's and SetUpRulesets.json's ids.</summary>
    private static readonly Guid FeaturesType = Guid.Parse("88a980ce-1c12-4622-b04f-00e3979a0fb4");
    private static readonly Guid RulesetsType = Guid.Parse("c5980da3-b622-46cb-b65e-e6fcbbbd48e7");

    private sealed class Managed : IDependencyOnboardingService
    {
        public Task<DependencyOnboardingResponse> ListAsync(IReadOnlyDictionary<string, string?> query, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyDictionary<string, string?>> CheckAsync(IReadOnlyList<Repository> repositories, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyDictionary<string, string?>>(repositories.ToDictionary(r => r.Id!, _ => (string?)DependencyOnboardingService.Owner));
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
        public IOnboardingService Onboarding { get; } = Substitute.For<IOnboardingService>();
        public SparkOneShotOAuthOptions Options { get; } = new();
        public Lease Lease { get; } = new();

        public Fixture()
        {
            GitHubRulesetsOAuth.Configure(Options.Clients[GitHubRulesetsOAuth.ClientName] = new SparkOneShotOAuthClient(), "client-id", "client-secret");
            OAuth.AuthorizeAsync(GitHubRulesetsOAuth.ClientName, Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
                .Returns(new SparkOneShotOAuthOutcome(Lease, null));
            Queue.Provider.Returns(EForgeProvider.GitHub);
            Queue.GetRulesetStateAsync(Arg.Any<Repository>(), "master", Token, Arg.Any<CancellationToken>())
                .Returns(new ForgeRulesetState(77, [], null));
            Queue.GetBranchHeadAsync(Arg.Any<Repository>(), "master", Arg.Any<CancellationToken>()).Returns("abc123");
            Queue.GetCheckNamesOnCommitAsync(Arg.Any<Repository>(), "abc123", Arg.Any<CancellationToken>()).Returns(["build"]);
            Queue.ApplyRulesetPlanAsync(Arg.Any<Repository>(), "master", Arg.Any<MergeQueueRulesetPlan>(), Token, Arg.Any<CancellationToken>())
                .Returns([new ForgeRulesetApplied("Created ruleset.", "https://github.com/acme/widget/rules/1")]);
            Onboarding.PreviewAsync(Arg.Any<Repository>(), Arg.Any<IReadOnlySet<EOnboardingFeature>>(), Arg.Any<CancellationToken>())
                .Returns(new OnboardingPreview(new OnboardingPlan([EOnboardingFeature.MergeQueue], [], [], new LineRenamePlan([], []), [], [], "body"), null));
            Onboarding.OnboardAsync(Arg.Any<Repository>(), Arg.Any<IReadOnlySet<EOnboardingFeature>>(), Arg.Any<CancellationToken>())
                .Returns(new VersioningOnboardingOutcome(VersioningOnboardingOutcome.Opened, "Opened #5.", 5));
            Manager.GetPersistentObjectAsync(FeaturesType, Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(_ => Features(false));
            Manager.GetPersistentObjectAsync(RulesetsType, Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(_ => Rulesets(null));
            Manager.GetTranslatedMessage(Arg.Any<string>(), Arg.Any<object[]>()).Returns(call => call.ArgAt<string>(0));
        }

        public SetUpFeaturesAction Action(Raven.Client.Documents.Session.IAsyncDocumentSession session)
            => new(new Managed(), Onboarding,
                new MergeQueueRulesetSetup([Queue], OAuth, Microsoft.Extensions.Options.Options.Create(Options), Manager, NullLogger<MergeQueueRulesetSetup>.Instance),
                session, Manager);
    }

    private static PersistentObject Features(bool mergeQueue) => new()
    {
        Id = null, Name = "SetUpFeatures", ObjectTypeId = FeaturesType,
        Attributes = [.. Enum.GetValues<EOnboardingFeature>().Select(f => new PersistentObjectAttribute
            { Name = f.ToString(), Value = f == EOnboardingFeature.MergeQueue ? mergeQueue : f == EOnboardingFeature.Coverage })],
    };

    private static PersistentObject Rulesets(int? approvals) => new()
    {
        Id = null, Name = "SetUpRulesets", ObjectTypeId = RulesetsType,
        Attributes =
        [
            new PersistentObjectAttribute { Name = nameof(MergeQueueRulesetChoices.RequiredApprovals), Value = approvals },
            new PersistentObjectAttribute { Name = nameof(MergeQueueRulesetChoices.AdminsMayMergeByPullRequest), Value = false },
        ],
    };

    private static CustomActionArgs OnRepository()
        => new() { Parent = new PersistentObject { Id = RepositoryId, Name = "Repository", ObjectTypeId = Guid.Empty, Attributes = [] } };

    private async Task<Raven.Client.Documents.Session.IAsyncDocumentSession> SeedAsync()
    {
        var h = new DependencyHarness(GetDocumentStore());
        await h.SeedAsync(enabled: false);
        return h.Store.OpenAsyncSession();
    }

    [Fact]
    public async Task With_the_merge_queue_the_rulesets_join_the_confirmation_and_apply_after_the_pull_request_opens()
    {
        var session = await SeedAsync();
        var f = new Fixture();
        f.Manager.Retry.Result.Returns(
            new RetryResult { Option = "onboarding.plan", PersistentObject = Features(true) },
            new RetryResult { Option = "onboarding.plan", PersistentObject = Rulesets(1) },
            new RetryResult { Option = "onboarding.open" });

        await f.Action(session).ExecuteAsync(OnRepository());

        f.Manager.Retry.Received(1).Action("onboarding.confirmTitle", Arg.Any<string[]>(), "onboarding.open", null,
            Arg.Is<string>(m => m.Contains("onboarding.summary.rulesets<ul><li>acme/widget<ul><li>Create ruleset")), true);
        Received.InOrder(() =>
        {
            f.Onboarding.OnboardAsync(Arg.Any<Repository>(), Arg.Any<IReadOnlySet<EOnboardingFeature>>(), Arg.Any<CancellationToken>());
            f.Queue.ApplyRulesetPlanAsync(Arg.Is<Repository>(r => r.Id == RepositoryId), "master", Arg.Any<MergeQueueRulesetPlan>(), Token, Arg.Any<CancellationToken>());
        });
        f.Lease.Disposed.Should().Be(1, "the token is revoked when the chooser ends");
    }

    [Fact]
    public async Task A_refused_onboarding_applies_no_ruleset_and_ends_the_token()
    {
        var session = await SeedAsync();
        var f = new Fixture();
        f.Onboarding.OnboardAsync(default!, default!, default)
            .ReturnsForAnyArgs(new VersioningOnboardingOutcome(VersioningOnboardingOutcome.Refused, "No."));
        f.Manager.Retry.Result.Returns(
            new RetryResult { Option = "onboarding.plan", PersistentObject = Features(true) },
            new RetryResult { Option = "onboarding.plan", PersistentObject = Rulesets(1) },
            new RetryResult { Option = "onboarding.open" });

        await f.Action(session).ExecuteAsync(OnRepository());

        await f.Queue.DidNotReceiveWithAnyArgs().ApplyRulesetPlanAsync(default!, default!, default!, default!, default);
        f.Lease.Disposed.Should().Be(1);
    }

    [Fact]
    public async Task Without_the_merge_queue_there_is_no_popup()
    {
        var session = await SeedAsync();
        var f = new Fixture();
        f.Manager.Retry.Result.Returns(
            new RetryResult { Option = "onboarding.plan", PersistentObject = Features(false) },
            new RetryResult { Option = "onboarding.open" });

        await f.Action(session).ExecuteAsync(OnRepository());

        await f.OAuth.DidNotReceiveWithAnyArgs().AuthorizeAsync(default!, default!, default);
        await f.Onboarding.ReceivedWithAnyArgs(1).OnboardAsync(default!, default!, default);
    }

    [Fact]
    public async Task Without_the_oauth_app_the_rulesets_are_left_by_hand_and_the_pull_request_still_opens()
    {
        var session = await SeedAsync();
        var f = new Fixture();
        f.Options.Clients[GitHubRulesetsOAuth.ClientName].ClientId = string.Empty;
        f.Manager.Retry.Result.Returns(
            new RetryResult { Option = "onboarding.plan", PersistentObject = Features(true) },
            new RetryResult { Option = "onboarding.open" });

        await f.Action(session).ExecuteAsync(OnRepository());

        await f.OAuth.DidNotReceiveWithAnyArgs().AuthorizeAsync(default!, default!, default);
        f.Manager.Retry.Received(1).Action("onboarding.confirmTitle", Arg.Any<string[]>(), "onboarding.open", null,
            Arg.Is<string>(m => m.Contains("onboarding.summary.rulesetsByHand")), true);
        await f.Onboarding.ReceivedWithAnyArgs(1).OnboardAsync(default!, default!, default);
    }
}
