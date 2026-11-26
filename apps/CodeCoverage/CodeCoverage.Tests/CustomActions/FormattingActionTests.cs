using CodeCoverage.CustomActions;
using CodeCoverage.Dependencies.Onboarding;
using CodeCoverage.Entities;
using CodeCoverage.Forge;
using CodeCoverage.Formatting;
using CodeCoverage.Onboarding;
using CodeCoverage.Tests.Dependencies.Lifecycle;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Actions;
using MintPlayer.Spark.Abstractions.Authentication;
using MintPlayer.Spark.Abstractions.ClientOperations;
using MintPlayer.Spark.Abstractions.Retry;
using NSubstitute;
using Xunit;
using static CodeCoverage.Tests.Dependencies.Lifecycle.DependencyHarness;

namespace CodeCoverage.Tests.CustomActions;

/// <summary>
/// #657 D1, D3, D5: FormatRepository and LintRepository start one run per selected repository the caller manages and
/// whose default branch has <c>formatting.yml</c>, ask once whether the pull requests go to the merge queue when one of
/// them has it, and report every repository that was not started.
/// </summary>
public class FormattingActionTests : CoverageRavenTest
{
    private static readonly string Second = Repository.DocumentId(EForgeProvider.GitHub, 920002);
    private static readonly string Foreign = Repository.DocumentId(EForgeProvider.GitHub, 920001);
    private static readonly string Missing = Repository.DocumentId(EForgeProvider.GitHub, 920009);

    private const string Workflow = """
        name: formatting
        on: workflow_dispatch
        jobs:
          format:
            runs-on: ubuntu-latest
            steps:
              - uses: MintPlayer/MintPlayer.Spark/.github/actions/format@format-v1
                with:
                  exclude: |
                    vendor/
                    # generated code
                    *.g.cs
        """;

    private sealed class ScriptedPermissions : IDependencyOnboardingService
    {
        public Task<DependencyOnboardingResponse> ListAsync(IReadOnlyDictionary<string, string?> query, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyDictionary<string, string?>> CheckAsync(IReadOnlyList<Repository> repositories, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyDictionary<string, string?>>(
                repositories.ToDictionary(r => r.Id.Value, r => string.Equals(r.Id.Value, Foreign, StringComparison.OrdinalIgnoreCase) ? null : DependencyOnboardingService.Owner));
    }

    private readonly IManager manager = Substitute.For<IManager>();
    private readonly IOnboardingService onboarding = Substitute.For<IOnboardingService>();
    private readonly IFormatRunEngine engine = Substitute.For<IFormatRunEngine>();
    private readonly ISparkCurrentPrincipal user = Substitute.For<ISparkCurrentPrincipal>();
    private readonly IForgeRepositoryAdministration administration = Substitute.For<IForgeRepositoryAdministration>();
    private readonly List<FormatRun> started = [];

    public FormattingActionTests()
    {
        manager.GetTranslatedMessage(Arg.Any<string>(), Arg.Any<object[]>()).Returns(call => call.ArgAt<string>(0));
        user.Id.Returns("users/7");
        administration.Provider.Returns(EForgeProvider.GitHub);
        administration.GetViewerLoginAsync(Arg.Any<CancellationToken>()).Returns(new ForgeViewerLogin("octocat", true, EForgeCredentialState.Ok));
        engine.StartAsync(Arg.Any<Repository>(), Arg.Do<FormatRun>(started.Add), Arg.Any<CancellationToken>()).Returns(true);
        Target(RepositoryId, new FormattingTarget("master", "aaa111", Workflow, MergeQueue: true));
        Target(Second, new FormattingTarget("main", "bbb222", Workflow, MergeQueue: false));
    }

    private void Target(string id, FormattingTarget? target, string? refusal = null)
        => onboarding.FormattingTargetAsync(Arg.Is<Repository>(r => string.Equals(r.Id.Value, id, StringComparison.OrdinalIgnoreCase)), Arg.Any<CancellationToken>()).Returns((target, refusal));

    private async Task<T> ActionAsync<T>(Func<IDependencyOnboardingService, IOnboardingService, IFormatRunEngine, Raven.Client.Documents.Session.IAsyncDocumentSession,
        IManager, ISparkCurrentPrincipal, IEnumerable<IForgeRepositoryAdministration>, T> create)
    {
        var h = new DependencyHarness(GetDocumentStore());
        await h.SeedAsync(enabled: false);
        await h.StoreAsync(new Repository { GitHubId = 920002, Name = "site", FullName = "acme/site", OwnerLogin = "acme", DefaultBranch = "main" }, Second);
        await h.StoreAsync(new Repository { GitHubId = 920001, Name = "tool", FullName = "other-org/tool", OwnerLogin = "other-org", DefaultBranch = "main" }, Foreign);
        return create(new ScriptedPermissions(), onboarding, engine, h.Store.OpenAsyncSession(), manager, user, [administration]);
    }

    private Task<FormatRepositoryAction> FormatAsync() => ActionAsync((p, o, e, s, m, u, a) => new FormatRepositoryAction(p, o, e, s, m, u, a));

    private static CustomActionArgs Selected(params string[] ids)
        => new() { SelectedItems = [.. ids.Select(id => new QueryResultItem { Id = id, Values = [] })] };

    [Fact]
    public async Task Open_and_queue_starts_each_repository_and_queues_only_where_the_merge_queue_is()
    {
        var action = await FormatAsync();
        manager.Retry.Result.Returns(new RetryResult { Option = "formatting.openAndQueue" });
        var args = Selected(RepositoryId, Second);

        await action.ExecuteAsync(args);

        manager.Retry.Received(1).Action("formatting.chooseTitle", Arg.Is<string[]>(o => o.SequenceEqual(new[] { "formatting.open", "formatting.openAndQueue" })),
            "formatting.open", null, "formatting.chooseMessage", true);
        started.Should().HaveCount(2);
        var queued = started.Single(r => r.Branch == "master");
        (queued.Mode, queued.Sha, queued.RequestedBy, queued.RequestedByLogin, queued.Enqueue)
            .Should().Be((FormattingWorkflow.FormatMode, "aaa111", "users/7", "octocat", true));
        queued.Exclude.Should().Equal("vendor/", "*.g.cs");
        var plain = started.Single(r => r.Branch == "main");
        (plain.RequestedByLogin, plain.Enqueue).Should().Be(((string?)null, false));
        await administration.Received(1).GetViewerLoginAsync(Arg.Any<CancellationToken>());

        var result = (FormattingResult)args.Result!;
        result.Mode.Should().Be(FormattingWorkflow.FormatMode);
        result.Repositories.Should().OnlyContain(r => r.Started && r.Note == "formatting.started");
        manager.Client.Received(2).Notify("formatting.startedOn", NotificationKind.Info);
    }

    [Fact]
    public async Task Open_starts_the_runs_without_queueing()
    {
        var action = await FormatAsync();
        manager.Retry.Result.Returns(new RetryResult { Option = "formatting.open" });

        await action.ExecuteAsync(Selected(RepositoryId));

        started.Single().Enqueue.Should().BeFalse();
        started.Single().RequestedByLogin.Should().BeNull();
        await administration.DidNotReceive().GetViewerLoginAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Cancel_starts_nothing()
    {
        var action = await FormatAsync();
        manager.Retry.Result.Returns(new RetryResult { Option = RetryResult.CancelOption });
        var args = Selected(RepositoryId);

        await action.ExecuteAsync(args);

        started.Should().BeEmpty();
        args.Result.Should().BeNull();
    }

    [Fact]
    public async Task Without_a_merge_queue_nothing_is_asked()
    {
        var action = await FormatAsync();

        await action.ExecuteAsync(Selected(Second));

        manager.Retry.DidNotReceive().Action(Arg.Any<string>(), Arg.Any<string[]>(), Arg.Any<string?>(), Arg.Any<PersistentObject?>(), Arg.Any<string?>(), Arg.Any<bool>());
        started.Single().Enqueue.Should().BeFalse();
    }

    [Fact]
    public async Task Every_repository_that_cannot_start_is_reported_with_its_reason()
    {
        var action = await FormatAsync();
        Target(RepositoryId, new FormattingTarget("master", "aaa111", Workflow: null, MergeQueue: true));
        Target(Second, null, "the default branch could not be read");
        var args = Selected(RepositoryId, Second, Foreign, Missing);

        await action.ExecuteAsync(args);

        await engine.DidNotReceive().StartAsync(Arg.Any<Repository>(), Arg.Any<FormatRun>(), Arg.Any<CancellationToken>());
        manager.Retry.DidNotReceive().Action(Arg.Any<string>(), Arg.Any<string[]>(), Arg.Any<string?>(), Arg.Any<PersistentObject?>(), Arg.Any<string?>(), Arg.Any<bool>());
        var notes = ((FormattingResult)args.Result!).Repositories.ToDictionary(r => r.Id, r => (r.Started, r.Note));
        notes.Should().BeEquivalentTo(new Dictionary<string, (bool, string)>
        {
            [RepositoryId.ToLowerInvariant()] = (false, "formatting.reason.notSetUp"),
            [Second.ToLowerInvariant()] = (false, "the default branch could not be read"),
            [Foreign.ToLowerInvariant()] = (false, "formatting.reason.notManaged"),
            [Missing] = (false, "formatting.reason.notFound"),
        });
        manager.Client.Received(4).Notify("formatting.notStarted", NotificationKind.Warning);
    }

    [Fact]
    public async Task An_unreadable_target_without_a_reason_says_so()
    {
        var action = await FormatAsync();
        Target(Second, null);
        var args = Selected(Second);

        await action.ExecuteAsync(args);

        ((FormattingResult)args.Result!).Repositories.Single().Note.Should().Be("formatting.reason.unreadable");
    }

    [Fact]
    public async Task A_run_already_going_is_reported_as_running()
    {
        var action = await FormatAsync();
        engine.StartAsync(Arg.Any<Repository>(), Arg.Any<FormatRun>(), Arg.Any<CancellationToken>()).Returns(false);
        var args = Selected(Second);

        await action.ExecuteAsync(args);

        var result = ((FormattingResult)args.Result!).Repositories.Single();
        (result.Started, result.Note).Should().Be((false, "formatting.reason.running"));
    }

    [Fact]
    public async Task Lint_starts_a_lint_run()
    {
        var action = await ActionAsync((p, o, e, s, m, u, a) => new LintRepositoryAction(p, o, e, s, m, u, a));
        var args = Selected(Second);

        await action.ExecuteAsync(args);

        started.Single().Mode.Should().Be(FormattingWorkflow.LintMode);
        ((FormattingResult)args.Result!).Mode.Should().Be(FormattingWorkflow.LintMode);
    }

    [Fact]
    public async Task No_selection_does_nothing()
    {
        var action = await FormatAsync();
        var args = Selected();

        await action.ExecuteAsync(args);

        args.Result.Should().BeNull();
        await onboarding.DidNotReceive().FormattingTargetAsync(Arg.Any<Repository>(), Arg.Any<CancellationToken>());
    }
}
