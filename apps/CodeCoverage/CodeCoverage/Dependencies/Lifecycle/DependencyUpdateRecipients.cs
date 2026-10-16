using CodeCoverage.Dependencies.Policy;
using CodeCoverage.Entities;
using CodeCoverage.Feedback;
using CodeCoverage.Forge;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Messaging;
using MintPlayer.Spark.Messaging.Abstractions;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;

namespace CodeCoverage.Dependencies.Lifecycle;

/// <summary>The saga's own lane: every step, every signal and the propagation messages.</summary>
public partial class DependencyUpdateRecipient :
    IRecipient<DependencyUpdateStepMessage>,
    IRecipient<DependencyUpdateSignalMessage>,
    IRecipient<DependencyProducerEventMessage>,
    IRecipient<DependencyPropagationMessage>
{
    [Inject] private readonly IDependencyUpdateEngine engine;
    [Inject] private readonly IDependencyPropagation propagation;

    public Task HandleAsync(DependencyUpdateStepMessage message, CancellationToken cancellationToken = default)
        => engine.HandleStepAsync(message, cancellationToken);

    public Task HandleAsync(DependencyUpdateSignalMessage message, CancellationToken cancellationToken = default)
        => engine.HandleSignalAsync(message, cancellationToken);

    public Task HandleAsync(DependencyProducerEventMessage message, CancellationToken cancellationToken = default)
        => propagation.RecordAsync(message, cancellationToken);

    public Task HandleAsync(DependencyPropagationMessage message, CancellationToken cancellationToken = default)
        => propagation.PropagateAsync(message, cancellationToken);
}

/// <summary>A producer's release or default-branch push, forwarded onto the saga's lane so the fleet state is written in one place.</summary>
[MessageQueue(CoverageQueues.DependencyUpdates)]
public sealed record DependencyProducerEventMessage
{
    public required string AccountId { get; init; }
    public required string ProducerRepositoryId { get; init; }
    public required string Kind { get; init; }
    public string? Reference { get; init; }
    public required DateTime AtUtc { get; init; }
}

/// <summary>
/// Turns the neutral forge events the updater cares about into signals on its lane. A filter, then a forward: point loads by
/// id decide whether a run could care (the repository's run document names the pull request or the workflow run).
/// </summary>
public partial class DependencyUpdateEventsRecipient :
    IRecipient<ForgeWebhookMessage<DependencyCommandIssued>>,
    IRecipient<ForgeWebhookMessage<CiRunCompleted>>,
    IRecipient<ForgeWebhookMessage<CommitChecksChanged>>,
    IRecipient<ForgeWebhookMessage<PullRequestMerged>>,
    IRecipient<ForgeWebhookMessage<PullRequestClosed>>,
    IRecipient<ForgeWebhookMessage<DependencyAlertRaised>>,
    IRecipient<ForgeWebhookMessage<ReleasePublished>>,
    IRecipient<ForgeWebhookMessage<BranchCommitPushed>>
{
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly IMessageBus messageBus;

    public async Task HandleAsync(ForgeWebhookMessage<DependencyCommandIssued> message, CancellationToken cancellationToken = default)
    {
        var evt = message.Event;
        if (await RunAsync(evt.RepositoryId, cancellationToken) is not { } run || run.PullRequestNumber != evt.Number) return;
        await SignalAsync(new DependencyUpdateSignalMessage
        {
            RepositoryId = evt.RepositoryId, Kind = EDependencySignal.Command, Number = evt.Number, CommentId = evt.CommentId,
            Command = evt.Command, Argument = evt.Argument, Actor = evt.ActorLogin,
        }, cancellationToken);
    }

    public async Task HandleAsync(ForgeWebhookMessage<CiRunCompleted> message, CancellationToken cancellationToken = default)
    {
        var evt = message.Event;
        if (await RunAsync(evt.RepositoryId, cancellationToken) is { } run && run.Pending()?.RunId == evt.RunId)
            await SignalAsync(new DependencyUpdateSignalMessage { RepositoryId = evt.RepositoryId, Kind = EDependencySignal.RunCompleted, RunId = evt.RunId }, cancellationToken);
    }

    public async Task HandleAsync(ForgeWebhookMessage<CommitChecksChanged> message, CancellationToken cancellationToken = default)
    {
        var evt = message.Event;
        if (await RunAsync(evt.RepositoryId, cancellationToken) is { State: EDependencyRunState.AwaitingChecks or EDependencyRunState.Bisecting } run
            && string.Equals(run.RollingSha, evt.Sha, StringComparison.OrdinalIgnoreCase))
            await SignalAsync(new DependencyUpdateSignalMessage { RepositoryId = evt.RepositoryId, Kind = EDependencySignal.ChecksChanged, Sha = evt.Sha }, cancellationToken);
    }

    public async Task HandleAsync(ForgeWebhookMessage<PullRequestMerged> message, CancellationToken cancellationToken = default)
    {
        var evt = message.Event;
        if (await RunAsync(evt.RepositoryId, cancellationToken) is { } run && (run.PullRequestNumber == evt.Number || run.OnboardingPullRequestNumber == evt.Number))
            await SignalAsync(new DependencyUpdateSignalMessage { RepositoryId = evt.RepositoryId, Kind = EDependencySignal.Merged, Number = evt.Number }, cancellationToken);
    }

    public async Task HandleAsync(ForgeWebhookMessage<PullRequestClosed> message, CancellationToken cancellationToken = default)
    {
        var evt = message.Event;
        if (await RunAsync(evt.RepositoryId, cancellationToken) is { } run && (run.PullRequestNumber == evt.Number || run.OnboardingPullRequestNumber == evt.Number))
            await SignalAsync(new DependencyUpdateSignalMessage { RepositoryId = evt.RepositoryId, Kind = EDependencySignal.Closed, Number = evt.Number }, cancellationToken);
    }

    /// <summary>R10: a vulnerable dependency starts an out-of-slot security run (or waits for the current one).</summary>
    public async Task HandleAsync(ForgeWebhookMessage<DependencyAlertRaised> message, CancellationToken cancellationToken = default)
    {
        var evt = message.Event;
        if (await session.LoadAsync<Repository>(evt.RepositoryId, cancellationToken) is not { } repository) return;
        var account = repository.Account is null ? null : await session.LoadAsync<Account>(repository.Account, cancellationToken);
        if (!Repository.ResolveDependencyUpdates(repository, account)) return;
        await SignalAsync(new DependencyUpdateSignalMessage
        {
            RepositoryId = evt.RepositoryId, Kind = EDependencySignal.Trigger, Trigger = EDependencyRunTrigger.Security,
            SecurityFixes = [new DependencySecurityFixRecord { Ecosystem = evt.Ecosystem, DepName = evt.DepName, Advisory = evt.Advisory }],
        }, cancellationToken);
    }

    public Task HandleAsync(ForgeWebhookMessage<ReleasePublished> message, CancellationToken cancellationToken = default)
        => ProducerAsync(message.Event.RepositoryId, "release", message.Event.Tag, message.Event.PublishedAt.UtcDateTime, null, cancellationToken);

    public Task HandleAsync(ForgeWebhookMessage<BranchCommitPushed> message, CancellationToken cancellationToken = default)
        => message.Event.Branch.StartsWith("mintplayer/", StringComparison.Ordinal) || message.Event.Branch.StartsWith("mq/", StringComparison.Ordinal)
            ? Task.CompletedTask
            : ProducerAsync(message.Event.RepositoryId, "push", message.Event.Sha, DateTime.UtcNow, message.Event.Branch, cancellationToken);

    /// <summary>§7.6: only a repository on an account that declares a package is a producer; only its default branch publishes.</summary>
    private async Task ProducerAsync(string repositoryId, string kind, string? reference, DateTime atUtc, string? branch, CancellationToken ct)
    {
        if (await session.LoadAsync<Repository>(repositoryId, ct) is not { Account: { } accountId } repository) return;
        if (branch is not null && branch != repository.DefaultBranch) return;
        if (await session.LoadAsync<RepositoryManifest>(RepositoryManifest.ForRepository(repositoryId), ct) is not { Produces.Count: > 0 }) return;
        await messageBus.BroadcastAsync(new DependencyProducerEventMessage
        {
            AccountId = accountId, ProducerRepositoryId = repositoryId, Kind = kind, Reference = reference, AtUtc = atUtc,
        }, ct);
    }

    private async Task<DependencyUpdateRun?> RunAsync(string repositoryId, CancellationToken ct)
        => repositoryId.StartsWith("Repositories/", StringComparison.OrdinalIgnoreCase)
            ? await session.LoadAsync<DependencyUpdateRun>(DependencyUpdateRun.ForRepository(repositoryId), ct)
            : null;

    private Task SignalAsync(DependencyUpdateSignalMessage signal, CancellationToken ct) => messageBus.BroadcastAsync(signal, ct);
}

public interface IDependencyPropagation
{
    /// <summary>Records a producer event in the account's fleet state, and queues the debounced propagation 30 minutes later.</summary>
    Task RecordAsync(DependencyProducerEventMessage message, CancellationToken cancellationToken = default);

    /// <summary>The debounce fired: when nothing newer came from the producer, each consumer on the account gets an own-release run.</summary>
    Task PropagateAsync(DependencyPropagationMessage message, CancellationToken cancellationToken = default);
}

/// <summary>
/// §7.6's release propagation: a producer's release or default-branch push starts its consumers' runs, debounced so a burst
/// of releases makes one update, 30 minutes after the last. The own-package check itself (gap E) is the saga's, at plan time
/// (<see cref="OwnReleaseCheck"/>): the event is one of its three legs, recorded here.
/// </summary>
[Register(typeof(IDependencyPropagation), ServiceLifetime.Scoped)]
public partial class DependencyPropagation : IDependencyPropagation
{
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly IDependencyStateStore stateStore;
    [Inject] private readonly IMessageOutbox outbox;
    [Inject] private readonly IMessageBus messageBus;

    public static readonly TimeSpan Debounce = TimeSpan.FromMinutes(30);

    /// <summary>Producer events kept per producer: enough for the own-package window, bounded.</summary>
    public const int EventsPerProducer = 20;

    public async Task RecordAsync(DependencyProducerEventMessage message, CancellationToken cancellationToken = default)
    {
        var fleet = await stateStore.LoadFleetAsync(message.AccountId, cancellationToken);
        fleet.ProducerEvents.Add(new DependencyProducerEvent { ProducerRepository = message.ProducerRepositoryId, Kind = message.Kind, Reference = message.Reference, AtUtc = message.AtUtc });
        var mine = fleet.ProducerEvents.Where(e => e.ProducerRepository == message.ProducerRepositoryId).OrderByDescending(e => e.AtUtc).Skip(EventsPerProducer).ToHashSet();
        fleet.ProducerEvents.RemoveAll(mine.Contains);
        if (fleet.ProducerEvents.Count > DependencyFleetState.MaxEntries)
            fleet.ProducerEvents = [.. fleet.ProducerEvents.OrderByDescending(e => e.AtUtc).Take(DependencyFleetState.MaxEntries)];
        await outbox.EnqueueAsync(session, new DependencyPropagationMessage
        {
            AccountId = message.AccountId, ProducerRepositoryId = message.ProducerRepositoryId, EventAtUtc = message.AtUtc,
        }, new BroadcastOptions { Delay = Debounce }, cancellationToken);
        await session.SaveChangesAsync(cancellationToken);
    }

    public async Task PropagateAsync(DependencyPropagationMessage message, CancellationToken cancellationToken = default)
    {
        var fleet = await session.LoadAsync<DependencyFleetState>(DependencyFleetState.ForAccount(message.AccountId), cancellationToken);
        var latest = fleet?.ProducerEvents.Where(e => e.ProducerRepository == message.ProducerRepositoryId).Select(e => e.AtUtc).DefaultIfEmpty().Max();
        // Debounced: a newer event queued its own propagation.
        if (latest is null || latest > message.EventAtUtc) return;

        var producer = await session.LoadAsync<RepositoryManifest>(RepositoryManifest.ForRepository(message.ProducerRepositoryId), cancellationToken);
        if (producer is null || producer.Produces.Count == 0) return;
        var produced = producer.Produces.Select(p => (Eco: p.Ecosystem.ToLowerInvariant(), p.Name)).ToHashSet();

        var repositories = await session.Query<Repository, Indexes.Repositories_Overview>()
            .Where(r => r.OwnerKey == producer.OwnerKey)
            .Take(1024)
            .ToListAsync(cancellationToken);
        var candidates = repositories.Where(r => r.Id is not null && r.Id != message.ProducerRepositoryId && r.Connection != RepositoryConnection.Disconnected && !r.Archived).ToList();
        if (candidates.Count == 0) return;
        var manifests = await session.LoadAsync<RepositoryManifest>(candidates.Select(r => RepositoryManifest.ForRepository(r.Id!)), cancellationToken);
        foreach (var consumer in candidates)
        {
            if (manifests.GetValueOrDefault(RepositoryManifest.ForRepository(consumer.Id!)) is not { } manifest
                || !manifest.Consumes.Any(d => produced.Contains((d.Ecosystem.ToLowerInvariant(), d.Name))))
                continue;
            await messageBus.BroadcastOnceAsync(new DependencyUpdateSignalMessage
            {
                RepositoryId = consumer.Id!, Kind = EDependencySignal.Trigger, Trigger = EDependencyRunTrigger.OwnRelease,
            }, $"dependency-own:{consumer.Id}:{message.ProducerRepositoryId}:{message.EventAtUtc.Ticks}", cancellationToken);
        }
    }
}
