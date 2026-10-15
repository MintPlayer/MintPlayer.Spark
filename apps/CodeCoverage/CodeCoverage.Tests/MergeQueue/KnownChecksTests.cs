using CodeCoverage.Dependencies;
using CodeCoverage.Entities;
using CodeCoverage.Forge;
using CodeCoverage.Ingestion;
using CodeCoverage.MergeQueue;
using CodeCoverage.Migrations;
using CodeCoverage.Tests.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Raven.Client.Documents;
using Xunit;
using static CodeCoverage.Tests.MergeQueue.MergeQueueHarness;

namespace CodeCoverage.Tests.MergeQueue;

/// <summary>
/// The checks a repository has seen, the options of the merge queue's exclusion lists (merge-queue PRD §11, O38,
/// D107–D109): when a sighting is worth a write, the writer itself, the queue's own reads as a source, and the backfill
/// with its scheduling and its one-time migration. The webhook source is in <see cref="MergeQueueWebhookTests"/>, the
/// pickers' queries in <c>RemainingActionsTests</c>.
/// </summary>
public class KnownChecksTests : CoverageRavenTest
{
    private static readonly DateTime Now = new(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);

    private static ForgeCheck Known(string name, DateTime lastSeen, EForgeCheckKind? kind = EForgeCheckKind.CheckRun, string? app = "github-actions")
        => new() { Id = name, Name = name, Kind = kind, AppSlug = app, LastSeenUtc = lastSeen };

    private static ForgeCheck Seen(string name, EForgeCheckKind? kind = EForgeCheckKind.CheckRun, string? app = "github-actions")
        => new() { Name = name, Kind = kind, AppSlug = app };

    private sealed class FixedTime(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow);
    }

    // ── When a sighting is worth a write ────────────────────────────────────────────────────────────

    [Fact]
    public void A_new_name_is_due_and_a_fresh_known_one_is_not()
    {
        var due = ForgeCheck.Due([Known("build", Now.AddHours(-23))], [Seen("build"), Seen("e2e")], Now);

        due.Select(c => c.Id).Should().Equal("e2e");
        due[0].Name.Should().Be("e2e");
        due[0].LastSeenUtc.Should().Be(Now);
    }

    [Fact]
    public void A_name_a_day_stale_is_due_again()
        => ForgeCheck.Due([Known("build", Now.AddDays(-1))], [Seen("build")], Now).Select(c => c.Id).Should().Equal("build");

    /// <summary>The queue's expected-minimum read gives names alone; a later sighting that says what it is fills that in.</summary>
    [Fact]
    public void A_known_name_without_a_kind_is_due_when_a_sighting_says_its_kind()
    {
        ForgeCheck.Due([Known("build", Now, kind: null, app: null)], [Seen("build")], Now).Should().ContainSingle()
            .Which.Kind.Should().Be(EForgeCheckKind.CheckRun);
        ForgeCheck.Due([Known("build", Now)], [Seen("build", kind: null, app: null)], Now).Should().BeEmpty();
    }

    [Fact]
    public void The_queues_own_check_blank_names_and_repeats_are_not_due()
    {
        var due = ForgeCheck.Due([], [Seen(CheckGate.QueueCheckName), Seen("  "), Seen("build", kind: null, app: null), Seen("build")], Now);

        due.Should().ContainSingle();
        due[0].Id.Should().Be("build");
        due[0].Kind.Should().Be(EForgeCheckKind.CheckRun, "a repeat in the same batch fills in what the first sighting lacked");
    }

    // ── The writer ───────────────────────────────────────────────────────────────────────────────────

    private static string RepoId(long id) => Entities.Repository.DocumentId(EForgeProvider.GitHub, id);

    private static async Task SeedRepositoryAsync(IDocumentStore store, long id, Action<Entities.Repository>? configure = null)
    {
        using var session = store.OpenAsyncSession();
        var repository = new Entities.Repository
        {
            GitHubId = id, Name = $"repo{id}", FullName = $"acme/repo{id}", OwnerLogin = "acme", DefaultBranch = "main",
            Account = Account.DocumentId(EForgeProvider.GitHub, 77),
        };
        configure?.Invoke(repository);
        await session.StoreAsync(repository, RepoId(id));
        await session.SaveChangesAsync();
    }

    private static async Task<Entities.Repository> LoadAsync(IDocumentStore store, long id)
    {
        using var session = store.OpenAsyncSession();
        return (await session.LoadAsync<Entities.Repository>(RepoId(id)))!;
    }

    private static async Task RecordAsync(IDocumentStore store, string repositoryId, DateTime now, params ForgeCheck[] reported)
    {
        using var session = store.OpenAsyncSession();
        await new KnownChecksRecorder(session, new FixedTime(now)).RecordAsync(repositoryId, reported);
    }

    /// <summary>
    /// A patch, not a save of the loaded repository: everything else on the document (here the headline coverage another
    /// writer keeps up to date) is left exactly as it was.
    /// </summary>
    [Fact]
    public async Task New_names_are_added_known_ones_refreshed_and_nothing_else_is_touched()
    {
        using var store = GetDocumentStore();
        await SeedRepositoryAsync(store, 1, r =>
        {
            r.LatestCoverageSha = "abc";
            r.KnownChecks = [Known("build", Now.AddDays(-3), kind: null, app: null)];
        });

        await RecordAsync(store, RepoId(1), Now, Seen("build"), Seen("ci/legacy", EForgeCheckKind.Status, null));

        var repository = await LoadAsync(store, 1);
        repository.LatestCoverageSha.Should().Be("abc");
        repository.KnownChecks.Select(c => c.Id).Order(StringComparer.Ordinal).Should().Equal("build", "ci/legacy");
        var build = repository.KnownChecks.Single(c => c.Id == "build");
        build.LastSeenUtc.Should().Be(Now);
        build.Kind.Should().Be(EForgeCheckKind.CheckRun);
        build.AppSlug.Should().Be("github-actions");
        repository.KnownChecks.Single(c => c.Id == "ci/legacy").Kind.Should().Be(EForgeCheckKind.Status);
    }

    [Fact]
    public async Task Beyond_the_cap_the_oldest_sighting_goes()
    {
        using var store = GetDocumentStore();
        await SeedRepositoryAsync(store, 1, r => r.KnownChecks =
            [.. Enumerable.Range(0, ForgeCheck.MaxKnown).Select(i => Known($"check-{i:D3}", Now.AddDays(-2).AddMinutes(i)))]);

        await RecordAsync(store, RepoId(1), Now, Seen("newcomer"));

        var names = (await LoadAsync(store, 1)).KnownChecks.Select(c => c.Id).ToList();
        names.Should().HaveCount(ForgeCheck.MaxKnown);
        names.Should().Contain("newcomer");
        names.Should().NotContain("check-000", "it was seen longest ago");
        names.Should().Contain("check-001");
    }

    [Fact]
    public async Task A_missing_repository_is_written_nothing()
    {
        using var store = GetDocumentStore();

        await RecordAsync(store, RepoId(404), Now, Seen("build"));

        using var session = store.OpenAsyncSession();
        (await session.LoadAsync<Entities.Repository>(RepoId(404))).Should().BeNull();
    }

    [Fact]
    public async Task A_reported_check_event_is_written_down()
    {
        using var store = GetDocumentStore();
        await SeedRepositoryAsync(store, 1);

        using (var session = store.OpenAsyncSession())
            await new KnownChecksRecipient(new KnownChecksRecorder(session, new FixedTime(Now)))
                .HandleAsync(new ForgeWebhookMessage<CheckReported>(EForgeProvider.GitHub,
                    new CheckReported(RepoId(1), "GitGuardian Security Checks", EForgeCheckKind.CheckRun, "gitguardian")));

        (await LoadAsync(store, 1)).KnownChecks.Should().ContainSingle().Which.AppSlug.Should().Be("gitguardian");
    }

    // ── The queue's own reads (D108 (b)) ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_checks_the_queue_reads_are_reported_with_what_each_read_knows()
    {
        var h = new MergeQueueHarness(GetDocumentStore(), mergeBase: BaseSha);
        await h.SeedAsync();
        h.Queue.CheckStates[Head] = new(Head,
            [new ForgeCheckRun(41, "build", "completed", "success", 11, "github-actions", null)],
            [new ForgeCommitStatus("ci/legacy", "success", null)],
            [new ForgeCheckSuite(11, "completed", "success", "github-actions", 1)]);

        await h.EnqueueAsync();
        await h.DrainAsync();

        (await h.EntryAsync()).State.Should().Be(EMergeQueueEntryState.Merged);
        var reported = h.Raised.OfType<ForgeWebhookMessage<CheckReported>>().Select(m => m.Event).ToList();
        reported.Should().Contain(new CheckReported(RepositoryId, "build", null, null), "the expected-minimum read gives the name alone");
        reported.Should().Contain(new CheckReported(RepositoryId, "build", EForgeCheckKind.CheckRun, "github-actions"));
        reported.Should().Contain(new CheckReported(RepositoryId, "ci/legacy", EForgeCheckKind.Status, null));
    }

    [Fact]
    public async Task The_queue_reports_nothing_the_repository_already_knows()
    {
        var h = new MergeQueueHarness(GetDocumentStore(), mergeBase: BaseSha);
        await h.SeedAsync();
        using (var session = h.Store.OpenAsyncSession())
        {
            var repository = await session.LoadAsync<Entities.Repository>(RepositoryId);
            repository!.KnownChecks = [Known("build", DateTime.UtcNow)];
            await session.SaveChangesAsync();
        }

        await h.EnqueueAsync();
        await h.DrainAsync();

        (await h.EntryAsync()).State.Should().Be(EMergeQueueEntryState.Merged);
        h.Raised.OfType<ForgeWebhookMessage<CheckReported>>().Should().BeEmpty("a poll every two minutes must not write anything");
    }

    // ── The backfill (D109) ──────────────────────────────────────────────────────────────────────────

    private static async Task RefreshAsync(IDocumentStore store, FakeForgeMergeQueue queue, long id)
    {
        using var session = store.OpenAsyncSession();
        await new RefreshKnownChecksRecipient(session, [queue], new KnownChecksRecorder(session, TimeProvider.System),
                NullLogger<RefreshKnownChecksRecipient>.Instance)
            .HandleAsync(new RefreshKnownChecksMessage { RepositoryId = RepoId(id) });
    }

    [Fact]
    public async Task A_refresh_writes_down_what_reported_on_the_branch_and_the_open_pull_requests()
    {
        using var store = GetDocumentStore();
        await SeedRepositoryAsync(store, 1);
        var queue = new FakeForgeMergeQueue();
        queue.ReportedChecks.Add(new ForgeReportedCheck("build", EForgeCheckKind.CheckRun, "github-actions"));
        queue.ReportedChecks.Add(new ForgeReportedCheck("ci/legacy", EForgeCheckKind.Status, null));

        await RefreshAsync(store, queue, 1);

        (await LoadAsync(store, 1)).KnownChecks.Select(c => c.Id).Order(StringComparer.Ordinal).Should().Equal("build", "ci/legacy");
    }

    [Theory]
    [InlineData("archived")]
    [InlineData("disconnected")]
    public async Task A_refresh_skips_an_archived_or_disconnected_repository(string state)
    {
        using var store = GetDocumentStore();
        await SeedRepositoryAsync(store, 1, r =>
        {
            if (state == "archived") r.Archived = true;
            else r.Connection = RepositoryConnection.Disconnected;
        });
        var queue = new FakeForgeMergeQueue();
        queue.ReportedChecks.Add(new ForgeReportedCheck("build", EForgeCheckKind.CheckRun, "github-actions"));

        await RefreshAsync(store, queue, 1);

        (await LoadAsync(store, 1)).KnownChecks.Should().BeEmpty();
    }

    [Fact]
    public async Task A_refused_refresh_writes_nothing_and_does_not_throw()
    {
        using var store = GetDocumentStore();
        await SeedRepositoryAsync(store, 1);
        var queue = new FakeForgeMergeQueue { ReportedChecksThrows = new ForgeAccessDeniedException("no", "checks=read") };

        await RefreshAsync(store, queue, 1);

        (await LoadAsync(store, 1)).KnownChecks.Should().BeEmpty();
    }

    private static async Task<Account> SeedAccountAsync(IDocumentStore store)
    {
        var account = new Account { GitHubId = 77, Login = "acme", InstallationId = 5 };
        using (var seed = store.OpenAsyncSession())
        {
            await seed.StoreAsync(account, Account.DocumentId(EForgeProvider.GitHub, 77));
            await seed.SaveChangesAsync();
        }
        await SeedRepositoryAsync(store, 1);
        await SeedRepositoryAsync(store, 2, r => r.Archived = true);
        await SeedRepositoryAsync(store, 3, r => r.Connection = RepositoryConnection.Disconnected);
        await SeedRepositoryAsync(store, 4, r => { r.OwnerLogin = "globex"; r.FullName = "globex/repo4"; });
        return account;
    }

    protected override bool DeployIndexes => true;

    [Fact]
    public async Task Every_connected_unarchived_repository_of_the_account_is_queued_once_per_day()
    {
        using var store = GetDocumentStore();
        var account = await SeedAccountAsync(store);
        var bus = new RecordingMessageBus();

        using (var session = store.OpenAsyncSession())
            await new KnownChecksScheduler(session, bus, NullLogger<KnownChecksScheduler>.Instance)
                .ScheduleAccountAsync(account, waitForIndex: true);

        bus.Of<RefreshKnownChecksMessage>().Select(m => m.RepositoryId).Should().Equal(RepoId(1));
        bus.DeduplicationKeys.Should().ContainSingle().Which.Should().Be($"known-checks-{RepoId(1)}-{DateTimeOffset.UtcNow:yyyy-MM-dd}");
    }

    [Fact]
    public async Task An_installation_reconcile_and_the_nightly_sweep_schedule_the_backfill()
    {
        using var store = GetDocumentStore();
        await SeedAccountAsync(store);
        WaitForIndexing(store);
        var forge = Substitute.For<IForgeIntegration>();
        forge.Provider.Returns(EForgeProvider.GitHub);
        var scheduler = Substitute.For<IKnownChecksScheduler>();

        using (var session = store.OpenAsyncSession())
            await new ReconcileAccountRecipient(session, new SingleForgeResolver(forge), Substitute.For<IManifestScanScheduler>(),
                    NullLogger<ReconcileAccountRecipient>.Instance, knownChecks: scheduler)
                .HandleAsync(new ReconcileAccountMessage { Provider = EForgeProvider.GitHub, AccountId = 77 });
        using (var session = store.OpenAsyncSession())
            await new ReconcileForgeStateCronJob(session, new SingleForgeResolver(forge), Substitute.For<IManifestScanScheduler>(),
                    NullLogger<ReconcileForgeStateCronJob>.Instance, knownChecks: scheduler)
                .RunAsync(default);

        await scheduler.Received(2).ScheduleAccountAsync(Arg.Is<Account>(a => a.Login == "acme"), false, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task The_migration_queues_one_refresh_per_connected_unarchived_repository()
    {
        using var store = GetDocumentStore();
        await SeedAccountAsync(store);
        var bus = new RecordingMessageBus();

        await new M_202610151200_BackfillRepositoryKnownChecks(store, bus, NullLogger<M_202610151200_BackfillRepositoryKnownChecks>.Instance)
            .UpAsync(default);

        bus.Of<RefreshKnownChecksMessage>().Select(m => m.RepositoryId).Order(StringComparer.Ordinal)
            .Should().Equal(RepoId(1), RepoId(4));
        bus.DeduplicationKeys.Should().AllSatisfy(key => key.Should().StartWith("known-checks-"));
    }
}
