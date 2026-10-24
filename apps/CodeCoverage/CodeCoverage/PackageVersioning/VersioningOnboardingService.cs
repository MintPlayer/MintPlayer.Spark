using System.Globalization;
using CodeCoverage.Entities;
using CodeCoverage.Forge;
using CodeCoverage.MergeQueue;
using CodeCoverage.Versioning.Versions;
using MintPlayer.SourceGenerators.Attributes;
using Raven.Client.Documents.Session;

namespace CodeCoverage.PackageVersioning;

/// <summary>
/// The versioning onboarding (#153 M13, PRD D17): one pull request that moves a repository onto server-side versioning,
/// and the seed tags that carry each package's current version over once it merges.
/// </summary>
public interface IVersioningOnboardingService
{
    /// <summary>Plans the onboarding on the default branch's head, pushes it and opens (or updates) its pull request.</summary>
    Task<VersioningOnboardingOutcome> OnboardAsync(Repository repository, CancellationToken cancellationToken = default);

    /// <summary>
    /// Pushes the pending seed tags at the onboarding pull request's merge commit, when it has merged. Returns false when
    /// seeds are pending on a merged pull request and could not be pushed: a versioning run must not go ahead without them,
    /// or it would release the untagged packages from scratch. The caller holds <see cref="VersioningRunLocks"/>.
    /// </summary>
    Task<bool> PushSeedsAsync(Repository repository, CancellationToken cancellationToken = default);
}

/// <param name="Outcome"><see cref="Opened"/>, <see cref="Seeded"/>, <see cref="Nothing"/> or <see cref="Refused"/>.</param>
public sealed record VersioningOnboardingOutcome(string Outcome, string Note, int? PullRequest = null)
{
    public const string Opened = "opened";
    public const string Seeded = "seeded";
    public const string Nothing = "nothing";
    public const string Refused = "refused";
}

[Register(typeof(IVersioningOnboardingService), ServiceLifetime.Scoped)]
public partial class VersioningOnboardingService : IVersioningOnboardingService
{
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly IForgeIntegrationResolver integrations;
    [Inject] private readonly IEnumerable<IForgeVersioning> forges;
    [Inject] private readonly IEnumerable<IForgeMergeQueue> queues;
    [Inject] private readonly IEnumerable<IForgeDependencyUpdates> updates;
    [Inject] private readonly IGitWriterSidecar writer;
    [Inject] private readonly TimeProvider time;
    [Inject] private readonly ILogger<VersioningOnboardingService> logger;

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    public async Task<VersioningOnboardingOutcome> OnboardAsync(Repository repository, CancellationToken cancellationToken = default)
    {
        if (repository.DefaultBranch is not { } defaultBranch || repository.Connection == RepositoryConnection.Disconnected || repository.Archived)
            return new(VersioningOnboardingOutcome.Refused, "The repository is disconnected, archived or has no default branch.");
        if (!writer.IsConfigured
            || queues.FirstOrDefault(q => q.Provider == repository.Provider) is not { } git
            || updates.FirstOrDefault(u => u.Provider == repository.Provider) is not { } pulls
            || !forges.Any(f => f.Provider == repository.Provider))
            return new(VersioningOnboardingOutcome.Refused, "Versioning is not available for this forge on this server.");

        // A merged onboarding whose seeds are still pending: push them before a new plan replaces them.
        if (!await PushSeedsAsync(repository, cancellationToken))
            return new(VersioningOnboardingOutcome.Refused, "The previous onboarding's seed tags could not be pushed yet; try again later.");

        var id = VersioningOnboardingState.ForRepository(repository.Id!);
        var state = await session.LoadAsync<VersioningOnboardingState>(id, cancellationToken);
        if (state is null)
        {
            state = new VersioningOnboardingState { Id = id };
            await session.StoreAsync(state, id, cancellationToken);
        }

        if (await ForgeRepositoryTree.OpenAsync(integrations.For(repository), repository, defaultBranch, cancellationToken) is not { } tree)
            return await NoteAsync(state, new(VersioningOnboardingOutcome.Refused, $"{defaultBranch} could not be read from the forge."), cancellationToken);

        string? actionSha = null;
        try
        {
            actionSha = await pulls.ResolveTagCommitAsync(repository, VersioningOnboarding.ActionRepository, VersioningOnboarding.ActionTag, cancellationToken);
        }
        catch (Exception ex) when (ex is ForgeRequestException or ForgeAccessDeniedException or HttpRequestException && ForgeRateLimitedException.Find(ex) is null)
        {
            logger.LogWarning(ex, "Versioning onboarding: could not resolve {Tag}; the workflow step keeps the tag", VersioningOnboarding.ActionTag);
        }
        var plan = await VersioningOnboarding.PlanAsync(tree, (package, ct) => pulls.HasTagWithPrefixAsync(repository, VersionTag.Prefix(package), ct), actionSha, cancellationToken);

        if (plan.Files.Count == 0)
        {
            // Already set up by hand: the seeds (if any) go straight onto the head.
            if (plan.Seeds.Count == 0)
                return await NoteAsync(state, new(VersioningOnboardingOutcome.Nothing, "The repository is already set up for versioning; nothing to change."), cancellationToken);
            state.Seeds = [.. plan.Seeds.Select(s => new VersioningOnboardingSeed { PackageId = s.PackageId, Version = s.Version })];
            state.PullRequestNumber = null;
            var tagged = await TagAsync(repository, git, defaultBranch, tree.Commit, state.Seeds, cancellationToken);
            if (tagged is not null)
                return await NoteAsync(state, new(VersioningOnboardingOutcome.Refused, tagged), cancellationToken);
            state.SeedCommit = tree.Commit;
            return await NoteAsync(state, new(VersioningOnboardingOutcome.Seeded, $"Pushed {state.Seeds.Count} seed tag(s) at {Short(tree.Commit)}."), cancellationToken);
        }

        // The same base and date rebuild the same commit, so a retry's force-push is a no-op.
        if (state.BaseSha != tree.Commit)
        {
            state.BaseSha = tree.Commit;
            state.StartedAtUtc = Now;
        }
        var identity = new GitWriterIdentity(VersioningController.Tagger.Name, VersioningController.Tagger.Email,
            new DateTimeOffset(DateTime.SpecifyKind(state.StartedAtUtc ?? Now, DateTimeKind.Utc)));
        var push = await git.GetWriterAccessAsync(repository, null, pushesWorkflowChanges: true, cancellationToken);
        if (push.Access is not { } access)
            return await NoteAsync(state, new(VersioningOnboardingOutcome.Refused,
                $"The App could not push the onboarding branch ({push.RefusalReason}). It needs **Workflows: write**: the installation's owner accepts it on GitHub."), cancellationToken);
        GitWriterDependencyResult pushed;
        try
        {
            pushed = await writer.VersioningOnboardingAsync(new GitWriterDependencyRequest(
                repository.Provider.ToCanonicalString(), repository.GitHubId.ToString(CultureInfo.InvariantCulture), access.CloneUrl,
                new CommitGraph.SidecarCredential(access.Username, access.Password), defaultBranch, tree.Commit)
            {
                Author = identity, Committer = identity, Message = VersioningOnboarding.Title, Files = plan.Files,
            }, cancellationToken);
        }
        catch (GitWriterRefusedException ex)
        {
            return await NoteAsync(state, new(VersioningOnboardingOutcome.Refused, ex.Message), cancellationToken);
        }
        finally
        {
            await git.RevokeWriterAccessAsync(repository, access, CancellationToken.None);
        }
        if (pushed.Outcome != GitWriterOutcomes.Pushed)
        {
            if (pushed.Outcome is GitWriterOutcomes.Busy or GitWriterOutcomes.Failed)
                throw new GitWriterUnavailableException($"The git writer answered the versioning onboarding with {pushed.Outcome}: {pushed.Detail}");
            return await NoteAsync(state, new(VersioningOnboardingOutcome.Refused, $"The onboarding branch was not pushed ({pushed.Outcome}): {pushed.Detail}"), cancellationToken);
        }
        state.BranchSha = pushed.Sha;

        // GitHub's 422 "already exists" (a lost answer, or a race) reads as "found".
        var pull = await pulls.FindPullRequestAsync(repository, VersioningOnboarding.Branch, defaultBranch, cancellationToken);
        if (pull is null)
        {
            try
            {
                pull = await pulls.CreatePullRequestAsync(repository, VersioningOnboarding.Branch, defaultBranch, VersioningOnboarding.Title, plan.Body, draft: false, cancellationToken);
            }
            catch (ForgeRequestException ex) when (ex.StatusCode == 422)
            {
                pull = await pulls.FindPullRequestAsync(repository, VersioningOnboarding.Branch, defaultBranch, cancellationToken) ?? throw ex;
            }
        }
        else
        {
            await pulls.UpdatePullRequestAsync(repository, pull.Number, VersioningOnboarding.Title, plan.Body, cancellationToken);
        }

        state.PullRequestNumber = pull.Number;
        state.Seeds = [.. plan.Seeds.Select(s => new VersioningOnboardingSeed { PackageId = s.PackageId, Version = s.Version })];
        state.SeedCommit = null;
        return await NoteAsync(state, new(VersioningOnboardingOutcome.Opened, $"Opened the onboarding pull request #{pull.Number}.", pull.Number), cancellationToken);
    }

    public async Task<bool> PushSeedsAsync(Repository repository, CancellationToken cancellationToken = default)
    {
        var state = await session.LoadAsync<VersioningOnboardingState>(VersioningOnboardingState.ForRepository(repository.Id!), cancellationToken);
        if (state is not { SeedsPending: true } || repository.DefaultBranch is not { } defaultBranch) return true;
        if (forges.FirstOrDefault(f => f.Provider == repository.Provider) is not { } forge
            || queues.FirstOrDefault(q => q.Provider == repository.Provider) is not { } git)
            return true;

        // The merge commit is read from the forge, never from a webhook: the run that may race the webhook needs it too.
        var pull = await forge.GetPullRequestTextAsync(repository, state.PullRequestNumber!.Value, cancellationToken);
        if (pull is not { Merged: true, MergeCommitSha: { } merge }) return true;

        if (await TagAsync(repository, git, defaultBranch, merge, state.Seeds, cancellationToken) is { } refusal)
        {
            await NoteAsync(state, new(VersioningOnboardingOutcome.Refused, refusal), cancellationToken);
            return false;
        }
        state.SeedCommit = merge;
        await NoteAsync(state, new(VersioningOnboardingOutcome.Seeded, $"Pushed {state.Seeds.Count} seed tag(s) at the merge commit {Short(merge)}."), cancellationToken);
        return true;
    }

    /// <summary>Pushes the seeds at <paramref name="commit"/>; null when every tag is there, else why not.</summary>
    private async Task<string?> TagAsync(Repository repository, IForgeMergeQueue git, string defaultBranch, string commit, IReadOnlyList<VersioningOnboardingSeed> seeds,
        CancellationToken cancellationToken)
    {
        var push = await git.GetWriterAccessAsync(repository, null, false, cancellationToken);
        if (push.Access is not { } access)
            return $"The App could not push the seed tags: {push.RefusalReason}";
        try
        {
            var result = await writer.TagsAsync(new GitWriterTagsRequest(
                repository.Provider.ToCanonicalString(), repository.GitHubId.ToString(CultureInfo.InvariantCulture), access.CloneUrl,
                new CommitGraph.SidecarCredential(access.Username, access.Password), [defaultBranch], VersioningController.Tagger,
                [.. seeds.Select(s => new GitWriterTag(VersionTag.Prefix(s.PackageId) + s.Version, commit))]), cancellationToken);
            if (result.Outcome is GitWriterOutcomes.Busy or GitWriterOutcomes.Failed)
                throw new GitWriterUnavailableException($"The git writer answered the seed tags with {result.Outcome}: {result.Detail}");
            if (result.Outcome != GitWriterTagsResult.Tagged)
                return $"The seed tags were refused ({result.Reason}): {result.Detail}";
            var failed = (result.Tags ?? []).Where(t => t.Outcome is not (GitWriterTagResult.Created or GitWriterTagResult.AlreadyPresent)).ToList();
            return failed.Count == 0 ? null : "Seed tags not pushed: " + string.Join("; ", failed.Select(t => $"{t.Name} ({t.Outcome}{(t.Detail is { } d ? $": {d}" : null)})"));
        }
        catch (GitWriterRefusedException ex)
        {
            return ex.Message;
        }
        finally
        {
            await git.RevokeWriterAccessAsync(repository, access, CancellationToken.None);
        }
    }

    private async Task<VersioningOnboardingOutcome> NoteAsync(VersioningOnboardingState state, VersioningOnboardingOutcome outcome, CancellationToken cancellationToken)
    {
        state.Note = outcome.Note;
        state.UpdatedAtUtc = Now;
        await session.SaveChangesAsync(cancellationToken);
        return outcome;
    }

    private static string Short(string sha) => sha.Length > 7 ? sha[..7] : sha;
}
