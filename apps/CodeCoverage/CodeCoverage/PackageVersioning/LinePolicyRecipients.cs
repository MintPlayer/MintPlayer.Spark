using CodeCoverage.Entities;
using CodeCoverage.Feedback;
using CodeCoverage.Forge;
using Microsoft.Extensions.Logging;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Messaging.Abstractions;

namespace CodeCoverage.PackageVersioning;

/// <summary>A pull request into a branch that may be a release line, to judge with the "Line policy" check (segment E).</summary>
[MessageQueue(CoverageQueues.Versioning)]
public sealed record LinePolicyMessage
{
    public required string RepositoryId { get; init; }
    public required int Number { get; init; }
    public required string HeadSha { get; init; }
    public required string HeadRef { get; init; }
    public required string BaseRef { get; init; }
}

/// <summary>
/// Forwards every opened, pushed, reopened or retargeted pull request that is not into the default branch (segment E).
/// Only a point-load here, on the shared forge-event lane. Bots are not skipped: the App's own backports need the check
/// on every head the queue pushes, or the line's required check never reports.
/// </summary>
public partial class LinePolicyEventsRecipient : IRecipient<ForgeWebhookMessage<PullRequestUpdated>>
{
    [Inject] private readonly Raven.Client.Documents.Session.IAsyncDocumentSession session;
    [Inject] private readonly IMessageBus messageBus;

    public async Task HandleAsync(ForgeWebhookMessage<PullRequestUpdated> message, CancellationToken cancellationToken = default)
    {
        var evt = message.Event;
        var repository = await session.LoadAsync<Repository>(evt.RepositoryId, cancellationToken);
        if (repository is null || repository.Archived || repository.DefaultBranch is null || repository.DefaultBranch == evt.BaseRef) return;

        await messageBus.BroadcastAsync(new LinePolicyMessage
        {
            RepositoryId = evt.RepositoryId, Number = evt.Number, HeadSha = evt.HeadSha, HeadRef = evt.HeadRef, BaseRef = evt.BaseRef,
        }, cancellationToken);
    }
}

/// <summary>
/// Publishes the "Line policy" check on a pull request into a release line (segment E, <see cref="LinePolicy"/>). A base
/// the line pattern does not match, or a repository without lines, gets no check.
/// </summary>
/// <remarks>
/// The App's own backport is the one with a <see cref="BackportPullRequest"/> record for this number onto this base: the
/// record is written only for pull requests the App opened, so a person's pull request, from a fork or not, never has it.
/// </remarks>
public partial class LinePolicyRecipient : IRecipient<LinePolicyMessage>
{
    [Inject] private readonly Raven.Client.Documents.Session.IAsyncDocumentSession session;
    [Inject] private readonly IForgeIntegrationResolver integrations;
    [Inject] private readonly IEnumerable<IForgeVersioning> forges;
    [Inject] private readonly IEnumerable<IForgeMergeQueue> queues;
    [Inject] private readonly ILogger<LinePolicyRecipient> logger;

    public async Task HandleAsync(LinePolicyMessage message, CancellationToken cancellationToken = default)
    {
        var repository = await session.LoadAsync<Repository>(message.RepositoryId, cancellationToken);
        if (repository is null || repository.Connection == RepositoryConnection.Disconnected || repository.Archived) return;
        if (repository.DefaultBranch is not { } defaultBranch || repository.Id is not { } repositoryId) return;
        if (forges.FirstOrDefault(f => f.Provider == repository.Provider) is not { } forge
            || queues.FirstOrDefault(q => q.Provider == repository.Provider) is not { } git)
            return;

        try
        {
            var yml = await integrations.For(repository).GetFileContentAsync(repository, defaultBranch, CoverageYml.FileName, cancellationToken);
            if (VersioningYml.Parse(yml).Config is not { EffectiveLines: { } pattern }) return;
            if (!pattern.TryMatch(message.BaseRef, out var line) || line is null) return;

            var branches = await git.ListBranchesAsync(repository, SupportedLines.BranchPrefix(pattern), cancellationToken);
            var current = pattern.CurrentLines([.. branches, message.BaseRef]).Contains(line);
            var record = await session.LoadAsync<BackportPullRequest>(BackportPullRequest.DocumentId(repositoryId, message.Number), cancellationToken);
            var appsBackport = record is not null && record.Branch == message.BaseRef
                && message.HeadRef.StartsWith(BackportRecipient.HeadPrefix, StringComparison.Ordinal);

            await forge.PublishCheckAsync(repository, message.HeadSha, LinePolicy.Check(line, message.BaseRef, current, appsBackport, defaultBranch), null, cancellationToken);
        }
        catch (ForgeAccessDeniedException ex)
        {
            logger.LogWarning(ex, "Line policy: missing permission {Permission} on {Repository}", ex.RequiredPermission, repository.FullName);
        }
        catch (ForgeRequestException ex) when (ForgeRateLimitedException.Find(ex) is null)
        {
            logger.LogWarning(ex, "Line policy of #{Number} in {Repository} failed", message.Number, repository.FullName);
        }
    }
}
