using CodeCoverage.Dependencies.Lifecycle;
using CodeCoverage.Entities;
using CodeCoverage.Forge;
using CodeCoverage.LookupReferences;
using CodeCoverage.Tests.CommitGraph;
using Microsoft.Extensions.Logging.Abstractions;
using Raven.Client.Documents;
using Xunit;
using RecordingBus = CodeCoverage.Tests.Dependencies.Lifecycle.DependencyOnboardingSurfaceTests.RecordingBus;

namespace CodeCoverage.Tests.Dependencies.Lifecycle;

/// <summary>
/// The scheduled half of the updater (Q8, R16, DependencyUpdatesCronJob): the daily onboarding fallback, the slot's run on
/// the default branch and on each release line the last run read (#568 D13), and the repositories it leaves alone.
/// </summary>
public class DependencyUpdatesCronJobTests : CoverageRavenTest
{
    private const string AccountId = "Accounts/8101";

    /// <summary>06:59 UTC: inside the 06:00 slot hour whatever a repository's spread minute (0–59).</summary>
    private static readonly DateTimeOffset InSlot = new(2026, 11, 9, 6, 59, 0, TimeSpan.Zero);

    private static string RepoId(long id) => Repository.DocumentId(EForgeProvider.GitHub, id);

    private static Repository Repo(long id, Action<Repository>? configure = null)
    {
        var repository = new Repository
        {
            GitHubId = id, Name = $"r{id}", FullName = $"acme/r{id}", OwnerLogin = "acme", Account = AccountId, DefaultBranch = "main",
            DependencyUpdates = EDependencyUpdatesPolicy.Enabled,
        };
        configure?.Invoke(repository);
        return repository;
    }

    private static DependencyUpdateRun Run(long id, Action<DependencyUpdateRun>? configure = null)
    {
        var run = new DependencyUpdateRun
        {
            Id = DependencyUpdateRun.ForRepository(RepoId(id)), Repository = RepoId(id), Onboarding = EDependencyOnboarding.Done,
            FileFrequency = EDependencyScheduleFrequency.Daily, FileHour = 6,
        };
        configure?.Invoke(run);
        return run;
    }

    private async Task<IDocumentStore> StoreAsync(Account account, params (Repository Repository, DependencyUpdateRun? Run)[] repositories)
    {
        var store = GetDocumentStore();
        await new global::CodeCoverage.Indexes.Accounts_Overview().ExecuteAsync(store);
        await new global::CodeCoverage.Indexes.Repositories_Overview().ExecuteAsync(store);
        using (var seed = store.OpenAsyncSession())
        {
            await seed.StoreAsync(account, AccountId);
            foreach (var (repository, run) in repositories)
            {
                await seed.StoreAsync(repository, RepoId(repository.GitHubId));
                if (run is not null) await seed.StoreAsync(run, run.Id);
            }
            await seed.SaveChangesAsync();
        }
        WaitForIndexing(store);
        return store;
    }

    private static Account Acme(Action<Account>? configure = null)
    {
        var account = new Account { GitHubId = 8101, Login = "acme" };
        configure?.Invoke(account);
        return account;
    }

    private static async Task<RecordingBus> TickAsync(IDocumentStore store, DateTimeOffset now)
    {
        var bus = new RecordingBus();
        using var session = store.OpenAsyncSession();
        await new DependencyUpdatesCronJob(session, bus, new MutableTime(now), NullLogger<DependencyUpdatesCronJob>.Instance).RunAsync(CancellationToken.None);
        return bus;
    }

    [Fact]
    public async Task A_due_slot_starts_the_scheduled_run_and_one_per_release_line_each_deduplicated_per_slot()
    {
        using var store = await StoreAsync(Acme(), (Repo(1), Run(1, r => r.FileLines = ["dotnet-10.x"])));

        var bus = await TickAsync(store, InSlot);

        var key = $"dependency-slot:{RepoId(1)}:2026-11-09";
        bus.Signals.Should().HaveCount(2);
        bus.Signals.Should().AllSatisfy(s => (s.Signal.Kind, s.Signal.Trigger).Should().Be((EDependencySignal.Trigger, (EDependencyRunTrigger?)EDependencyRunTrigger.Scheduled)));
        bus.Signals.Select(s => (s.Signal.Line, s.Options.DeduplicationKey)).Should().Equal((null, key), ("dotnet-10.x", $"{key}:dotnet-10.x"));
    }

    [Fact]
    public async Task Outside_the_slot_hour_nothing_starts()
    {
        using var store = await StoreAsync(Acme(), (Repo(1), Run(1)));

        var bus = await TickAsync(store, InSlot.AddHours(2));

        bus.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task A_repository_without_a_run_or_with_a_blocked_onboarding_gets_the_daily_onboarding_fallback()
    {
        using var store = await StoreAsync(Acme(),
            (Repo(1), null),
            (Repo(2), Run(2, r => { r.Onboarding = EDependencyOnboarding.Blocked; r.FileDisabled = true; })));

        var bus = await TickAsync(store, InSlot.AddHours(2));

        var onboard = bus.Signals.Where(s => s.Signal.Kind == EDependencySignal.Onboard).ToList();
        onboard.Select(s => s.Signal.RepositoryId).Should().BeEquivalentTo([RepoId(1), RepoId(2)]);
        onboard.Should().Contain(s => s.Options.DeduplicationKey == $"dependency-onboard:{RepoId(1)}:2026-11-09");
    }

    [Fact]
    public async Task A_file_that_turns_the_feature_off_skips_the_slot()
    {
        using var store = await StoreAsync(Acme(), (Repo(1), Run(1, r => r.FileDisabled = true)));

        var bus = await TickAsync(store, InSlot);

        bus.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task Archived_disconnected_branchless_and_switched_off_repositories_are_left_alone()
    {
        using var store = await StoreAsync(Acme(),
            (Repo(1, r => r.Archived = true), null),
            (Repo(2, r => r.Connection = RepositoryConnection.Disconnected), null),
            (Repo(3, r => r.DefaultBranch = null), null),
            (Repo(4, r => r.DependencyUpdates = EDependencyUpdatesPolicy.Disabled), null));

        var bus = await TickAsync(store, InSlot);

        bus.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task A_disconnected_account_is_not_scheduled()
    {
        using var store = await StoreAsync(Acme(a => a.Connection = RepositoryConnection.Disconnected), (Repo(1), null));

        var bus = await TickAsync(store, InSlot);

        bus.Sent.Should().BeEmpty();
    }

    [Fact]
    public void It_ticks_every_ten_minutes()
    {
        DependencyUpdatesCronJob.CronSchedule.Should().Be("*/10 * * * *");
    }
}
