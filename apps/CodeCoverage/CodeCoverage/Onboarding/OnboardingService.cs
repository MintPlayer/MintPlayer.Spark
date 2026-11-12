using System.Globalization;
using CodeCoverage.Dependencies.Lifecycle;
using CodeCoverage.Entities;
using CodeCoverage.Feedback;
using CodeCoverage.Forge;
using CodeCoverage.LookupReferences;
using CodeCoverage.MergeQueue;
using CodeCoverage.PackageVersioning;
using CodeCoverage.Versioning.Graph;
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
    /// <summary>
    /// Why no onboarding pull request can be planned for <paramref name="repository"/> on this server, from what is known
    /// without asking the forge (disconnected, archived, no default branch, no git writer, a forge without the
    /// adapters), or <see langword="null"/>. The chooser asks it before the GitHub popup, so such a refusal comes before
    /// any authorization (#554, owner 2026-11-06). <see cref="PreviewAsync"/> refuses the same.
    /// </summary>
    string? RefusalBeforePlanning(Repository repository);

    /// <summary>
    /// The features the chooser's form starts with: what the repository has now (<see cref="OnboardingFeatureDetection"/>, over
    /// the default branch's <c>coverage.yml</c>), plus what an onboarding pull request that is still open carries. Reads only.
    /// </summary>
    Task<IReadOnlySet<EOnboardingFeature>> CurrentFeaturesAsync(Repository repository, CancellationToken cancellationToken = default);

    /// <summary>What <see cref="OnboardAsync"/> would do now; writes nothing.</summary>
    Task<OnboardingPreview> PreviewAsync(Repository repository, IReadOnlySet<EOnboardingFeature> features, CancellationToken cancellationToken = default);

    /// <summary>
    /// Plans <paramref name="features"/> on the default branch's head, pushes the commit and opens (or updates) the pull request.
    /// <paramref name="olderLines"/>: the offered older release lines (<see cref="OnboardingPlan.OfferedLines"/>) the owner
    /// chose to create (docs/onboarding_line_cuts_PRD.md D2). <paramref name="moveTags"/>: the collisions
    /// (<see cref="OnboardingPlan.TagCollisions"/>) the owner chose to move, at the history backfill (#638 D9).
    /// </summary>
    Task<VersioningOnboardingOutcome> OnboardAsync(Repository repository, IReadOnlySet<EOnboardingFeature> features, IReadOnlyCollection<string> olderLines,
        CancellationToken cancellationToken = default, IReadOnlyCollection<string>? moveTags = null);

    /// <summary>The default branch moved: rebuilds the open onboarding pull request on its new head, with the features it was opened for.</summary>
    Task<VersioningOnboardingOutcome> RebuildAsync(Repository repository, CancellationToken cancellationToken = default);

    /// <summary>
    /// The onboarding pull request merged (#571 M10d), the one entry point of a merge: pushes its seed tags
    /// (<see cref="IVersioningOnboardingService.PushSeedsAsync"/>, also for a #153 pull request), switches its settings on, renames the old line branches (D2),
    /// creates the release lines it planned from the merge commit (docs/onboarding_line_cuts_PRD.md) and opens a
    /// pull request into each supported line that lacks a regenerate workflow the default branch has (D9). Once only; a
    /// rename or line the forge refuses is noted, not retried.
    /// </summary>
    Task<VersioningOnboardingOutcome> MergedAsync(Repository repository, CancellationToken cancellationToken = default);
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
    [Inject] private readonly IEnumerable<IForgeVersioning> forges;
    [Inject] private readonly MintPlayer.Spark.Messaging.Abstractions.IMessageBus messageBus;
    [Inject] private readonly IHttpClientFactory httpClients;
    [Inject] private readonly TimeProvider time;
    [Inject] private readonly ILogger<OnboardingService> logger;

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    public string? RefusalBeforePlanning(Repository repository)
    {
        if (repository.DefaultBranch is not { Length: > 0 } || repository.Connection == RepositoryConnection.Disconnected || repository.Archived)
            return "The repository is disconnected, archived or has no default branch.";
        if (!writer.IsConfigured
            || !queues.Any(q => q.Provider == repository.Provider)
            || !updates.Any(u => u.Provider == repository.Provider))
            return "The onboarding pull request is not available for this forge on this server.";
        return null;
    }

    public async Task<IReadOnlySet<EOnboardingFeature>> CurrentFeaturesAsync(Repository repository, CancellationToken cancellationToken = default)
    {
        var account = repository.Account is { Length: > 0 } accountId ? await session.LoadAsync<Account>(accountId, cancellationToken) : null;
        string? yml = null;
        if (repository.DefaultBranch is { Length: > 0 } defaultBranch
            && await ForgeRepositoryTree.OpenAsync(integrations.For(repository), repository, defaultBranch, cancellationToken) is { } tree)
            yml = await tree.ReadTextAsync(CoverageYml.FileName, Limits.MaxManifestBytes, cancellationToken);
        var features = OnboardingFeatureDetection.Detect(repository, account, yml).ToHashSet();

        // An open onboarding pull request's features are chosen already, though not applied yet. A closed one's are not.
        var state = await session.LoadAsync<VersioningOnboardingState>(VersioningOnboardingState.ForRepository(repository.Id!), cancellationToken);
        if (state is { Features.Count: > 0, PullRequestNumber: { } number, MergeHandledAt: null }
            && updates.FirstOrDefault(u => u.Provider == repository.Provider) is { } pulls
            && await pulls.GetPullRequestAsync(repository, number, cancellationToken) is { IsOpen: true })
            features.UnionWith(state.Features);
        return features;
    }

    public async Task<OnboardingPreview> PreviewAsync(Repository repository, IReadOnlySet<EOnboardingFeature> features, CancellationToken cancellationToken = default)
    {
        var planned = await PlanAsync(repository, features, [], cancellationToken);
        if (planned.Plan is not { } plan) return new OnboardingPreview(null, planned.Refusal);
        // #638 D9: the App's tags the history places elsewhere, for the owner to move or leave; only a versioned repository has any.
        return new OnboardingPreview(plan with { TagCollisions = await versioning.CollisionsAsync(repository, cancellationToken) }, null);
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
        return await OnboardAsync(repository, state.Features.ToHashSet(), state.OlderLines, cancellationToken, state.MoveTags);
    }

    public async Task<VersioningOnboardingOutcome> OnboardAsync(Repository repository, IReadOnlySet<EOnboardingFeature> features, IReadOnlyCollection<string> olderLines,
        CancellationToken cancellationToken = default, IReadOnlyCollection<string>? moveTags = null)
    {
        // A merged onboarding whose seeds are still pending: push them before a new plan replaces them.
        if (!await versioning.PushSeedsAsync(repository, afterCuts: false, cancellationToken))
            return new(VersioningOnboardingOutcome.Refused, "The previous onboarding's seed tags could not be pushed yet; try again later.");

        var planned = await PlanAsync(repository, features, olderLines, cancellationToken);
        if (planned is not { Plan: { } plan, Tree: { } tree, Git: { } git, Pulls: { } pulls })
            return new(VersioningOnboardingOutcome.Refused, planned.Refusal!);
        var forge = forges.FirstOrDefault(f => f.Provider == repository.Provider);
        var defaultBranch = repository.DefaultBranch!;

        var id = VersioningOnboardingState.ForRepository(repository.Id!);
        var state = await session.LoadAsync<VersioningOnboardingState>(id, cancellationToken);
        if (state is null)
        {
            state = new VersioningOnboardingState { Id = id };
            await session.StoreAsync(state, id, cancellationToken);
        }

        if (plan.Files.Count == 0)
        {
            // No pull request to wait for: the settings, renames and line cuts apply now (M10d; line cuts D3, from the head
            // the plan read, whose workflows run on the lines already).
            var applied = await ApplyAsync(repository, git, plan.OnMerge, plan.Renames.Renames.Select(r => new OnboardingRename { From = r.From, To = r.To }), cancellationToken);
            applied.AddRange(await CutAsync(repository, forge, plan.Cuts.Select(c => c.Branch), tree.Commit, cancellationToken));
            // docs/onboarding_merge_backfill_PRD.md D3: the merge's steps in the merge's order (MergedAsync).
            // #638 D8: re-running Set up features backfills the versions the history declares and no tag carries yet, after the cuts (D7).
            if (await versioning.BackfillAsync(repository, moveTags ?? [], cancellationToken) is { } backfilled) applied.Add(backfilled.TrimEnd('.'));
            if (plan.Features.Contains(EOnboardingFeature.ReleaseLines))
                applied.AddRange(await LineWorkflowsAsync(repository, git, pulls, defaultBranch, cancellationToken));
            // #638 D15: an older line cut from a commit that targets a higher .NET gets a pull request narrowing it.
            applied.AddRange(await NarrowAsync(repository, git, pulls, plan.Cuts.Select(c => c.Branch), defaultBranch, cancellationToken));
            return await NoteAsync(state, new(applied.Count > 0 ? VersioningOnboardingOutcome.Merged : VersioningOnboardingOutcome.Nothing,
                (applied.Count > 0 ? "Applied without a pull request: " + string.Join("; ", applied) + ". " : "")
                + "Nothing to change in the repository: " + (plan.Notes.Count == 0 ? "it is set up already." : string.Join(" ", plan.Notes))), cancellationToken);
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
        state.Cuts = [.. plan.Cuts.Select(c => c.Branch)];
        state.OlderLines = [.. olderLines];
        state.MoveTags = [.. moveTags ?? []];
        state.Seeds = [.. plan.Seeds.Select(s => new VersioningOnboardingSeed { PackageId = s.PackageId, Version = s.Version })];
        state.SeedCommit = null;
        state.Backfill = true;
        state.BackfilledAt = null;
        state.MergeHandledAt = null;
        return await NoteAsync(state, new(VersioningOnboardingOutcome.Opened, $"Opened the onboarding pull request #{pull.Number}.", pull.Number), cancellationToken);
    }

    public async Task<VersioningOnboardingOutcome> MergedAsync(Repository repository, CancellationToken cancellationToken = default)
    {
        // The seeds (with the history backfill, #638) come after the line cuts (D7), so a version goes on the line of its major. A
        // #153 pull request (no features) carries seeds too, so every early return pushes them. A refusal is noted on the state
        // and retried by the next versioning run, so it does not hold back the rest.
        var state = await session.LoadAsync<VersioningOnboardingState>(VersioningOnboardingState.ForRepository(repository.Id!), cancellationToken);
        if (state is not { Features.Count: > 0, PullRequestNumber: { } number, MergeHandledAt: null }
            || forges.FirstOrDefault(f => f.Provider == repository.Provider) is not { } forge
            || queues.FirstOrDefault(q => q.Provider == repository.Provider) is not { } git
            || updates.FirstOrDefault(u => u.Provider == repository.Provider) is not { } pulls
            || repository.DefaultBranch is not { } defaultBranch)
        {
            // Handled already, or a #153 pull request: no line cuts are pending.
            await versioning.PushSeedsAsync(repository, afterCuts: true, cancellationToken);
            return new(VersioningOnboardingOutcome.Nothing, "No onboarding pull request waits for its merge.");
        }
        if (await forge.GetPullRequestTextAsync(repository, number, cancellationToken) is not { Merged: true } merged)
            return new(VersioningOnboardingOutcome.Nothing, $"The onboarding pull request #{number} has not merged.");

        var mergeCommit = merged.MergeCommitSha ?? merged.HeadSha;
        var done = await ApplyAsync(repository, git, state.OnMerge, state.Renames, cancellationToken);
        // D7: the lines after the renames, before their workflow pull requests; from the merge commit, which carries the
        // onboarding's own line triggers (D3).
        if (mergeCommit is { Length: > 0 })
            done.AddRange(await CutAsync(repository, forge, state.Cuts, mergeCommit, cancellationToken));
        // PushSeedsAsync notes what it did on this same state (the session's one instance).
        var before = state.Note;
        await versioning.PushSeedsAsync(repository, afterCuts: true, cancellationToken);
        if (state.Note is { } seeded && seeded != before) done.Add(seeded.TrimEnd('.'));
        if (state.Features.Contains(EOnboardingFeature.ReleaseLines))
            done.AddRange(await LineWorkflowsAsync(repository, git, pulls, defaultBranch, cancellationToken));
        // #638 D15: an older line cut from a commit that targets a higher .NET gets a pull request narrowing it.
        if (mergeCommit is { Length: > 0 })
            done.AddRange(await NarrowAsync(repository, git, pulls, state.Cuts, defaultBranch, cancellationToken));

        state.MergeHandledAt = mergeCommit;
        return await NoteAsync(state, new(VersioningOnboardingOutcome.Merged,
            $"The onboarding pull request #{number} merged" + (done.Count == 0 ? "." : ": " + string.Join("; ", done) + "."), number), cancellationToken);
    }

    /// <summary>Switches <paramref name="onMerge"/> on and renames the line branches (D2); what was done, or why not, per item.</summary>
    private async Task<List<string>> ApplyAsync(Repository repository, IForgeMergeQueue git, IEnumerable<EOnboardingFeature> onMerge,
        IEnumerable<OnboardingRename> renames, CancellationToken cancellationToken)
    {
        var done = new List<string>();
        var settings = onMerge.ToHashSet();
        if (settings.Contains(EOnboardingFeature.DependencyUpdates) && repository.DependencyUpdates != EDependencyUpdatesPolicy.Enabled)
        {
            repository.DependencyUpdates = EDependencyUpdatesPolicy.Enabled;
            await DependencyUpdateSwitch.SignalAsync(messageBus, repository.Id!, DependencyUpdateSwitch.On, time.GetUtcNow(), expedite: false, cancellationToken);
            done.Add("dependency updates are on");
        }
        if (settings.Contains(EOnboardingFeature.SizeLabels) && repository.PrSizeLabels != EPrSizeLabelPolicy.Enabled)
        {
            repository.PrSizeLabels = EPrSizeLabelPolicy.Enabled;
            done.Add("PR size labels are on");
        }
        foreach (var rename in renames)
        {
            var refusal = await git.RenameBranchAsync(repository, rename.From, rename.To, cancellationToken);
            done.Add(refusal is null ? $"renamed {rename.From} to {rename.To}" : $"{rename.From} was not renamed: {refusal}");
        }
        return done;
    }

    /// <summary>
    /// docs/onboarding_line_cuts_PRD.md D4: creates each release line at <paramref name="sha"/> with the App's own token, which
    /// ruleset 3's exempt bypass admits. A line that exists (the first versioning run's cut got there first, S3) counts as
    /// done; a refusal is noted, not retried, like a refused rename.
    /// </summary>
    private async Task<List<string>> CutAsync(Repository repository, IForgeVersioning? forge, IEnumerable<string> lines, string sha, CancellationToken cancellationToken)
    {
        var done = new List<string>();
        foreach (var line in lines)
        {
            if (forge is null)
            {
                done.Add($"{line} was not created: this forge cannot create branches");
                continue;
            }
            try
            {
                done.Add(await forge.CreateBranchAsync(repository, line, sha, cancellationToken) == ForgeWriteOutcome.Created
                    ? $"created {line}" : $"{line} exists already");
            }
            catch (ForgeRequestException ex)
            {
                logger.LogWarning(ex, "Onboarding: {Repository} refused creating {Line}", repository.FullName, line);
                done.Add($"{line} was not created: {ex.Message}");
            }
        }
        return done;
    }

    /// <summary>
    /// D9: a pull request into each supported line that lacks one of the regenerate workflows the default branch's
    /// <c>mergeQueue</c> names, copied from the default branch, so a backport regenerates with the line's own tools.
    /// </summary>
    private async Task<IReadOnlyList<string>> LineWorkflowsAsync(Repository repository, IForgeMergeQueue git, IForgeDependencyUpdates pulls, string defaultBranch,
        CancellationToken cancellationToken)
    {
        var integration = integrations.For(repository);
        var yml = await integration.GetFileContentAsync(repository, defaultBranch, CoverageYml.FileName, cancellationToken);
        if (VersioningYml.Parse(yml).Config is not { EffectiveLines: { } pattern }
            || MergeQueueYml.Parse(yml, defaultBranch).Section?.Workflows() is not { Count: > 0 } workflows)
            return [];

        var defaults = new List<(string Path, string Content)>();
        foreach (var workflow in workflows)
            if (await integration.GetFileContentAsync(repository, defaultBranch, WorkflowPath(workflow), cancellationToken) is { } content)
                defaults.Add((WorkflowPath(workflow), content));
        if (defaults.Count == 0) return [];

        var notes = new List<string>();
        foreach (var line in await SupportedLines.ReadAsync(session, git, repository, pattern, BackportYml.Parse(yml).Section.Lines, cancellationToken,
            SupportedLines.PromotesCurrent(yml)))
        {
            if (!line.Supported) continue;
            var missing = new List<GitWriterFile>();
            foreach (var (path, content) in defaults)
                if (await integration.GetFileContentAsync(repository, line.Branch, path, cancellationToken) is null)
                    missing.Add(new GitWriterFile(path, content));
            if (missing.Count == 0) continue;
            if (await git.GetBranchHeadAsync(repository, line.Branch, cancellationToken) is not { } head)
            {
                notes.Add($"{line.Branch} could not be read");
                continue;
            }
            notes.Add(await LinePullRequestAsync(repository, git, pulls, line.Branch, line.Line.ToString(), head, missing, narrow: false, cancellationToken));
        }
        return notes;
    }

    private static string WorkflowPath(string workflow) => $".github/workflows/{workflow}";

    /// <summary>The C# sources read for a narrowing's conditionals, at most; a larger repository gets a note.</summary>
    internal const int MaxNarrowingSources = 500;

    private const long MaxNarrowingFileBytes = 256 * 1024;

    /// <summary>
    /// #638 D15: a pull request into each .NET line cut now whose projects target a higher major than the line's (Tools #55,
    /// done by hand), narrowing it with <see cref="LineNarrowing"/>. Read through the forge like the onboarding plan: the
    /// project files and global.json, then the C# sources under a project that targets higher, for its conditionals.
    /// </summary>
    private async Task<IReadOnlyList<string>> NarrowAsync(Repository repository, IForgeMergeQueue git, IForgeDependencyUpdates pulls, IEnumerable<string> lines,
        string defaultBranch, CancellationToken cancellationToken)
    {
        var integration = integrations.For(repository);
        var pattern = VersioningYml.Parse(await integration.GetFileContentAsync(repository, defaultBranch, CoverageYml.FileName, cancellationToken)).Config?.EffectiveLines
            ?? CodeCoverage.Versioning.Releases.ReleaseLinePattern.Default;
        var notes = new List<string>();
        foreach (var branch in lines)
        {
            if (!pattern.TryMatch(branch, out var line) || line is not { Ecosystem: CodeCoverage.Versioning.Releases.Ecosystems.Dotnet })
                continue;
            try
            {
                if (await ForgeRepositoryTree.OpenAsync(integration, repository, branch, cancellationToken) is not { } tree)
                    continue;
                var files = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var path in tree.Entries.Keys.Where(p => LineNarrowing.IsMsBuild(p) || LineNarrowing.IsGlobalJson(p) || LineNarrowing.IsWorkflow(p)))
                    if (await ReadTextAsync(tree, path, cancellationToken) is { } text) files[path] = text;
                var above = files.Where(f => LineNarrowing.IsMsBuild(f.Key) && LineNarrowing.TargetsAbove(line.Major, f.Value))
                    .Select(f => f.Key.LastIndexOf('/') is var slash and >= 0 ? f.Key[..(slash + 1)] : "").ToList();
                if (above.Count == 0)
                    continue;
                var sources = tree.Entries.Keys.Where(p => LineNarrowing.IsSource(p) && above.Any(dir => p.StartsWith(dir, StringComparison.Ordinal)))
                    .Order(StringComparer.Ordinal).ToList();
                foreach (var path in sources.Take(MaxNarrowingSources))
                    if (await ReadTextAsync(tree, path, cancellationToken) is { } text) files[path] = text;

                var release = await session.LoadAsync<PlatformRelease>(PlatformRelease.IdFor(line.Ecosystem, $"{line.Major}.0"), cancellationToken);
                var narrowing = LineNarrowing.Plan(line.Major, files, release?.LatestSdk, release?.LatestVersion);
                if (sources.Count > MaxNarrowingSources)
                    narrowing = narrowing with { Notes = [.. narrowing.Notes, $"{sources.Count - MaxNarrowingSources} C# files were not searched for conditionals; search them for `NET{line.Major + 1}`."] };
                if (narrowing.Files.Count == 0)
                {
                    // Nothing to rewrite, only conditionals or notes: there is no commit to open a pull request with.
                    if (!narrowing.IsEmpty)
                        notes.Add($"{branch} targets above .NET {line.Major} in ways the bot cannot rewrite; see its conditionals by hand");
                    continue;
                }
                var note = await LinePullRequestAsync(repository, git, pulls, branch, line.ToString(), tree.Commit, narrowing.Files, narrow: true, cancellationToken,
                    narrowing.Body(branch));
                notes.Add(note.StartsWith("opened #", StringComparison.Ordinal) ? $"{note} to narrow it to .NET {line.Major}" : note);
            }
            catch (GraphException ex)
            {
                notes.Add($"{branch} was not narrowed: {ex.Message}");
            }
        }
        return notes;
    }

    private static async Task<string?> ReadTextAsync(ForgeRepositoryTree tree, string path, CancellationToken cancellationToken)
    {
        try
        {
            return await tree.ReadAsync(path, MaxNarrowingFileBytes, cancellationToken) is { } bytes ? System.Text.Encoding.UTF8.GetString(bytes) : null;
        }
        catch (GraphException)
        {
            return null;   // over the size limit: not a file a narrowing rewrites
        }
    }

    /// <summary>
    /// A bot pull request into a line: D9's regenerate workflows, or with <paramref name="narrow"/> #638 D15's narrowing,
    /// committed on <paramref name="head"/> by the git writer and opened (or found, on a retry) on the forge.
    /// </summary>
    private async Task<string> LinePullRequestAsync(Repository repository, IForgeMergeQueue git, IForgeDependencyUpdates pulls, string lineBranch, string line,
        string head, IReadOnlyList<GitWriterFile> files, bool narrow, CancellationToken cancellationToken, string? narrowBody = null)
    {
        var what = narrow ? "narrowing" : "workflow";
        var push = await git.GetWriterAccessAsync(repository, null, pushesWorkflowChanges: !narrow, cancellationToken);
        if (push.Access is not { } access)
            return $"no {what} pull request into {lineBranch}: {push.RefusalReason}";
        var identity = new GitWriterIdentity(VersioningController.Tagger.Name, VersioningController.Tagger.Email, time.GetUtcNow());
        GitWriterDependencyResult pushed;
        try
        {
            pushed = await writer.OnboardingAsync(new GitWriterDependencyRequest(
                repository.Provider.ToCanonicalString(), repository.GitHubId.ToString(CultureInfo.InvariantCulture), access.CloneUrl,
                new CommitGraph.SidecarCredential(access.Username, access.Password), lineBranch, head)
            {
                Line = line, Author = identity, Committer = identity, Message = narrow ? OnboardingPlanner.NarrowTitle : OnboardingPlanner.LineTitle, Files = files,
                Narrow = narrow,
            }, cancellationToken);
        }
        catch (GitWriterRefusedException ex)
        {
            return $"no {what} pull request into {lineBranch}: {ex.Message}";
        }
        finally
        {
            await git.RevokeWriterAccessAsync(repository, access, CancellationToken.None);
        }
        if (pushed.Outcome != GitWriterOutcomes.Pushed)
        {
            if (pushed.Outcome is GitWriterOutcomes.Busy or GitWriterOutcomes.Failed)
                throw new GitWriterUnavailableException($"The git writer answered {lineBranch}'s {what} with {pushed.Outcome}: {pushed.Detail}");
            return $"no {what} pull request into {lineBranch} ({pushed.Outcome}): {pushed.Detail}";
        }

        var branch = narrow ? OnboardingPlanner.NarrowBranch(line) : OnboardingPlanner.LineBranch(line);
        var title = narrow ? OnboardingPlanner.NarrowTitle : OnboardingPlanner.LineTitle;
        var pull = await pulls.FindPullRequestAsync(repository, branch, lineBranch, cancellationToken);
        if (pull is null)
        {
            try
            {
                pull = await pulls.CreatePullRequestAsync(repository, branch, lineBranch, title,
                    narrowBody ?? OnboardingPlanner.LineBody(lineBranch, files.Select(f => f.Path)), draft: false, cancellationToken);
            }
            catch (ForgeRequestException ex) when (ex.StatusCode == 422)
            {
                pull = await pulls.FindPullRequestAsync(repository, branch, lineBranch, cancellationToken) ?? throw ex;
            }
        }
        return $"opened #{pull.Number} into {lineBranch}";
    }

    private sealed record Planned(OnboardingPlan? Plan, ForgeRepositoryTree? Tree, IForgeMergeQueue? Git, IForgeDependencyUpdates? Pulls, string? Refusal)
    {
        public static Planned Refused(string why) => new(null, null, null, null, why);
    }

    private async Task<Planned> PlanAsync(Repository repository, IReadOnlySet<EOnboardingFeature> features, IReadOnlyCollection<string> olderLines,
        CancellationToken cancellationToken)
    {
        if (features.Count == 0)
            return Planned.Refused("Pick at least one feature.");
        if (RefusalBeforePlanning(repository) is { } refusal)
            return Planned.Refused(refusal);
        var defaultBranch = repository.DefaultBranch!;
        var git = queues.First(q => q.Provider == repository.Provider);
        var pulls = updates.First(u => u.Provider == repository.Provider);
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
            OlderLines = olderLines,
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
