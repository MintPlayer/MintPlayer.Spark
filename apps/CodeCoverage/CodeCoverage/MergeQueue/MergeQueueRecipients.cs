using CodeCoverage.Entities;
using CodeCoverage.Forge;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Messaging.Abstractions;
using Raven.Client.Documents.Session;
using QueueDocument = CodeCoverage.Entities.MergeQueue;

namespace CodeCoverage.MergeQueue;

/// <summary>The saga's own lane: every step and every signal goes through <see cref="IMergeQueueEngine"/>.</summary>
public partial class MergeQueueRecipient :
    IRecipient<MergeQueueStepMessage>,
    IRecipient<MergeQueueSignalMessage>
{
    [Inject] private readonly IMergeQueueEngine engine;

    public Task HandleAsync(MergeQueueStepMessage message, CancellationToken cancellationToken = default)
        => engine.HandleStepAsync(message, cancellationToken);

    public Task HandleAsync(MergeQueueSignalMessage message, CancellationToken cancellationToken = default)
        => engine.HandleSignalAsync(message, cancellationToken);
}

/// <summary>
/// Turns the neutral forge events the queue cares about into signals on the saga's lane (merge-queue PRD M5).
/// </summary>
/// <remarks>
/// <para>
/// <b>A filter, then a forward.</b> This runs on the shared forge-event lane, so it only decides, with point loads by
/// deterministic id, whether a queue could care: a pull request that has an active entry, a repository whose queue is
/// busy. Everything that changes queue state happens on <see cref="Feedback.CoverageQueues.MergeQueue"/>, serialised with
/// the steps (D11).
/// </para>
/// <para>
/// A command is always forwarded: whether a label is the configured one is only known after reading
/// <c>coverage.yml</c>, which the saga does. M6's button and the check's button (§0 O30) raise the same
/// <see cref="MergeQueueCommandIssued"/>, so every enqueue path shares one permission check (O8).
/// </para>
/// </remarks>
public partial class MergeQueueEventsRecipient :
    IRecipient<ForgeWebhookMessage<MergeQueueCommandIssued>>,
    IRecipient<ForgeWebhookMessage<PullRequestUpdated>>,
    IRecipient<ForgeWebhookMessage<PullRequestClosed>>,
    IRecipient<ForgeWebhookMessage<PullRequestMerged>>,
    IRecipient<ForgeWebhookMessage<BranchCommitPushed>>,
    IRecipient<ForgeWebhookMessage<CiRunCompleted>>,
    IRecipient<ForgeWebhookMessage<CommitChecksChanged>>,
    IRecipient<ForgeWebhookMessage<PullRequestReviewed>>
{
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly IMessageBus messageBus;

    public Task HandleAsync(ForgeWebhookMessage<MergeQueueCommandIssued> message, CancellationToken cancellationToken = default)
    {
        var evt = message.Event;
        return messageBus.BroadcastAsync(new MergeQueueSignalMessage
        {
            RepositoryId = evt.RepositoryId,
            Kind = EMergeQueueSignal.Command,
            Number = evt.Number,
            Command = evt.Command,
            Actor = evt.ActorLogin,
            Source = evt.Source,
            Label = evt.Label,
        }, cancellationToken);
    }

    /// <remarks>
    /// A queued pull request's new head is the saga's to judge (T3). Any other pull request may be offered the queue's
    /// button (§0 O30): whether its repository has a queue at all is only known after reading <c>coverage.yml</c> on the
    /// default branch, which the saga does, like for a command.
    /// </remarks>
    public async Task HandleAsync(ForgeWebhookMessage<PullRequestUpdated> message, CancellationToken cancellationToken = default)
    {
        var evt = message.Event;
        var kind = await HasActiveEntryAsync(evt.RepositoryId, evt.Number, cancellationToken) ? EMergeQueueSignal.HeadPushed : EMergeQueueSignal.Offer;
        await SignalAsync(evt.RepositoryId, kind, evt.Number, evt.HeadSha, cancellationToken: cancellationToken);
    }

    public async Task HandleAsync(ForgeWebhookMessage<PullRequestClosed> message, CancellationToken cancellationToken = default)
    {
        var evt = message.Event;
        if (await HasActiveEntryAsync(evt.RepositoryId, evt.Number, cancellationToken))
            await SignalAsync(evt.RepositoryId, EMergeQueueSignal.Closed, evt.Number, cancellationToken: cancellationToken);
    }

    public async Task HandleAsync(ForgeWebhookMessage<PullRequestMerged> message, CancellationToken cancellationToken = default)
    {
        var evt = message.Event;
        if (await HasActiveEntryAsync(evt.RepositoryId, evt.Number, cancellationToken))
            await SignalAsync(evt.RepositoryId, EMergeQueueSignal.Merged, evt.Number, sha: evt.MergeCommitSha, cancellationToken: cancellationToken);
    }

    public async Task HandleAsync(ForgeWebhookMessage<BranchCommitPushed> message, CancellationToken cancellationToken = default)
    {
        var evt = message.Event;
        // The queue's own branches move with every prepare; only a lane's base moving matters.
        if (evt.Branch.StartsWith("mq/", StringComparison.Ordinal)) return;
        var queue = await session.LoadAsync<QueueDocument>(QueueDocument.DocumentId(evt.RepositoryId), cancellationToken);
        if (queue?.Lanes.Any(l => l.BaseBranch == evt.Branch && l.Current is not null) == true)
            await SignalAsync(evt.RepositoryId, EMergeQueueSignal.BranchPushed, sha: evt.Sha, branch: evt.Branch, cancellationToken: cancellationToken);
    }

    public async Task HandleAsync(ForgeWebhookMessage<CiRunCompleted> message, CancellationToken cancellationToken = default)
    {
        var evt = message.Event;
        if (await IsBusyAsync(evt.RepositoryId, cancellationToken))
            await SignalAsync(evt.RepositoryId, EMergeQueueSignal.RunCompleted, sha: evt.HeadSha, runId: evt.RunId, cancellationToken: cancellationToken);
    }

    public async Task HandleAsync(ForgeWebhookMessage<CommitChecksChanged> message, CancellationToken cancellationToken = default)
    {
        var evt = message.Event;
        if (await IsBusyAsync(evt.RepositoryId, cancellationToken))
            await SignalAsync(evt.RepositoryId, EMergeQueueSignal.ChecksChanged, sha: evt.Sha, cancellationToken: cancellationToken);
    }

    public async Task HandleAsync(ForgeWebhookMessage<PullRequestReviewed> message, CancellationToken cancellationToken = default)
    {
        var evt = message.Event;
        if (await HasActiveEntryAsync(evt.RepositoryId, evt.Number, cancellationToken))
            await SignalAsync(evt.RepositoryId, EMergeQueueSignal.Reviewed, evt.Number, cancellationToken: cancellationToken);
    }

    private async Task<bool> HasActiveEntryAsync(string repositoryId, int number, CancellationToken ct)
        => await session.LoadAsync<MergeQueueEntry>(MergeQueueEntry.DocumentId(repositoryId, number), ct) is { } entry && !entry.State.IsTerminal();

    private async Task<bool> IsBusyAsync(string repositoryId, CancellationToken ct)
        => await session.LoadAsync<QueueDocument>(QueueDocument.DocumentId(repositoryId), ct) is { } queue && queue.IsBusy();

    private Task SignalAsync(string repositoryId, EMergeQueueSignal kind, int? number = null, string? sha = null, string? branch = null, long? runId = null, CancellationToken cancellationToken = default)
        => messageBus.BroadcastAsync(new MergeQueueSignalMessage
        {
            RepositoryId = repositoryId,
            Kind = kind,
            Number = number,
            Sha = sha,
            Branch = branch,
            RunId = runId,
        }, cancellationToken);
}
