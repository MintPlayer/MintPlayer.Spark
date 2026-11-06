using CodeCoverage.Entities;
using CodeCoverage.Feedback;
using CodeCoverage.Forge;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Messaging.Abstractions;
using Raven.Client.Documents.Session;

namespace CodeCoverage.Onboarding;

/// <summary>The default branch moved under an open onboarding pull request (#571 D3): rebuild it on the new head.</summary>
[MessageQueue(CoverageQueues.Versioning)]
public sealed record OnboardingRebuildMessage
{
    public required string RepositoryId { get; init; }
}

/// <summary>
/// Forwards a push to the default branch of a repository whose chooser pull request is open. The pull request is rebuilt
/// on the new head instead of merging it in (§8.4 "M10"), so the bot never conflicts with what landed on the default branch.
/// </summary>
public partial class OnboardingRebuildEventsRecipient : IRecipient<ForgeWebhookMessage<BranchCommitPushed>>
{
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly IMessageBus messageBus;

    public async Task HandleAsync(ForgeWebhookMessage<BranchCommitPushed> message, CancellationToken cancellationToken = default)
    {
        var evt = message.Event;
        if (await session.LoadAsync<Repository>(evt.RepositoryId, cancellationToken) is not { DefaultBranch: { } defaultBranch } repository
            || evt.Branch != defaultBranch || repository.Connection == RepositoryConnection.Disconnected || repository.Archived)
            return;
        var state = await session.LoadAsync<VersioningOnboardingState>(VersioningOnboardingState.ForRepository(repository.Id!), cancellationToken);
        if (state is not { Features.Count: > 0, PullRequestNumber: not null, MergeHandledAt: null }) return;
        await messageBus.BroadcastOnceAsync(new OnboardingRebuildMessage { RepositoryId = repository.Id! },
            $"onboarding-rebuild:{repository.Id}:{evt.Sha}", cancellationToken);
    }
}

public partial class OnboardingRebuildRecipient : IRecipient<OnboardingRebuildMessage>
{
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly IOnboardingService onboarding;
    [Inject] private readonly ILogger<OnboardingRebuildRecipient> logger;

    public async Task HandleAsync(OnboardingRebuildMessage message, CancellationToken cancellationToken = default)
    {
        var repository = await session.LoadAsync<Repository>(message.RepositoryId, cancellationToken);
        if (repository is null || repository.Connection == RepositoryConnection.Disconnected || repository.Archived) return;
        var outcome = await onboarding.RebuildAsync(repository, cancellationToken);
        logger.LogInformation("Onboarding rebuild of {Repository}: {Outcome} {Note}", repository.FullName, outcome.Outcome, outcome.Note);
    }
}
