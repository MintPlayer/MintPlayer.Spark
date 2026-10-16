using CodeCoverage.CustomActions;
using CodeCoverage.Dependencies.Lifecycle;
using CodeCoverage.Dependencies.Onboarding;
using CodeCoverage.Entities;
using CodeCoverage.Forge;
using CodeCoverage.LookupReferences;
using CodeCoverage.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Interceptors;
using MintPlayer.Spark.Messaging;
using MintPlayer.Spark.Messaging.Abstractions;
using NSubstitute;
using Xunit;
using static CodeCoverage.Tests.Dependencies.Lifecycle.DependencyHarness;

namespace CodeCoverage.Tests.Dependencies.Lifecycle;

/// <summary>
/// The onboarding's surface around the saga (dependency-updates PRD §13.13): the switch's transitions and signals (R23,
/// R24), the account fan-out, "run the scheduled slot now" (R28), the rate-limit note for the status pages (R30), and the
/// onboarding list and its execution-time check (R20, R21) over a scripted forge seam.
/// </summary>
public class DependencyOnboardingSurfaceTests : CoverageRavenTest
{
    internal sealed class RecordingBus : IMessageBus
    {
        public List<(object Message, BroadcastOptions Options)> Sent { get; } = [];

        public Task BroadcastAsync<TMessage>(TMessage message, BroadcastOptions options, CancellationToken cancellationToken = default)
        {
            Sent.Add((message!, options));
            return Task.CompletedTask;
        }

        public IEnumerable<(DependencyUpdateSignalMessage Signal, BroadcastOptions Options)> Signals
            => Sent.Where(s => s.Message is DependencyUpdateSignalMessage).Select(s => ((DependencyUpdateSignalMessage)s.Message, s.Options));
    }

    // ── R23/R24: the switch ──────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(EDependencyUpdatesPolicy.Inherit, EDependencyUpdatesPolicy.Enabled, false, DependencyUpdateSwitch.On)]
    [InlineData(EDependencyUpdatesPolicy.Enabled, EDependencyUpdatesPolicy.Disabled, false, DependencyUpdateSwitch.Off)]
    [InlineData(EDependencyUpdatesPolicy.Inherit, EDependencyUpdatesPolicy.Enabled, true, null)]
    [InlineData(EDependencyUpdatesPolicy.Disabled, EDependencyUpdatesPolicy.Inherit, true, DependencyUpdateSwitch.On)]
    [InlineData(EDependencyUpdatesPolicy.Enabled, EDependencyUpdatesPolicy.Enabled, false, null)]
    public void A_repository_save_records_only_a_change_of_the_resolved_setting(EDependencyUpdatesPolicy before, EDependencyUpdatesPolicy after, bool accountOn, string? expected)
    {
        var account = new Account { DependencyUpdates = accountOn };
        DependencyUpdateSwitch.Transition(new Repository { DependencyUpdates = before }, new Repository { DependencyUpdates = after }, account).Should().Be(expected);
    }

    [Fact]
    public void A_new_repository_switched_on_is_an_on_transition_and_an_account_default_too()
    {
        DependencyUpdateSwitch.Transition(null, new Repository { DependencyUpdates = EDependencyUpdatesPolicy.Enabled }, null).Should().Be(DependencyUpdateSwitch.On);
        DependencyUpdateSwitch.Transition(new Account(), new Account { DependencyUpdates = true }).Should().Be(DependencyUpdateSwitch.On);
        DependencyUpdateSwitch.Transition(new Account { DependencyUpdates = true }, new Account()).Should().Be(DependencyUpdateSwitch.Off);
        DependencyUpdateSwitch.Transition(new Account(), new Account()).Should().BeNull();
    }

    [Fact]
    public async Task A_repositorys_switch_signals_once_per_save_and_is_expedited()
    {
        var bus = new RecordingBus();
        var at = new DateTimeOffset(2026, 10, 16, 17, 3, 0, TimeSpan.Zero);

        await DependencyUpdateSwitch.SignalAsync(bus, RepositoryId, DependencyUpdateSwitch.On, at, expedite: true, CancellationToken.None);
        await DependencyUpdateSwitch.SignalAsync(bus, RepositoryId, DependencyUpdateSwitch.Off, at, expedite: true, CancellationToken.None);

        var (onboard, options) = bus.Signals.First();
        onboard.Kind.Should().Be(EDependencySignal.Onboard);
        options.Expedite.Should().BeTrue();
        options.DeduplicationKey.Should().Be($"dependency-onboard:{RepositoryId}:{at.UtcTicks}");
        bus.Signals.Last().Signal.Kind.Should().Be(EDependencySignal.Offboard);
    }

    [Fact]
    public async Task An_account_switch_fans_out_over_its_inheriting_connected_repositories_only_not_expedited()
    {
        var store = GetDocumentStore();
        // Only the index the fan-out queries (CoverageRavenTest deploys none by default).
        await new global::CodeCoverage.Indexes.Repositories_Overview().ExecuteAsync(store);
        using (var seed = store.OpenAsyncSession())
        {
            await seed.StoreAsync(new Account { GitHubId = AccountGitHubId, Login = "acme", DependencyUpdates = true }, AccountId);
            Repository Repo(long id, EDependencyUpdatesPolicy policy, bool archived = false) => new()
            {
                GitHubId = id, Name = $"r{id}", FullName = $"acme/r{id}", OwnerLogin = "acme", Account = AccountId, DependencyUpdates = policy, Archived = archived,
            };
            await seed.StoreAsync(Repo(1, EDependencyUpdatesPolicy.Inherit), Repository.DocumentId(EForgeProvider.GitHub, 1));
            await seed.StoreAsync(Repo(2, EDependencyUpdatesPolicy.Disabled), Repository.DocumentId(EForgeProvider.GitHub, 2));
            await seed.StoreAsync(Repo(3, EDependencyUpdatesPolicy.Inherit, archived: true), Repository.DocumentId(EForgeProvider.GitHub, 3));
            await seed.SaveChangesAsync();
        }
        WaitForIndexing(store);
        var bus = new RecordingBus();

        using var session = store.OpenAsyncSession();
        var count = await DependencyUpdateSwitch.FanOutAsync(session, bus, AccountId, DependencyUpdateSwitch.On, DateTimeOffset.UnixEpoch, CancellationToken.None);

        count.Should().Be(1);
        bus.Signals.Should().ContainSingle().Which.Signal.RepositoryId.Should().Be(Repository.DocumentId(EForgeProvider.GitHub, 1));
        bus.Signals.Single().Options.Expedite.Should().BeFalse();
    }

    [Fact]
    public void The_switch_is_an_after_commit_outbox_hook_not_a_new_subscription()
    {
        // R23: IAfterSaveCommitted writes its message in the save's own commit (SparkCommittedInterceptors.cs), delivered by
        // Messaging's existing feeder: neither actions class declares a subscription of its own.
        typeof(IAfterSaveCommitted<Repository>).IsAssignableFrom(typeof(global::CodeCoverage.Actions.RepositoryActions)).Should().BeTrue();
        typeof(IAfterSaveCommitted<Account>).IsAssignableFrom(typeof(global::CodeCoverage.Actions.AccountActions)).Should().BeTrue();
        typeof(IBeforeSave<Account>).IsAssignableFrom(typeof(global::CodeCoverage.Actions.AccountActions)).Should().BeTrue();
        typeof(DependencyUpdateSwitch).Assembly.GetTypes()
            .Where(t => t.Namespace?.StartsWith("CodeCoverage.Dependencies", StringComparison.Ordinal) == true)
            .Where(t => t.BaseType is { IsGenericType: true } b && b.GetGenericTypeDefinition().Name.StartsWith("SubscriptionWorker", StringComparison.Ordinal))
            .Should().BeEmpty();
    }

    // ── R28: run the scheduled slot now ──────────────────────────────────────────────────────────

    private sealed class ScriptedOnboarding(string? reason) : IDependencyOnboardingService
    {
        public Task<DependencyOnboardingResponse> ListAsync(IReadOnlyDictionary<string, string?> query, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyDictionary<string, string?>> CheckAsync(IReadOnlyList<Repository> repositories, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyDictionary<string, string?>>(repositories.ToDictionary(r => r.Id!, _ => reason));
    }

    private async Task<(RunScheduledSlotNowAction Action, RecordingBus Bus, Repository Repository, Raven.Client.Documents.IDocumentStore Store)> RunNowAsync(
        string? reason, EDependencyRunState? state, bool enabled = true)
    {
        var h = new DependencyHarness(GetDocumentStore());
        await h.SeedAsync(enabled);
        if (state is { } running)
            await h.StoreAsync(new DependencyUpdateRun { Id = DependencyUpdateRun.ForRepository(RepositoryId), Repository = RepositoryId, State = running },
                DependencyUpdateRun.ForRepository(RepositoryId));
        var bus = new RecordingBus();
        var session = h.Store.OpenAsyncSession();
        var action = new RunScheduledSlotNowAction(new ScriptedOnboarding(reason), session, bus, Substitute.For<IManager>(), h.Time);
        return (action, bus, (await session.LoadAsync<Repository>(RepositoryId))!, h.Store);
    }

    [Fact]
    public async Task R28_run_now_starts_a_scheduled_run_with_a_fresh_key_expedited()
    {
        var (action, bus, repository, _) = await RunNowAsync(DependencyOnboardingService.Owner, EDependencyRunState.Idle);

        (await action.RunAsync(repository, CancellationToken.None)).Should().Be(RunScheduledSlotNowAction.Started);
        (await action.RunAsync(repository, CancellationToken.None)).Should().Be(RunScheduledSlotNowAction.Started);

        bus.Signals.Should().HaveCount(2);
        bus.Signals.Should().OnlyContain(s => s.Signal.Kind == EDependencySignal.Trigger && s.Signal.Trigger == EDependencyRunTrigger.Scheduled && s.Options.Expedite);
        bus.Signals.Select(s => s.Options.DeduplicationKey).Distinct().Should().HaveCount(2, "a fresh key each time, never the slot's own");
    }

    [Theory]
    [InlineData(EDependencyRunState.Looking)]
    [InlineData(EDependencyRunState.Publishing)]
    public async Task R28_run_now_is_refused_while_a_run_is_in_progress(EDependencyRunState state)
    {
        var (action, bus, repository, _) = await RunNowAsync(DependencyOnboardingService.Owner, state);

        (await action.RunAsync(repository, CancellationToken.None)).Should().Be(RunScheduledSlotNowAction.Busy);
        bus.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task R28_run_now_is_refused_to_someone_who_does_not_manage_it_and_where_the_feature_is_off()
    {
        var (refused, bus, repository, _) = await RunNowAsync(reason: null, EDependencyRunState.Idle);
        (await refused.RunAsync(repository, CancellationToken.None)).Should().Be(RunScheduledSlotNowAction.Refused);

        var (off, offBus, offRepository, _) = await RunNowAsync(DependencyOnboardingService.RepoAdmin, null, enabled: false);
        (await off.RunAsync(offRepository, CancellationToken.None)).Should().Be(RunScheduledSlotNowAction.Off);

        bus.Sent.Concat(offBus.Sent).Should().BeEmpty();
    }

    // ── R30: the rate-limit note on the status pages ─────────────────────────────────────────────

    private sealed class FixedMessageContext : IMessageContext
    {
        public string MessageId { get; set; } = "SparkMessages/1-A";
        public string QueueName => CodeCoverage.Feedback.CoverageQueues.DependencyUpdates;
        public int AttemptCount => 1;
        public DateTime? ExpiresAtUtc => null;
    }

    [Fact]
    public async Task R30_a_rate_limited_handling_is_noted_for_the_page_rethrown_for_the_bus_and_cleared_by_the_next_success()
    {
        var h = new DependencyHarness(GetDocumentStore());
        await h.SeedAsync();
        await h.StoreAsync(new DependencyUpdateRun { Id = DependencyUpdateRun.ForRepository(RepositoryId), Repository = RepositoryId }, DependencyUpdateRun.ForRepository(RepositoryId));
        var context = new FixedMessageContext();
        var notes = new DependencyRateLimitNotes(h.Store, context, Options.Create(new SparkMessagingOptions()), h.Time, NullLogger<DependencyRateLimitNotes>.Instance);
        var limited = new ForgeRequestException("GitHub rate limit reached.", 429,
            new ForgeRateLimitedException(TimeSpan.FromMinutes(20), null, EForgeRateLimitKind.Primary, "spent"));

        for (var park = 1; park <= 2; park++)
        {
            var act = () => notes.WatchAsync(RepositoryId, () => throw limited, CancellationToken.None);
            (await act.Should().ThrowAsync<ForgeRequestException>()).Which.Should().BeSameAs(limited);
        }

        var run = await h.RunAsync();
        run.RateLimitedUntilUtc.Should().Be(h.Time.Now.UtcDateTime.AddMinutes(20));
        run.RateLimitParks.Should().Be(2);
        run.RateLimitParkBudget.Should().Be(25, "MaxAttempts 5 × RateLimitParksPerAttempt 5 (R30)");
        var status = DependencyFleetView.Status(new DependencyFleetInput
        {
            Repositories = [], Now = h.Time.Now, Runs = new Dictionary<string, DependencyUpdateRun> { [RepositoryId] = run },
        }, new Repository { Id = RepositoryId, FullName = "acme/widget", Name = "widget" });
        status.RateLimit.Should().Be(new DependencyRateLimitView(run.RateLimitedUntilUtc!.Value, 2, 25));

        await notes.WatchAsync(RepositoryId, () => Task.CompletedTask, CancellationToken.None);
        (await h.RunAsync()).RateLimitedUntilUtc.HasValue.Should().BeFalse();
    }

    // ── R20/R21: the onboarding list and its execution-time check ─────────────────────────────────

    private sealed class ScriptedAdministration : IForgeRepositoryAdministration
    {
        public EForgeProvider Provider => EForgeProvider.GitHub;
        public List<ForgeViewerRepository> Listed { get; } = [];
        public List<string?> AskedKeys { get; } = [];
        public Dictionary<string, bool> Admins { get; } = new(StringComparer.Ordinal);
        public int AdminChecks { get; private set; }

        public Task<ForgeViewerLogin> GetViewerLoginAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new ForgeViewerLogin("octocat", true, EForgeCredentialState.Ok));

        public Task<ForgeViewerRepositories> ListViewerRepositoriesAsync(string? installationKey, CancellationToken cancellationToken = default)
        {
            AskedKeys.Add(installationKey);
            return Task.FromResult(new ForgeViewerRepositories([.. Listed.Where(r => installationKey is null || r.InstallationKey == installationKey)], "octocat", EForgeCredentialState.Ok));
        }

        public Task<IReadOnlyDictionary<string, bool>> CheckAdministratorAsync(Account account, IReadOnlyList<Repository> repositories, string login, CancellationToken cancellationToken = default)
        {
            AdminChecks++;
            return Task.FromResult<IReadOnlyDictionary<string, bool>>(repositories.ToDictionary(r => r.Id!, r => Admins.GetValueOrDefault(r.Id!)));
        }

        public Task<IReadOnlySet<string>> GetAdministeredRepositoriesAsync(Account account, IReadOnlyList<Repository> repositories, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlySet<string>>(repositories.Where(r => Admins.GetValueOrDefault(r.Id!)).Select(r => r.Id!).ToHashSet());
    }

    /// <summary>The viewer's owners: <paramref name="managed"/> are reached AND managed; <see cref="ReachedOnly"/> are
    /// reached (installation access) but not managed, the read-only organization member of R39.</summary>
    private sealed class ScriptedVisibility(params string[] managed) : ISparkVisibility
    {
        public string[] ReachedOnly { get; init; } = [];
        public Task<string[]> GetAllowedOwnersAsync() => Task.FromResult<string[]>([.. managed, .. ReachedOnly]);
        public Task<string[]> GetVisibleRepositoryIdsAsync() => Task.FromResult(Array.Empty<string>());
        public Task<bool> CanManageOwnerAsync(string ownerKey) => Task.FromResult(managed.Contains(ownerKey, StringComparer.OrdinalIgnoreCase));
        public Task<string[]> GetManagedOwnersAsync() => Task.FromResult(managed);
        public Task<bool> CanManageRepositoryAsync(Repository repository) => CanManageOwnerAsync(repository.OwnerKey);
        public Task<string[]> GetAdministeredRepositoryIdsAsync() => Task.FromResult(Array.Empty<string>());
    }

    private Task<(DependencyOnboardingService Service, ScriptedAdministration Forge)> OnboardingAsync(params string[] managed)
        => OnboardingAsync(new ScriptedVisibility(managed));

    private async Task<(DependencyOnboardingService Service, ScriptedAdministration Forge)> OnboardingAsync(ScriptedVisibility visibility)
    {
        var h = new DependencyHarness(GetDocumentStore());
        await h.SeedAsync(enabled: false);
        var forge = new ScriptedAdministration();
        forge.Listed.Add(new ForgeViewerRepository("4242", AccountGitHubId, "acme", RepoGitHubId, "acme/widget", true, IsAdmin: false));
        forge.Listed.Add(new ForgeViewerRepository("4242", AccountGitHubId, "acme", 910099, "acme/not-synced-yet", false, IsAdmin: false));
        forge.Listed.Add(new ForgeViewerRepository("5151", 88, "other-org", 920001, "other-org/tool", false, IsAdmin: true));
        forge.Listed.Add(new ForgeViewerRepository("5151", 88, "other-org", 920002, "other-org/readonly", false, IsAdmin: false));
        var service = new DependencyOnboardingService([forge], [new GitHubSetupRedirect()], visibility, h.Store.OpenAsyncSession());
        return (service, forge);
    }

    [Fact]
    public async Task R20_the_list_is_what_the_viewer_manages_with_the_reason_and_the_state()
    {
        var (service, _) = await OnboardingAsync("github:acme");

        var listing = await service.ListAsync(new Dictionary<string, string?>());

        listing.Login.Should().Be("octocat");
        listing.Narrowed.Should().BeFalse();
        listing.Repositories.Select(r => (r.FullName, r.ManagedBy)).Should().Equal(
            [("acme/not-synced-yet", "owner"), ("acme/widget", "owner"), ("other-org/tool", "repo-admin")]);
        var widget = listing.Repositories.Single(r => r.FullName == "acme/widget");
        widget.RepositoryId.Should().Be(RepositoryId);
        widget.Syncing.Should().BeFalse();
        widget.Enabled.Should().BeFalse();
        widget.Setting.Should().Be("Inherit");
        widget.OnboardingMergeQueue.Should().BeTrue("R25's default");
        var notSynced = listing.Repositories.Single(r => r.FullName == "acme/not-synced-yet");
        notSynced.Syncing.Should().BeTrue();
        notSynced.RepositoryId.Should().BeNull();
    }

    [Fact]
    public async Task R20_the_setup_redirect_narrows_to_its_installation_and_a_foreign_one_shows_nothing()
    {
        var (service, forge) = await OnboardingAsync("github:acme");

        var narrowed = await service.ListAsync(new Dictionary<string, string?> { ["installation_id"] = "4242", ["setup_action"] = "install" });
        var foreign = await service.ListAsync(new Dictionary<string, string?> { ["installation_id"] = "999" });
        var request = await service.ListAsync(new Dictionary<string, string?> { ["installation_id"] = "4242", ["setup_action"] = "request" });

        narrowed.Narrowed.Should().BeTrue();
        narrowed.SetupAction.Should().Be("install");
        narrowed.Repositories.Select(r => r.FullName).Should().BeEquivalentTo(["acme/widget", "acme/not-synced-yet"]);
        foreign.Repositories.Should().BeEmpty("no existence oracle: an installation the viewer cannot reach shows nothing");
        request.SetupAction.Should().Be("request");
        forge.AskedKeys.Should().Equal(["4242", "999", "4242"], "the key is handed to the forge's own viewer-scoped listing, never trusted");
    }

    [Fact]
    public async Task R21_the_execution_time_check_asks_the_forge_only_for_what_management_does_not_cover()
    {
        var (service, forge) = await OnboardingAsync("github:acme");
        var widget = new Repository { Id = RepositoryId, OwnerLogin = "acme", Account = AccountId, Provider = EForgeProvider.GitHub };
        var foreign = new Repository { Id = Repository.DocumentId(EForgeProvider.GitHub, 920001), OwnerLogin = "other-org", Account = AccountId, Provider = EForgeProvider.GitHub };
        var refused = new Repository { Id = Repository.DocumentId(EForgeProvider.GitHub, 920002), OwnerLogin = "other-org", Account = AccountId, Provider = EForgeProvider.GitHub };
        forge.Admins[foreign.Id!] = true;

        var reasons = await service.CheckAsync([widget, foreign, refused]);

        reasons[widget.Id!].Should().Be("owner");
        reasons[foreign.Id!].Should().Be("repo-admin");
        reasons[refused.Id!].Should().BeNull("a listing's admin flag is shown, never trusted: the forge said no now");
        forge.AdminChecks.Should().Be(1);
    }

    [Fact]
    public async Task R39_a_read_only_member_who_reaches_the_installation_gets_no_owner_reason_only_a_checked_repo_admin_one()
    {
        // acme's installation is reachable (the owner set has it), but the viewer is no owner of the organization.
        var (service, forge) = await OnboardingAsync(new ScriptedVisibility { ReachedOnly = ["github:acme"] });
        var widget = new Repository { Id = RepositoryId, OwnerLogin = "acme", Account = AccountId, Provider = EForgeProvider.GitHub };

        var listing = await service.ListAsync(new Dictionary<string, string?>());
        var before = await service.CheckAsync([widget]);
        forge.Admins[widget.Id!] = true;
        var after = await service.CheckAsync([widget]);

        listing.Repositories.Select(r => (r.FullName, r.ManagedBy)).Should().Equal([("other-org/tool", "repo-admin")],
            "acme's repositories are listed without the admin flag, and reaching the installation is not owning it");
        before[widget.Id!].Should().BeNull("a member with read access may not enable dependency updates");
        after[widget.Id!].Should().Be("repo-admin", "an administrator of the repository may, asked of the forge now");
    }
}
