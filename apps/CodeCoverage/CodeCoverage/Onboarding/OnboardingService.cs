using System.Globalization;
using CodeCoverage.Dependencies.Lifecycle;
using CodeCoverage.Entities;
using CodeCoverage.Forge;
using CodeCoverage.MergeQueue;
using CodeCoverage.PackageVersioning;
using CodeCoverage.Versioning.Versions;
using MintPlayer.SourceGenerators.Attributes;
using Raven.Client.Documents.Session;

namespace CodeCoverage.Onboarding;

/// <summary>
/// The onboarding chooser's one pull request (#571 M10c, coverage_backport_PRD D3): planned on the default branch's head
/// (<see cref="OnboardingPlanner"/>), pushed to <see cref="OnboardingPlanner.Branch"/> and rebuilt on every new head while it
/// is open, so it never has to merge the default branch. Its seed tags are pushed by <see cref="IVersioningOnboardingService.PushSeedsAsync"/>
/// once it merges; the state is the repository's <see cref="VersioningOnboardingState"/>.
/// </summary>
public interface IOnboardingService
{
    /// <summary>What <see cref="OnboardAsync"/> would do now; writes nothing.</summary>
    Task<OnboardingPreview> PreviewAsync(Repository repository, IReadOnlySet<EOnboardingFeature> features, CancellationToken cancellationToken = default);

    /// <summary>Plans <paramref name="features"/> on the default branch's head, pushes the commit and opens (or updates) the pull request.</summary>
    Task<VersioningOnboardingOutcome> OnboardAsync(Repository repository, IReadOnlySet<EOnboardingFeature> features, CancellationToken cancellationToken = default);

    /// <summary>The default branch moved: rebuilds the open onboarding pull request on its new head, with the features it was opened for.</summary>
    Task<VersioningOnboardingOutcome> RebuildAsync(Repository repository, CancellationToken cancellationToken = default);
}

/// <param name="Plan">Null when nothing could be planned; <paramref name="Refusal"/> says why.</param>
public sealed record OnboardingPreview(OnboardingPlan? Plan, string? Refusal);

[Register(typeof(IOnboardingService), ServiceLifetime.Scoped)]
public partial class OnboardingService : IOnboardingService
{
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly IForgeIntegrationResolver integrations;
    [Inject] private readonly IEnumerable<IForgeMergeQueue> queues;
    [Inject] private readonly IEnumerable<IForgeDependencyUpdates> updates;
    [Inject] private readonly IGitWriterSidecar writer;
    [Inject] private readonly IVersioningOnboardingService versioning;
    [Inject] private readonly IHttpClientFactory httpClients;
    [Inject] private readonly TimeProvider time;
    [Inject] private readonly ILogger<OnboardingService> logger;

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    public async Task<OnboardingPreview> PreviewAsync(Repository repository, IReadOnlySet<EOnboardingFeature> features, CancellationToken cancellationToken = default)
    {
        var planned = await PlanAsync(repository, features, cancellationToken);
        return new OnboardingPreview(planned.Plan, planned.Refusal);
    }

    public async Task<VersioningOnboardingOutcome> RebuildAsync(Repository repository, CancellationToken cancellationToken = default)
    {
        var state = await session.LoadAsync<VersioningOnboardingState>(VersioningOnboardingState.ForRepository(repository.Id!), cancellationToken);
        if (state is not { Features.Count: > 0, PullRequestNumber: { } number, MergeHandledAt: null }
            || updates.FirstOrDefault(u => u.Provider == repository.Provider) is not { } pulls
            || queues.FirstOrDefault(q => q.Provider == repository.Provider) is not { } git
            || repository.DefaultBranch is not { } defaultBranch)
            return new(VersioningOnboardingOutcome.Nothing, "No onboarding pull request is open.");
        if (await pulls.GetPullRequestAsync(repository, number, cancellationToken) is not { IsOpen: true })
            return new(VersioningOnboardingOutcome.Nothing, $"The onboarding pull request #{number} is no longer open.");
        if (string.Equals(await git.GetBranchHeadAsync(repository, defaultBranch, cancellationToken), state.BaseSha, StringComparison.OrdinalIgnoreCase))
            return new(VersioningOnboardingOutcome.Nothing, $"The onboarding pull request #{number} is on {defaultBranch}'s head already.");
        return await OnboardAsync(repository, state.Features.ToHashSet(), cancellationToken);
    }

    public async Task<VersioningOnboardingOutcome> OnboardAsync(Repository repository, IReadOnlySet<EOnboardingFeature> features, CancellationToken cancellationToken = default)
    {
        // A merged onboarding whose seeds are still pending: push them before a new plan replaces them.
        if (!await versioning.PushSeedsAsync(repository, cancellationToken))
            return new(VersioningOnboardingOutcome.Refused, "The previous onboarding's seed tags could not be pushed yet; try again later.");

        var planned = await PlanAsync(repository, features, cancellationToken);
        if (planned is not { Plan: { } plan, Tree: { } tree, Git: { } git, Pulls: { } pulls })
            return new(VersioningOnboardingOutcome.Refused, planned.Refusal!);
        var defaultBranch = repository.DefaultBranch!;

        var id = VersioningOnboardingState.ForRepository(repository.Id!);
        var state = await session.LoadAsync<VersioningOnboardingState>(id, cancellationToken);
        if (state is null)
        {
            state = new VersioningOnboardingState { Id = id };
            await session.StoreAsync(state, id, cancellationToken);
        }

        if (plan.Files.Count == 0)
            return await NoteAsync(state, new(VersioningOnboardingOutcome.Nothing,
                "Nothing to change in the repository: " + (plan.Notes.Count == 0 ? "it is set up already." : string.Join(" ", plan.Notes))), cancellationToken);

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
            pushed = await writer.OnboardingAsync(new GitWriterDependencyRequest(
                repository.Provider.ToCanonicalString(), repository.GitHubId.ToString(CultureInfo.InvariantCulture), access.CloneUrl,
                new CommitGraph.SidecarCredential(access.Username, access.Password), defaultBranch, tree.Commit)
            {
                Author = identity, Committer = identity, Message = OnboardingPlanner.Title, Files = plan.Files,
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
                throw new GitWriterUnavailableException($"The git writer answered the onboarding with {pushed.Outcome}: {pushed.Detail}");
            return await NoteAsync(state, new(VersioningOnboardingOutcome.Refused, $"The onboarding branch was not pushed ({pushed.Outcome}): {pushed.Detail}"), cancellationToken);
        }
        state.BranchSha = pushed.Sha;

        // GitHub's 422 "already exists" (a lost answer, or a race) reads as "found".
        var pull = await pulls.FindPullRequestAsync(repository, OnboardingPlanner.Branch, defaultBranch, cancellationToken);
        if (pull is null)
        {
            try
            {
                pull = await pulls.CreatePullRequestAsync(repository, OnboardingPlanner.Branch, defaultBranch, OnboardingPlanner.Title, plan.Body, draft: false, cancellationToken);
            }
            catch (ForgeRequestException ex) when (ex.StatusCode == 422)
            {
                pull = await pulls.FindPullRequestAsync(repository, OnboardingPlanner.Branch, defaultBranch, cancellationToken) ?? throw ex;
            }
        }
        else
        {
            await pulls.UpdatePullRequestAsync(repository, pull.Number, OnboardingPlanner.Title, plan.Body, cancellationToken);
        }

        state.PullRequestNumber = pull.Number;
        state.Features = [.. plan.Features];
        state.OnMerge = [.. plan.OnMerge];
        state.Renames = [.. plan.Renames.Renames.Select(r => new OnboardingRename { From = r.From, To = r.To })];
        state.Seeds = [.. plan.Seeds.Select(s => new VersioningOnboardingSeed { PackageId = s.PackageId, Version = s.Version })];
        state.SeedCommit = null;
        state.MergeHandledAt = null;
        return await NoteAsync(state, new(VersioningOnboardingOutcome.Opened, $"Opened the onboarding pull request #{pull.Number}.", pull.Number), cancellationToken);
    }

    private sealed record Planned(OnboardingPlan? Plan, ForgeRepositoryTree? Tree, IForgeMergeQueue? Git, IForgeDependencyUpdates? Pulls, string? Refusal)
    {
        public static Planned Refused(string why) => new(null, null, null, null, why);
    }

    private async Task<Planned> PlanAsync(Repository repository, IReadOnlySet<EOnboardingFeature> features, CancellationToken cancellationToken)
    {
        if (features.Count == 0)
            return Planned.Refused("Pick at least one feature.");
        if (repository.DefaultBranch is not { } defaultBranch || repository.Connection == RepositoryConnection.Disconnected || repository.Archived)
            return Planned.Refused("The repository is disconnected, archived or has no default branch.");
        if (!writer.IsConfigured
            || queues.FirstOrDefault(q => q.Provider == repository.Provider) is not { } git
            || updates.FirstOrDefault(u => u.Provider == repository.Provider) is not { } pulls)
            return Planned.Refused("The onboarding pull request is not available for this forge on this server.");
        if (await ForgeRepositoryTree.OpenAsync(integrations.For(repository), repository, defaultBranch, cancellationToken) is not { } tree)
            return Planned.Refused($"{defaultBranch} could not be read from the forge.");

        var wantsVersioning = features.Contains(EOnboardingFeature.Versioning) || features.Contains(EOnboardingFeature.ReleaseLines);
        var plan = await OnboardingPlanner.PlanAsync(tree, new OnboardingInputs
        {
            Features = features,
            DefaultBranch = defaultBranch,
            Branches = features.Contains(EOnboardingFeature.ReleaseLines) ? await git.ListBranchesAsync(repository, "", cancellationToken) : [],
            DependencyActionSha = features.Contains(EOnboardingFeature.DependencyUpdates)
                ? await TagCommitAsync(pulls, repository, DependencyOnboarding.ActionRepository, DependencyOnboarding.ActionTag, cancellationToken) : null,
            VersioningActionSha = wantsVersioning
                ? await TagCommitAsync(pulls, repository, VersioningOnboarding.ActionRepository, VersioningOnboarding.ActionTag, cancellationToken) : null,
            VersioningPackageVersion = wantsVersioning ? await LatestPackageVersionAsync(cancellationToken) : null,
            Owner = repository.OwnerLogin,
            IsTagged = (package, ct) => pulls.HasTagWithPrefixAsync(repository, VersionTag.Prefix(package), ct),
        }, cancellationToken);
        return new Planned(plan, tree, git, pulls, null);
    }

    /// <summary>The commit an action's tag resolves to; null keeps the tag in the workflow.</summary>
    private async Task<string?> TagCommitAsync(IForgeDependencyUpdates pulls, Repository repository, string ownerRepo, string tag, CancellationToken cancellationToken)
    {
        try
        {
            return await pulls.ResolveTagCommitAsync(repository, ownerRepo, tag, cancellationToken);
        }
        catch (Exception ex) when (ex is ForgeRequestException or ForgeAccessDeniedException or HttpRequestException && ForgeRateLimitedException.Find(ex) is null)
        {
            logger.LogWarning(ex, "Onboarding: could not resolve {Repository}@{Tag}; the workflow keeps the tag", ownerRepo, tag);
            return null;
        }
    }

    /// <summary>The latest stable <see cref="VersioningOnboarding.PackageId"/> on nuget.org; null (the constant) when it cannot be read.</summary>
    private async Task<string?> LatestPackageVersionAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            var json = await httpClients.CreateClient().GetStringAsync(VersioningOnboarding.PackageIndexUrl, timeout.Token);
            return VersioningOnboarding.LatestStable(json);
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Onboarding: could not read {Url}; referencing {Version}", VersioningOnboarding.PackageIndexUrl, VersioningOnboarding.PackageVersion);
            return null;
        }
    }

    private async Task<VersioningOnboardingOutcome> NoteAsync(VersioningOnboardingState state, VersioningOnboardingOutcome outcome, CancellationToken cancellationToken)
    {
        state.Note = outcome.Note;
        state.UpdatedAtUtc = Now;
        await session.SaveChangesAsync(cancellationToken);
        return outcome;
    }
}
