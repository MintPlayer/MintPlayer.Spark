using CodeCoverage.Dependencies.Lifecycle;
using CodeCoverage.Dependencies.Policy;
using CodeCoverage.Entities;
using CodeCoverage.Forge;
using CodeCoverage.LookupReferences;
using MintPlayer.Spark.Messaging;
using MintPlayer.Spark.Messaging.Abstractions;
using NSubstitute;
using Raven.Client.Documents;
using Xunit;
using RecordingBus = CodeCoverage.Tests.Dependencies.Lifecycle.DependencyOnboardingSurfaceTests.RecordingBus;

namespace CodeCoverage.Tests.Dependencies.Lifecycle;

/// <summary>
/// The saga's lane and its filters (DependencyUpdateRecipients.cs): which forge events become signals on which run (the
/// default branch's or a release line's, #568 D13), the producer events of §7.6, and the debounced release propagation.
/// </summary>
public class DependencyUpdateRecipientsTests : CoverageRavenTest
{
    private const string AccountId = "Accounts/7001";
    private static readonly string RepositoryId = Repository.DocumentId(EForgeProvider.GitHub, 7001);
    private static readonly string ConsumerId = Repository.DocumentId(EForgeProvider.GitHub, 7002);
    private const string Line = "dotnet-10.x";

    private static ForgeWebhookMessage<T> Webhook<T>(T evt) where T : class, IForgeEvent => new(EForgeProvider.GitHub, evt);

    private static async Task SeedAsync(IDocumentStore store, params (object Document, string Id)[] documents)
    {
        using var seed = store.OpenAsyncSession();
        foreach (var (document, id) in documents) await seed.StoreAsync(document, id);
        await seed.SaveChangesAsync();
    }

    private static (DependencyUpdateRun, string) Run(string? line = null, Action<DependencyUpdateRun>? configure = null)
    {
        var id = DependencyUpdateRun.ForRepository(RepositoryId, line);
        var run = new DependencyUpdateRun { Id = id, Repository = RepositoryId, Line = line };
        configure?.Invoke(run);
        return (run, id);
    }

    private static (Repository, string) Repo(string id, long gitHubId, bool dependencyUpdates = true, string defaultBranch = "main") => (new Repository
    {
        GitHubId = gitHubId, Name = $"r{gitHubId}", FullName = $"acme/r{gitHubId}", OwnerLogin = "acme", Account = AccountId, DefaultBranch = defaultBranch,
        DependencyUpdates = dependencyUpdates ? EDependencyUpdatesPolicy.Enabled : EDependencyUpdatesPolicy.Disabled,
    }, id);

    private static (RepositoryManifest, string) Manifest(string repositoryId, List<ManifestPackage>? produces = null, List<ManifestDependency>? consumes = null)
        => (new RepositoryManifest { Repository = repositoryId, OwnerKey = new ForgeOwner(EForgeProvider.GitHub, "acme").ToString(), Produces = produces ?? [], Consumes = consumes ?? [] },
            RepositoryManifest.ForRepository(repositoryId));

    private Tests.Commands.FakeCommentReactions Reactions { get; } = new();

    private async Task<RecordingBus> EventsAsync(IDocumentStore store, Func<DependencyUpdateEventsRecipient, Task> act)
    {
        var bus = new RecordingBus();
        using var session = store.OpenAsyncSession();
        await act(new DependencyUpdateEventsRecipient(session, bus, Tests.Commands.CommandTestKit.Lifecycle(store, Reactions)));
        return bus;
    }

    // ── DependencyUpdateRecipient: the lane forwards ───────────────────────────────────────────────

    [Fact]
    public async Task The_lane_forwards_steps_and_signals_under_the_rate_limit_watch_and_propagation_to_its_service()
    {
        var engine = Substitute.For<IDependencyUpdateEngine>();
        var propagation = Substitute.For<IDependencyPropagation>();
        var rateLimits = Substitute.For<IDependencyRateLimitNotes>();
        rateLimits.WatchAsync(Arg.Any<string>(), Arg.Any<Func<Task>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Func<Task>>()());
        var recipient = new DependencyUpdateRecipient(engine, propagation, rateLimits);
        var step = new DependencyUpdateStepMessage { RepositoryId = RepositoryId, Run = 1, Generation = 1, Step = EDependencyStep.Start };
        var signal = new DependencyUpdateSignalMessage { RepositoryId = RepositoryId, Kind = EDependencySignal.Merged };
        var produced = new DependencyProducerEventMessage { AccountId = AccountId, ProducerRepositoryId = RepositoryId, Kind = "release", AtUtc = DateTime.UtcNow };
        var propagate = new DependencyPropagationMessage { AccountId = AccountId, ProducerRepositoryId = RepositoryId, EventAtUtc = DateTime.UtcNow };

        await recipient.HandleAsync(step);
        await recipient.HandleAsync(signal);
        await recipient.HandleAsync(produced);
        await recipient.HandleAsync(propagate);

        await engine.Received(1).HandleStepAsync(step, Arg.Any<CancellationToken>());
        await engine.Received(1).HandleSignalAsync(signal, Arg.Any<CancellationToken>());
        await rateLimits.Received(2).WatchAsync(RepositoryId, Arg.Any<Func<Task>>(), Arg.Any<CancellationToken>());
        await propagation.Received(1).RecordAsync(produced, Arg.Any<CancellationToken>());
        await propagation.Received(1).PropagateAsync(propagate, Arg.Any<CancellationToken>());
    }

    // ── DependencyUpdateEventsRecipient: events to signals ────────────────────────────────────────

    [Fact]
    public async Task A_command_on_the_runs_pull_request_signals_that_run_with_its_details()
    {
        using var store = GetDocumentStore();
        var (main, mainId) = Run(configure: r => r.PullRequestNumber = 12);
        var (line, lineId) = Run(Line, r => r.PullRequestNumber = 13);
        await SeedAsync(store, (main, mainId), (line, lineId));

        var bus = await EventsAsync(store, r => r.HandleAsync(Webhook(new DependencyCommandIssued(RepositoryId, 13, 99, EDependencyCommand.Hold, "until friday", "octo"))));

        var signal = bus.Signals.Should().ContainSingle().Which.Signal;
        signal.Line.Should().Be(Line);
        signal.Kind.Should().Be(EDependencySignal.Command);
        signal.Number.Should().Be(13);
        signal.CommentId.Should().Be(99);
        signal.Command.Should().Be(EDependencyCommand.Hold);
        signal.Argument.Should().Be("until friday");
        signal.Actor.Should().Be("octo");
        Reactions.Added.Should().BeEmpty("the run's engine reacts, not the recipient");
    }

    [Fact]
    public async Task A_command_on_another_pull_request_of_a_repository_that_uses_the_updater_gets_thumbs_down()
    {
        using var store = GetDocumentStore();
        var (main, mainId) = Run(configure: r => r.PullRequestNumber = 12);
        await SeedAsync(store, (main, mainId), Repo(RepositoryId, 7001));

        var bus = await EventsAsync(store, r => r.HandleAsync(Webhook(new DependencyCommandIssued(RepositoryId, 40, 98, EDependencyCommand.Approve, "nx", "octo"))));

        bus.Signals.Should().BeEmpty();
        Reactions.On(98).Should().Equal([EForgeReaction.ThumbsDown], "#660: the bot says it read the command and why it did nothing");
        var repository = new Repository { Provider = EForgeProvider.GitHub, GitHubId = 7001 };
        var record = await Tests.Commands.CommandTestKit.InvocationAsync(store, repository, 98);
        record!.Outcome.Should().Be(ECommandOutcome.Refused);
        record.Message.Should().Contain("#40 is not a dependency update pull request");
    }

    [Fact]
    public async Task A_command_in_a_repository_without_dependency_runs_is_left_to_other_bots()
    {
        using var store = GetDocumentStore();
        await SeedAsync(store, Repo(RepositoryId, 7001));

        await EventsAsync(store, r => r.HandleAsync(Webhook(new DependencyCommandIssued(RepositoryId, 40, 98, EDependencyCommand.Hold, "nx", "octo"))));

        Reactions.Added.Should().BeEmpty("other bots read /hold too");
    }

    [Fact]
    public async Task A_completed_run_signals_only_the_run_waiting_for_it()
    {
        using var store = GetDocumentStore();
        var (main, mainId) = Run(configure: r => r.Runs.Add(new DependencyWorkflowRun { RunId = 501 }));
        var (line, lineId) = Run(Line, r => r.Runs.Add(new DependencyWorkflowRun { RunId = 502 }));
        await SeedAsync(store, (main, mainId), (line, lineId));

        var bus = await EventsAsync(store, r => r.HandleAsync(Webhook(new CiRunCompleted(RepositoryId, 501, new string('a', 40), "success"))));

        var signal = bus.Signals.Should().ContainSingle().Which.Signal;
        signal.Line.Should().BeNull();
        signal.Kind.Should().Be(EDependencySignal.RunCompleted);
        signal.RunId.Should().Be(501);
    }

    [Fact]
    public async Task Changed_checks_signal_a_run_awaiting_or_bisecting_on_that_rolling_commit_case_insensitively()
    {
        using var store = GetDocumentStore();
        var sha = new string('c', 40);
        var (main, mainId) = Run(configure: r => { r.State = EDependencyRunState.AwaitingChecks; r.RollingSha = sha.ToUpperInvariant(); });
        var (line, lineId) = Run(Line, r => { r.State = EDependencyRunState.Bisecting; r.RollingSha = sha; });
        var (other, otherId) = Run("angular-21.x", r => { r.State = EDependencyRunState.Idle; r.RollingSha = sha; });
        await SeedAsync(store, (main, mainId), (line, lineId), (other, otherId));

        var bus = await EventsAsync(store, r => r.HandleAsync(Webhook(new CommitChecksChanged(RepositoryId, sha))));

        bus.Signals.Select(s => s.Signal.Line).Should().BeEquivalentTo(new[] { null, Line });
        bus.Signals.Should().AllSatisfy(s => s.Signal.Kind.Should().Be(EDependencySignal.ChecksChanged));
    }

    [Fact]
    public async Task A_merged_or_closed_pull_request_signals_the_run_whose_update_or_onboarding_it_is()
    {
        using var store = GetDocumentStore();
        var (main, mainId) = Run(configure: r => r.OnboardingPullRequestNumber = 21);
        var (line, lineId) = Run(Line, r => r.PullRequestNumber = 22);
        await SeedAsync(store, (main, mainId), (line, lineId));

        var merged = await EventsAsync(store, r => r.HandleAsync(Webhook(new PullRequestMerged(RepositoryId, 21, "x", true))));
        var closed = await EventsAsync(store, r => r.HandleAsync(Webhook(new PullRequestClosed(RepositoryId, 22))));
        var unrelated = await EventsAsync(store, r => r.HandleAsync(Webhook(new PullRequestClosed(RepositoryId, 23))));

        var m = merged.Signals.Should().ContainSingle().Which.Signal;
        (m.Kind, m.Line, m.Number).Should().Be((EDependencySignal.Merged, (string?)null, (int?)21));
        var c = closed.Signals.Should().ContainSingle().Which.Signal;
        (c.Kind, c.Line, c.Number).Should().Be((EDependencySignal.Closed, Line, (int?)22));
        unrelated.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task An_event_for_a_repository_id_outside_the_collection_loads_nothing_and_signals_nothing()
    {
        using var store = GetDocumentStore();

        var bus = await EventsAsync(store, r => r.HandleAsync(Webhook(new PullRequestClosed("projects/42", 1))));

        bus.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task An_alert_starts_a_security_run_on_the_default_branch_and_on_every_line_with_a_run()
    {
        using var store = GetDocumentStore();
        var (repository, repositoryId) = Repo(RepositoryId, 7001);
        var (line, lineId) = Run(Line);
        await SeedAsync(store, (new Account { GitHubId = 7001, Login = "acme" }, AccountId), (repository, repositoryId), (line, lineId));

        var bus = await EventsAsync(store, r => r.HandleAsync(Webhook(new DependencyAlertRaised(RepositoryId, "npm", "left-pad", "GHSA-1"))));

        bus.Signals.Select(s => s.Signal.Line).Should().Equal(new string?[] { null, Line });
        bus.Signals.Should().AllSatisfy(s =>
        {
            s.Signal.Kind.Should().Be(EDependencySignal.Trigger);
            s.Signal.Trigger.Should().Be(EDependencyRunTrigger.Security);
            var fix = s.Signal.SecurityFixes.Should().ContainSingle().Which;
            (fix.Ecosystem, fix.DepName, fix.Advisory).Should().Be(("npm", "left-pad", (string?)"GHSA-1"));
        });
    }

    [Fact]
    public async Task An_alert_is_ignored_for_an_unknown_repository_or_one_with_updates_off()
    {
        using var store = GetDocumentStore();
        var (repository, repositoryId) = Repo(RepositoryId, 7001, dependencyUpdates: false);
        await SeedAsync(store, (repository, repositoryId));

        var off = await EventsAsync(store, r => r.HandleAsync(Webhook(new DependencyAlertRaised(RepositoryId, "npm", "left-pad", null))));
        var unknown = await EventsAsync(store, r => r.HandleAsync(Webhook(new DependencyAlertRaised(ConsumerId, "npm", "left-pad", null))));

        off.Sent.Should().BeEmpty();
        unknown.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task A_producers_release_and_default_branch_push_become_producer_events()
    {
        using var store = GetDocumentStore();
        var (repository, repositoryId) = Repo(RepositoryId, 7001);
        await SeedAsync(store, (repository, repositoryId), Manifest(RepositoryId, produces: [new ManifestPackage { Ecosystem = "npm", Name = "@acme/core" }]));
        var publishedAt = new DateTimeOffset(2026, 11, 7, 9, 0, 0, TimeSpan.FromHours(2));

        var release = await EventsAsync(store, r => r.HandleAsync(Webhook(new ReleasePublished(RepositoryId, "v1.2.0", publishedAt))));
        var push = await EventsAsync(store, r => r.HandleAsync(Webhook(new BranchCommitPushed(RepositoryId, "main", new string('d', 40), null, null))));

        var r = release.Sent.Should().ContainSingle().Which.Message.Should().BeOfType<DependencyProducerEventMessage>().Which;
        (r.AccountId, r.ProducerRepositoryId, r.Kind, r.Reference, r.AtUtc).Should().Be((AccountId, RepositoryId, "release", (string?)"v1.2.0", publishedAt.UtcDateTime));
        var p = push.Sent.Should().ContainSingle().Which.Message.Should().BeOfType<DependencyProducerEventMessage>().Which;
        (p.Kind, p.Reference).Should().Be(("push", (string?)new string('d', 40)));
    }

    [Theory]
    [InlineData("feature/x")]
    [InlineData("mintplayer/dependencies")]
    [InlineData("mq/main/12")]
    public async Task A_push_to_another_branch_or_a_bot_branch_is_not_a_producer_event(string branch)
    {
        using var store = GetDocumentStore();
        var (repository, repositoryId) = Repo(RepositoryId, 7001);
        await SeedAsync(store, (repository, repositoryId), Manifest(RepositoryId, produces: [new ManifestPackage { Ecosystem = "npm", Name = "@acme/core" }]));

        var bus = await EventsAsync(store, r => r.HandleAsync(Webhook(new BranchCommitPushed(RepositoryId, branch, new string('d', 40), null, null))));

        bus.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task A_repository_that_produces_nothing_or_has_no_account_is_not_a_producer()
    {
        using var store = GetDocumentStore();
        var (repository, repositoryId) = Repo(RepositoryId, 7001);
        var (orphan, orphanId) = Repo(ConsumerId, 7002);
        orphan.Account = null;
        await SeedAsync(store, (repository, repositoryId), Manifest(RepositoryId), (orphan, orphanId));

        var none = await EventsAsync(store, r => r.HandleAsync(Webhook(new ReleasePublished(RepositoryId, "v1", DateTimeOffset.UtcNow))));
        var noAccount = await EventsAsync(store, r => r.HandleAsync(Webhook(new ReleasePublished(ConsumerId, "v1", DateTimeOffset.UtcNow))));

        none.Sent.Should().BeEmpty();
        noAccount.Sent.Should().BeEmpty();
    }

    // ── DependencyPropagation (§7.6) ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Recording_keeps_the_newest_events_per_producer_and_queues_the_debounced_propagation()
    {
        using var store = GetDocumentStore();
        var outbox = Substitute.For<IMessageOutbox>();
        var start = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);

        for (var i = 0; i < DependencyPropagation.EventsPerProducer + 3; i++)
        {
            using var session = store.OpenAsyncSession();
            var propagation = new DependencyPropagation(session, new DependencyStateStore(session), outbox, new RecordingBus());
            await propagation.RecordAsync(new DependencyProducerEventMessage { AccountId = AccountId, ProducerRepositoryId = RepositoryId, Kind = "push", Reference = $"r{i}", AtUtc = start.AddMinutes(i) });
        }

        using var check = store.OpenAsyncSession();
        var fleet = await check.LoadAsync<DependencyFleetState>(DependencyFleetState.ForAccount(AccountId));
        fleet.ProducerEvents.Should().HaveCount(DependencyPropagation.EventsPerProducer);
        fleet.ProducerEvents.Min(e => e.AtUtc).Should().Be(start.AddMinutes(3));
        await outbox.Received(DependencyPropagation.EventsPerProducer + 3).EnqueueAsync(
            Arg.Any<Raven.Client.Documents.Session.IAsyncDocumentSession>(),
            Arg.Is<DependencyPropagationMessage>(m => m.AccountId == AccountId && m.ProducerRepositoryId == RepositoryId),
            Arg.Is<BroadcastOptions?>(o => o!.Delay == DependencyPropagation.Debounce),
            Arg.Any<CancellationToken>());
    }

    private static async Task<RecordingBus> PropagateAsync(IDocumentStore store, DateTime eventAtUtc)
    {
        var bus = new RecordingBus();
        using var session = store.OpenAsyncSession();
        var propagation = new DependencyPropagation(session, new DependencyStateStore(session), Substitute.For<IMessageOutbox>(), bus);
        await propagation.PropagateAsync(new DependencyPropagationMessage { AccountId = AccountId, ProducerRepositoryId = RepositoryId, EventAtUtc = eventAtUtc });
        return bus;
    }

    private static async Task SeedFleetAsync(IDocumentStore store, DateTime latest)
    {
        var fleet = new DependencyFleetState { Id = DependencyFleetState.ForAccount(AccountId) };
        fleet.ProducerEvents.Add(new DependencyProducerEvent { ProducerRepository = RepositoryId, Kind = "release", AtUtc = latest });
        await SeedAsync(store, (fleet, fleet.Id));
    }

    [Fact]
    public async Task Propagation_starts_an_own_release_run_on_each_connected_consumer_of_a_produced_package_once()
    {
        using var store = GetDocumentStore();
        await new global::CodeCoverage.Indexes.Repositories_Overview().ExecuteAsync(store);
        var at = new DateTime(2026, 11, 7, 8, 0, 0, DateTimeKind.Utc);
        var (producer, producerId) = Repo(RepositoryId, 7001);
        var (consumer, consumerId) = Repo(ConsumerId, 7002);
        var unrelatedId = Repository.DocumentId(EForgeProvider.GitHub, 7003);
        var (unrelated, _) = Repo(unrelatedId, 7003);
        var archivedId = Repository.DocumentId(EForgeProvider.GitHub, 7004);
        var (archived, _) = Repo(archivedId, 7004);
        archived.Archived = true;
        await SeedAsync(store,
            (producer, producerId), (consumer, consumerId), (unrelated, unrelatedId), (archived, archivedId),
            Manifest(RepositoryId, produces: [new ManifestPackage { Ecosystem = "NuGet", Name = "Acme.Core" }]),
            Manifest(ConsumerId, consumes: [new ManifestDependency { Ecosystem = "nuget", Name = "Acme.Core" }]),
            Manifest(unrelatedId, consumes: [new ManifestDependency { Ecosystem = "npm", Name = "Acme.Core" }]),
            Manifest(archivedId, consumes: [new ManifestDependency { Ecosystem = "nuget", Name = "Acme.Core" }]));
        await SeedFleetAsync(store, at);
        WaitForIndexing(store);

        var bus = await PropagateAsync(store, at);

        var (signal, options) = bus.Signals.Should().ContainSingle().Which;
        signal.RepositoryId.Should().Be(ConsumerId);
        signal.Kind.Should().Be(EDependencySignal.Trigger);
        signal.Trigger.Should().Be(EDependencyRunTrigger.OwnRelease);
        options.DeduplicationKey.Should().Be($"dependency-own:{ConsumerId}:{RepositoryId}:{at.Ticks}");
    }

    [Fact]
    public async Task Propagation_of_an_older_event_than_the_latest_or_of_an_unknown_fleet_does_nothing()
    {
        using var store = GetDocumentStore();
        var at = new DateTime(2026, 11, 7, 8, 0, 0, DateTimeKind.Utc);

        (await PropagateAsync(store, at)).Sent.Should().BeEmpty("there is no fleet state");

        await SeedFleetAsync(store, at.AddMinutes(5));
        (await PropagateAsync(store, at)).Sent.Should().BeEmpty("a newer event queued its own propagation");
    }

    [Fact]
    public async Task Propagation_stops_when_the_producer_has_no_manifest_or_no_package()
    {
        using var store = GetDocumentStore();
        var at = new DateTime(2026, 11, 7, 8, 0, 0, DateTimeKind.Utc);
        await SeedFleetAsync(store, at);

        (await PropagateAsync(store, at)).Sent.Should().BeEmpty("there is no manifest");

        await SeedAsync(store, Manifest(RepositoryId));
        (await PropagateAsync(store, at)).Sent.Should().BeEmpty("the producer declares no package");
    }
}
