using CodeCoverage.Entities;
using CodeCoverage.Feedback;
using CodeCoverage.Forge;
using CodeCoverage.MergeQueue;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Messaging.Abstractions;

namespace CodeCoverage.PackageVersioning;

/// <summary>The onboarding pull request merged (#153 M13, D17): push its seed tags, on the versioning lane.</summary>
[MessageQueue(CoverageQueues.Versioning)]
public sealed record VersioningSeedsMessage
{
    public required string RepositoryId { get; init; }
}

public partial class VersioningSeedsEventsRecipient : IRecipient<ForgeWebhookMessage<PullRequestMerged>>
{
    [Inject] private readonly IMessageBus messageBus;

    public Task HandleAsync(ForgeWebhookMessage<PullRequestMerged> message, CancellationToken cancellationToken = default)
    {
        var evt = message.Event;
        // #571 D3: the chooser's pull request carries the seeds now; a #153 one opened before it still may.
        if (!evt.HeadIsFromSameRepository || evt.HeadRef is not (VersioningOnboarding.Branch or Onboarding.OnboardingPlanner.Branch)) return Task.CompletedTask;
        return messageBus.BroadcastOnceAsync(new VersioningSeedsMessage { RepositoryId = evt.RepositoryId },
            $"versioning-seeds:{evt.RepositoryId}:{evt.Number}", cancellationToken);
    }
}

/// <summary>
/// Pushes the seeds under the run lock, so a versioning run on the merge commit waits for them. Whatever comes first does
/// the push: this message, or the run itself (<see cref="VersioningController"/>); the other finds nothing pending.
/// </summary>
public partial class VersioningSeedsRecipient : IRecipient<VersioningSeedsMessage>
{
    [Inject] private readonly Raven.Client.Documents.Session.IAsyncDocumentSession session;
    [Inject] private readonly Onboarding.IOnboardingService onboarding;
    [Inject] private readonly VersioningRunLocks locks;
    [Inject] private readonly ILogger<VersioningSeedsRecipient> logger;

    public async Task HandleAsync(VersioningSeedsMessage message, CancellationToken cancellationToken = default)
    {
        var repository = await session.LoadAsync<Repository>(message.RepositoryId, cancellationToken);
        if (repository is null || repository.Connection == RepositoryConnection.Disconnected || repository.Archived) return;
        using var held = await locks.TryEnterAsync(repository.Id!, cancellationToken)
            ?? throw new GitWriterUnavailableException("A versioning run of this repository is still going.");
        // #571 M10d: the seeds, then the chooser's settings, renames and line workflows (only the seeds for a #153 pull request).
        // #649 D9: nobody watches a merge's outcome, so it is logged; a failed step as a warning.
        var outcome = await onboarding.MergedAsync(repository, cancellationToken);
        if (outcome.HasFailures)
            logger.LogWarning("Onboarding merge of {Repository}: {Note}", repository.FullName, outcome.Note);
        else
            logger.LogInformation("Onboarding merge of {Repository} ({Outcome}): {Note}", repository.FullName, outcome.Outcome, outcome.Note);
    }
}
