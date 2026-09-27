using CodeCoverage.Entities;
using CodeCoverage.Feedback;
using CodeCoverage.Forge;
using CodeCoverage.Ingestion;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Raven.Client.Documents;
using Xunit;

namespace CodeCoverage.Tests.Ingestion;

/// <summary>
/// The cron jobs and thin recipients that move a build from Open to Finalized, and the nightly
/// forge reconcile.
/// </summary>
/// <remarks>
/// Thin glue, but glue with rules: the finalize sweep's debounce and 30-minute timeout, which of
/// two messages follows a finalize, and one account's failure not costing the others their sweep.
/// None of it had a test, and the first one written found the sweep re-announcing builds it had
/// deliberately left open.
/// </remarks>
public class IngestionJobsTests : CoverageRavenTest
{
    private const long RepoId = 55501;

    private static string CommitId(string sha) => Commit.DocumentId(EForgeProvider.GitHub, RepoId, sha);

    private static string BuildId(string sha) => Build.DocumentId(EForgeProvider.GitHub, RepoId, sha, 1, 1);

    private static SingleForgeResolver Forges(IForgeIntegration? forge = null)
    {
        forge ??= Substitute.For<IForgeIntegration>();
        forge.Provider.Returns(EForgeProvider.GitHub);
        return new SingleForgeResolver(forge);
    }

    private static async Task SeedBuildAsync(
        IDocumentStore store, string sha, DateTime createdAtUtc, DateTime? lastUploadAtUtc,
        bool withCommit = true, params BuildSession[] sessions)
    {
        using var seed = store.OpenAsyncSession();
        if (withCommit)
            await seed.StoreAsync(new Commit { Repository = Repository.DocumentId(EForgeProvider.GitHub, RepoId), Sha = sha }, CommitId(sha));
        await seed.StoreAsync(new Build
        {
            Commit = withCommit ? CommitId(sha) : null,
            CiRunId = 1,
            CiRunAttempt = 1,
            Run = Build.ComposeRun(1, 1),
            Status = "Open",
            CreatedAtUtc = createdAtUtc,
            LastUploadAtUtc = lastUploadAtUtc,
            Sessions = [.. sessions],
        }, BuildId(sha));
        await seed.SaveChangesAsync();
    }

    private static BuildSession Parsed(string id = "s-parsed") => new() { SessionId = id, ParseStatus = "Parsed" };

    private static BuildSession Pending(string id, params string[] files) => new() { SessionId = id, ParseStatus = "Pending", RawFileNames = files };

    private async Task<RecordingMessageBus> RunFinalizeSweepAsync(IDocumentStore store)
    {
        WaitForIndexing(store);
        var bus = new RecordingMessageBus();
        using var session = store.OpenAsyncSession();
        await new FinalizeBuildsCronJob(session, Forges(), bus, NullLogger<FinalizeBuildsCronJob>.Instance).RunAsync(default);
        return bus;
    }

    private static async Task<Build> LoadBuildAsync(IDocumentStore store, string sha)
    {
        using var session = store.OpenAsyncSession();
        return (await session.LoadAsync<Build>(BuildId(sha)))!;
    }

    [Fact]
    public async Task A_sweep_with_nothing_due_sends_nothing()
    {
        using var store = GetDocumentStore();
        // Uploaded a moment ago: still inside the two-minute debounce.
        await SeedBuildAsync(store, "fresh", DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddSeconds(-10), sessions: Parsed());

        var bus = await RunFinalizeSweepAsync(store);

        bus.Messages.Should().BeEmpty();
        (await LoadBuildAsync(store, "fresh")).Status.Should().Be("Open");
    }

    [Fact]
    public async Task A_quiet_fully_parsed_build_is_finalized_by_debounce_and_its_commit_assembled()
    {
        using var store = GetDocumentStore();
        await SeedBuildAsync(store, "quiet", DateTime.UtcNow.AddMinutes(-10), DateTime.UtcNow.AddMinutes(-5), sessions: Parsed());

        var bus = await RunFinalizeSweepAsync(store);

        var build = await LoadBuildAsync(store, "quiet");
        build.Status.Should().Be("Finalized");
        build.FinalizeReason.Should().Be("Debounce");
        bus.Of<AssembleCommitMessage>().Should().ContainSingle();
        bus.Of<AssembleCommitMessage>().Single().CommitId.Should().Be(CommitId("quiet"));
        bus.Of<AssembleCommitMessage>().Single().BuildId.Should().Be(BuildId("quiet"));
    }

    /// <summary>A build with no commit has nothing to assemble, so its feedback goes out directly.</summary>
    [Fact]
    public async Task A_finalized_build_without_a_commit_publishes_feedback_directly()
    {
        using var store = GetDocumentStore();
        await SeedBuildAsync(store, "orphan", DateTime.UtcNow.AddMinutes(-10), DateTime.UtcNow.AddMinutes(-5), withCommit: false, Parsed());

        var bus = await RunFinalizeSweepAsync(store);

        bus.Of<PublishFeedbackMessage>().Select(m => m.BuildId).Should().Equal(BuildId("orphan"));
        bus.Of<AssembleCommitMessage>().Should().BeEmpty();
    }

    /// <summary>
    /// The timeout closes a build regardless, and a session still Pending then will never parse, so
    /// it is labelled Failed with an error a consumer can act on — which differs by whether the
    /// upload carried any reports at all.
    /// </summary>
    [Fact]
    public async Task A_build_past_the_timeout_is_finalized_and_its_pending_sessions_marked_failed()
    {
        using var store = GetDocumentStore();
        await SeedBuildAsync(store, "stuck", DateTime.UtcNow.AddMinutes(-45), lastUploadAtUtc: null,
            sessions: [Parsed(), Pending("s-empty"), Pending("s-two", "a.xml", "b.xml")]);

        var bus = await RunFinalizeSweepAsync(store);

        var build = await LoadBuildAsync(store, "stuck");
        build.Status.Should().Be("Finalized");
        build.FinalizeReason.Should().Be("Timeout");
        build.Coverage.Should().NotBeNull("a terminal build always carries a summary, zeroed rather than absent");
        build.Sessions.Single(s => s.SessionId == "s-parsed").ParseStatus.Should().Be("Parsed");
        var empty = build.Sessions.Single(s => s.SessionId == "s-empty");
        empty.ParseStatus.Should().Be("Failed");
        empty.Error.Should().Contain("carried no report files");
        var two = build.Sessions.Single(s => s.SessionId == "s-two");
        two.ParseStatus.Should().Be("Failed");
        two.Error.Should().Contain("The 2 uploaded report(s) were never parsed");
        bus.Of<AssembleCommitMessage>().Should().ContainSingle();
    }

    /// <summary>A build that timed out with every session parsed is closed as a debounce, not a timeout.</summary>
    [Fact]
    public async Task A_timed_out_build_whose_sessions_all_parsed_is_labelled_a_debounce()
    {
        using var store = GetDocumentStore();
        await SeedBuildAsync(store, "late", DateTime.UtcNow.AddMinutes(-45), DateTime.UtcNow.AddSeconds(-5), sessions: Parsed());

        await RunFinalizeSweepAsync(store);

        (await LoadBuildAsync(store, "late")).FinalizeReason.Should().Be("Debounce");
    }

    /// <summary>
    /// The bug this file found. A build past the debounce but still parsing, and under the timeout,
    /// is skipped by the sweep — correctly, since finalizing it would give the commit a partial
    /// summary. But the broadcast loop walked every build the QUERY returned, not the builds the
    /// sweep finalized, so an assemble message still went out for it every minute until it
    /// finalized: the assembler re-ran, re-saved the commit's assembly, and logged "no finalized
    /// build" each time.
    /// </summary>
    [Fact]
    public async Task A_build_the_sweep_skipped_is_not_announced()
    {
        using var store = GetDocumentStore();
        await SeedBuildAsync(store, "parsing", DateTime.UtcNow.AddMinutes(-10), DateTime.UtcNow.AddMinutes(-5),
            sessions: [Parsed(), Pending("s-busy", "a.xml")]);
        await SeedBuildAsync(store, "done", DateTime.UtcNow.AddMinutes(-10), DateTime.UtcNow.AddMinutes(-5), sessions: Parsed());

        var bus = await RunFinalizeSweepAsync(store);

        (await LoadBuildAsync(store, "parsing")).Status.Should().Be("Open");
        bus.Of<AssembleCommitMessage>().Select(m => m.BuildId).Should().Equal(BuildId("done"));
        bus.Of<PublishFeedbackMessage>().Should().BeEmpty();
    }

    // ------------------------------------------------------------------------------------------
    // FinalizeBuildRecipient
    // ------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_explicit_finalize_closes_the_build_and_queues_its_commit()
    {
        using var store = GetDocumentStore();
        await SeedBuildAsync(store, "explicit", DateTime.UtcNow, DateTime.UtcNow, sessions: Pending("s", "a.xml"));
        var bus = new RecordingMessageBus();

        using (var session = store.OpenAsyncSession())
            await new FinalizeBuildRecipient(session, Forges(), NullLogger<FinalizeBuildRecipient>.Instance, bus)
                .HandleAsync(new FinalizeBuildMessage { BuildId = BuildId("explicit") });

        var build = await LoadBuildAsync(store, "explicit");
        build.Status.Should().Be("Finalized");
        build.FinalizeReason.Should().Be("Explicit");
        bus.Of<AssembleCommitMessage>().Single().CommitId.Should().Be(CommitId("explicit"));
    }

    [Fact]
    public async Task An_explicit_finalize_of_a_commitless_build_publishes_feedback()
    {
        using var store = GetDocumentStore();
        await SeedBuildAsync(store, "nocommit", DateTime.UtcNow, DateTime.UtcNow, withCommit: false, Parsed());
        var bus = new RecordingMessageBus();

        using (var session = store.OpenAsyncSession())
            await new FinalizeBuildRecipient(session, Forges(), NullLogger<FinalizeBuildRecipient>.Instance, bus)
                .HandleAsync(new FinalizeBuildMessage { BuildId = BuildId("nocommit") });

        bus.Of<PublishFeedbackMessage>().Single().BuildId.Should().Be(BuildId("nocommit"));
    }

    [Fact]
    public async Task An_explicit_finalize_of_a_missing_build_does_nothing()
    {
        using var store = GetDocumentStore();
        var bus = new RecordingMessageBus();

        using (var session = store.OpenAsyncSession())
            await new FinalizeBuildRecipient(session, Forges(), NullLogger<FinalizeBuildRecipient>.Instance, bus)
                .HandleAsync(new FinalizeBuildMessage { BuildId = BuildId("ghost") });

        bus.Messages.Should().BeEmpty();
    }

    // ------------------------------------------------------------------------------------------
    // AssembleCommitRecipient
    // ------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Assembling_a_commit_publishes_feedback_for_the_build_that_asked(bool assembled)
    {
        using var store = GetDocumentStore();
        var assembler = Substitute.For<ICommitAssembler>();
        assembler.AssembleAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(assembled ? new CommitAssembly { MeasuredFiles = 3, IncompleteReasons = ["noBase"] } : null));
        var bus = new RecordingMessageBus();

        using (var session = store.OpenAsyncSession())
        {
            var recipient = new AssembleCommitRecipient(session, assembler, bus, NullLogger<AssembleCommitRecipient>.Instance);
            await recipient.HandleAsync(new AssembleCommitMessage { CommitId = CommitId("x"), BuildId = BuildId("x") });
            // A webhook-driven re-assembly names no build, so there is no feedback to publish.
            await recipient.HandleAsync(new AssembleCommitMessage { CommitId = CommitId("x") });
        }

        await assembler.Received(2).AssembleAsync(CommitId("x"), Arg.Any<CancellationToken>());
        bus.Of<PublishFeedbackMessage>().Select(m => m.BuildId).Should().Equal(BuildId("x"));
    }

    // ------------------------------------------------------------------------------------------
    // ReconcileAccountRecipient
    // ------------------------------------------------------------------------------------------

    private static async Task SeedAccountAsync(IDocumentStore store, long id, string login, RepositoryConnection connection = RepositoryConnection.Connected)
    {
        using var seed = store.OpenAsyncSession();
        await seed.StoreAsync(new Account { GitHubId = id, Login = login, Connection = connection }, Account.DocumentId(EForgeProvider.GitHub, id));
        await seed.SaveChangesAsync();
    }

    [Fact]
    public async Task An_installation_change_reconciles_a_connected_account()
    {
        using var store = GetDocumentStore();
        await SeedAccountAsync(store, 1, "acme");
        var forge = Substitute.For<IForgeIntegration>();

        using (var session = store.OpenAsyncSession())
            await new ReconcileAccountRecipient(session, Forges(forge), NullLogger<ReconcileAccountRecipient>.Instance)
                .HandleAsync(new ReconcileAccountMessage { Provider = EForgeProvider.GitHub, AccountId = 1 });

        await forge.Received(1).ReconcileAsync(Arg.Is<Account>(a => a.Login == "acme"), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(2L, true)]   // disconnected: nothing to ask
    [InlineData(99L, false)] // unknown account
    public async Task An_installation_change_skips_an_account_it_cannot_reconcile(long accountId, bool seedDisconnected)
    {
        using var store = GetDocumentStore();
        if (seedDisconnected) await SeedAccountAsync(store, accountId, "gone", RepositoryConnection.Disconnected);
        var forge = Substitute.For<IForgeIntegration>();

        using (var session = store.OpenAsyncSession())
            await new ReconcileAccountRecipient(session, Forges(forge), NullLogger<ReconcileAccountRecipient>.Instance)
                .HandleAsync(new ReconcileAccountMessage { Provider = EForgeProvider.GitHub, AccountId = accountId });

        await forge.DidNotReceiveWithAnyArgs().ReconcileAsync(default!, default);
    }

    // ------------------------------------------------------------------------------------------
    // ReconcileForgeStateCronJob
    // ------------------------------------------------------------------------------------------

    private async Task RunNightlyAsync(IDocumentStore store, IForgeIntegration forge)
    {
        WaitForIndexing(store);
        using var session = store.OpenAsyncSession();
        await new ReconcileForgeStateCronJob(session, Forges(forge), NullLogger<ReconcileForgeStateCronJob>.Instance).RunAsync(default);
    }

    [Fact]
    public async Task The_nightly_sweep_with_no_connected_accounts_asks_nothing()
    {
        using var store = GetDocumentStore();
        await SeedAccountAsync(store, 3, "gone", RepositoryConnection.Disconnected);
        var forge = Substitute.For<IForgeIntegration>();

        await RunNightlyAsync(store, forge);

        await forge.DidNotReceiveWithAnyArgs().ReconcileAsync(default!, default);
    }

    /// <summary>One account failing must not cost the accounts behind it their sweep, and what they reconciled is saved.</summary>
    [Fact]
    public async Task One_account_failing_does_not_stop_the_others()
    {
        using var store = GetDocumentStore();
        await SeedAccountAsync(store, 10, "broken");
        await SeedAccountAsync(store, 11, "healthy");
        await SeedAccountAsync(store, 12, "disconnected", RepositoryConnection.Disconnected);
        var forge = Substitute.For<IForgeIntegration>();
        forge.ReconcileAsync(Arg.Is<Account>(a => a.Login == "broken"), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("forge unavailable"));
        forge.ReconcileAsync(Arg.Is<Account>(a => a.Login == "healthy"), Arg.Any<CancellationToken>())
            .Returns(call => { call.Arg<Account>().AvatarUrl = "https://example.invalid/healthy.png"; return Task.CompletedTask; });

        await RunNightlyAsync(store, forge);

        await forge.Received(2).ReconcileAsync(Arg.Any<Account>(), Arg.Any<CancellationToken>());
        using var check = store.OpenAsyncSession();
        (await check.LoadAsync<Account>(Account.DocumentId(EForgeProvider.GitHub, 11))).AvatarUrl
            .Should().Be("https://example.invalid/healthy.png");
    }

    /// <summary>
    /// A reconcile that throws halfway must not be saved halfway. The sweep ran one SaveChanges
    /// after the loop, on the same session the failing reconcile had already written into, so the
    /// half of it that ran before the throw was persisted along with every healthy account.
    /// </summary>
    [Fact]
    public async Task A_reconcile_that_throws_halfway_is_not_saved_halfway()
    {
        using var store = GetDocumentStore();
        await SeedAccountAsync(store, 20, "broken");
        await SeedAccountAsync(store, 21, "healthy");
        var forge = Substitute.For<IForgeIntegration>();
        forge.ReconcileAsync(Arg.Is<Account>(a => a.Login == "broken"), Arg.Any<CancellationToken>())
            .Returns<Task>(call =>
            {
                // The shape of a real partial reconcile: the account is touched, then the forge fails.
                call.Arg<Account>().AvatarUrl = "https://example.invalid/half-applied.png";
                throw new HttpRequestException("forge failed mid-reconcile");
            });
        forge.ReconcileAsync(Arg.Is<Account>(a => a.Login == "healthy"), Arg.Any<CancellationToken>())
            .Returns(call => { call.Arg<Account>().AvatarUrl = "https://example.invalid/healthy.png"; return Task.CompletedTask; });

        await RunNightlyAsync(store, forge);

        using var check = store.OpenAsyncSession();
        (await check.LoadAsync<Account>(Account.DocumentId(EForgeProvider.GitHub, 20))).AvatarUrl
            .Should().BeNull("the failing account's partial reconcile must be discarded");
        (await check.LoadAsync<Account>(Account.DocumentId(EForgeProvider.GitHub, 21))).AvatarUrl
            .Should().Be("https://example.invalid/healthy.png", "the healthy account's reconcile is still saved");
    }
}
