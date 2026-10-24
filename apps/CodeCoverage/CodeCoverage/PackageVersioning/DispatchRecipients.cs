using System.Globalization;
using CodeCoverage.Entities;
using CodeCoverage.Feedback;
using CodeCoverage.Forge;
using CodeCoverage.MergeQueue;
using CodeCoverage.Versioning.Releases;
using Microsoft.Extensions.Logging;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Messaging.Abstractions;
using Raven.Client.Documents.Session;

namespace CodeCoverage.PackageVersioning;

/// <summary>A push to a versioned line to plan (#153 M11), queued so the writer never holds the webhook.</summary>
[MessageQueue(CoverageQueues.Versioning)]
public sealed record VersioningDispatchMessage
{
    public required string RepositoryId { get; init; }
    public required string Branch { get; init; }
    public required string Sha { get; init; }
}

/// <summary>
/// Forwards a push to the default branch or to a branch the cached release patterns match. Everything else is dropped
/// here, at the cost of one load, so ordinary feature-branch pushes never reach the forge.
/// </summary>
public partial class VersioningDispatchEventsRecipient : IRecipient<ForgeWebhookMessage<BranchCommitPushed>>
{
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly IMessageBus messageBus;

    public async Task HandleAsync(ForgeWebhookMessage<BranchCommitPushed> message, CancellationToken cancellationToken = default)
    {
        var evt = message.Event;
        if (await session.LoadAsync<Repository>(evt.RepositoryId, cancellationToken) is not { DefaultBranch: { } defaultBranch } repository
            || repository.Connection == RepositoryConnection.Disconnected || repository.Archived)
            return;
        if (evt.Branch != defaultBranch)
        {
            var state = await session.LoadAsync<VersioningDispatchState>(VersioningDispatchState.ForRepository(evt.RepositoryId), cancellationToken);
            if (state?.Dispatch is null || !ReleaseBranches.IsMatch(state.ReleaseBranches, evt.Branch, out _)) return;
        }
        // Keyed on the commit: a redelivered push, or the same commit pushed to a second line, dispatches once.
        await messageBus.BroadcastOnceAsync(new VersioningDispatchMessage { RepositoryId = evt.RepositoryId, Branch = evt.Branch, Sha = evt.Sha },
            $"versioning-dispatch:{evt.RepositoryId}:{evt.Branch}:{evt.Sha}", cancellationToken);
    }
}

/// <summary>
/// Trigger B (#153 M11, PRD D3): after a push to a versioned line, fire <c>versioning.dispatch</c> when, and only when,
/// a run on the pushed commit would do something. The writer plans the commit without a build: the static graph, the
/// tags and the input diff. An unknown API hash counts as changed, so the plan can over-report a release (the run then
/// tags nothing for it) but never misses one; an empty plan dispatches nothing.
/// </summary>
/// <remarks>
/// A refused package dispatches too: the run fails with the refusal, which is where the owner sees it. A plan the writer
/// could not make (busy, unreachable, an unplannable commit) dispatches nothing and is logged; the next push plans again.
/// </remarks>
public partial class VersioningDispatchRecipient : IRecipient<VersioningDispatchMessage>
{
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly IForgeIntegrationResolver integrations;
    [Inject] private readonly IEnumerable<IForgeVersioning> forges;
    [Inject] private readonly IEnumerable<IForgeMergeQueue> queues;
    [Inject] private readonly IGitWriterSidecar writer;
    [Inject] private readonly TimeProvider clock;
    [Inject] private readonly ILogger<VersioningDispatchRecipient> logger;

    public async Task HandleAsync(VersioningDispatchMessage message, CancellationToken cancellationToken = default)
    {
        var repository = await session.LoadAsync<Repository>(message.RepositoryId, cancellationToken);
        if (repository is null || repository.Connection == RepositoryConnection.Disconnected || repository.Archived) return;
        if (repository.DefaultBranch is not { } defaultBranch) return;
        if (forges.FirstOrDefault(f => f.Provider == repository.Provider) is not { } forge) return;
        if (queues.FirstOrDefault(q => q.Provider == repository.Provider) is not { } git) return;

        try
        {
            var config = VersioningYml.Parse(await integrations.For(repository).GetFileContentAsync(repository, defaultBranch, CoverageYml.FileName, cancellationToken)).Config;
            await RememberAsync(repository.Id!, config, cancellationToken);
            if (config?.Dispatch is not { } workflow) return;

            int? branchMajor = null;
            if (message.Branch != defaultBranch && !ReleaseBranches.IsMatch(config.EffectiveReleaseBranches, message.Branch, out branchMajor)) return;

            var plan = await PlanAsync(repository, git, message, defaultBranch, branchMajor, config.ToSettings(), cancellationToken);
            if (plan is null) return;
            if ((plan.Releases?.Count ?? 0) == 0 && (plan.Refused?.Count ?? 0) == 0)
            {
                logger.LogInformation("Versioning: {Sha} on {Branch} of {Repository} releases nothing; no dispatch", message.Sha, message.Branch, repository.FullName);
                return;
            }
            await forge.DispatchWorkflowAsync(repository, workflow, message.Branch, cancellationToken);
        }
        catch (ForgeAccessDeniedException ex)
        {
            logger.LogWarning(ex, "Versioning dispatch: missing permission {Permission} on {Repository}", ex.RequiredPermission, repository.FullName);
        }
        catch (ForgeRequestException ex) when (ForgeRateLimitedException.Find(ex) is null)
        {
            logger.LogWarning(ex, "Versioning dispatch for {Branch} of {Repository} failed", message.Branch, repository.FullName);
        }
    }

    private async Task RememberAsync(string repositoryId, VersioningConfig? config, CancellationToken cancellationToken)
    {
        var id = VersioningDispatchState.ForRepository(repositoryId);
        var state = await session.LoadAsync<VersioningDispatchState>(id, cancellationToken);
        if (config?.Dispatch is null)
        {
            if (state is null) return;
            session.Delete(state);
        }
        else
        {
            if (state is null) await session.StoreAsync(state = new VersioningDispatchState(), id, cancellationToken);
            state.Dispatch = config.Dispatch;
            state.ReleaseBranches = [.. config.EffectiveReleaseBranches];
            state.UpdatedAtUtc = clock.GetUtcNow().UtcDateTime;
        }
        await session.SaveChangesAsync(cancellationToken);
    }

    private async Task<GitWriterVersioningPlanResult?> PlanAsync(Repository repository, IForgeMergeQueue git, VersioningDispatchMessage message, string defaultBranch,
        int? branchMajor, VersioningSettings settings, CancellationToken cancellationToken)
    {
        var push = await git.GetWriterAccessAsync(repository, null, false, cancellationToken);
        if (push.Access is not { } access)
        {
            logger.LogWarning("Versioning dispatch: no writer access for {Repository}: {Reason}", repository.FullName, push.RefusalReason);
            return null;
        }
        try
        {
            var result = await writer.VersioningPlanAsync(new GitWriterVersioningPlanRequest(
                repository.Provider.ToCanonicalString(), repository.GitHubId.ToString(CultureInfo.InvariantCulture), access.CloneUrl,
                new CommitGraph.SidecarCredential(access.Username, access.Password), message.Branch, defaultBranch, message.Sha, branchMajor, settings),
                cancellationToken);
            if (result.Outcome == GitWriterVersioningPlanResult.Planned) return result;
            logger.LogWarning("Versioning dispatch: no plan for {Sha} on {Branch} of {Repository}: {Outcome} {Reason} {Detail}",
                message.Sha, message.Branch, repository.FullName, result.Outcome, result.Reason, result.Detail);
            return null;
        }
        catch (GitWriterRefusedException ex)
        {
            logger.LogWarning(ex, "Versioning dispatch: the git writer refused the plan for {Repository}", repository.FullName);
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Versioning dispatch: the git writer did not answer for {Repository}", repository.FullName);
            return null;
        }
        finally
        {
            await git.RevokeWriterAccessAsync(repository, access, CancellationToken.None);
        }
    }
}
