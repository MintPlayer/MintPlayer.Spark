using System.Globalization;
using CodeCoverage.Entities;
using CodeCoverage.Forge;
using CodeCoverage.MergeQueue;
using CodeCoverage.Versioning.Versions;
using MintPlayer.SourceGenerators.Attributes;
using Raven.Client.Documents.Session;

namespace CodeCoverage.PackageVersioning;

/// <summary>
/// The versioning onboarding's seed tags (#153 M13, PRD D17), which carry each package's current version over once the
/// onboarding pull request merges. The pull request itself is the chooser's since #571 D3 (<see cref="Onboarding.IOnboardingService"/>).
/// </summary>
public interface IVersioningOnboardingService
{
    /// <summary>
    /// Pushes the pending seed tags at the onboarding pull request's merge commit, when it has merged. Returns false when
    /// seeds are pending on a merged pull request and could not be pushed: a versioning run must not go ahead without them,
    /// or it would release the untagged packages from scratch. The caller holds <see cref="VersioningRunLocks"/>.
    /// </summary>
    /// <remarks>#638 D5-D7: the history backfill runs first; a seed whose version it tagged at an earlier commit is not pushed again.</remarks>
    Task<bool> PushSeedsAsync(Repository repository, CancellationToken cancellationToken = default);

    /// <summary>
    /// #638 D5-D8: tags every version the default branch's and the release lines' history declares at its earliest occurrence,
    /// when the default branch's <c>coverage.yml</c> has a versioning section. What was done, or why not; null when there was
    /// nothing to tag. <paramref name="move"/>: the collisions (D9) the owner chose to move, by tag name.
    /// </summary>
    Task<string?> BackfillAsync(Repository repository, IReadOnlyCollection<string> move, CancellationToken cancellationToken = default);

    /// <summary>#638 D9: the App-made tags away from their version's earliest occurrence, from a backfill plan that writes nothing.</summary>
    Task<IReadOnlyList<GitWriterBackfillCollision>> CollisionsAsync(Repository repository, CancellationToken cancellationToken = default);
}

/// <param name="Outcome"><see cref="Opened"/>, <see cref="Seeded"/>, <see cref="Nothing"/> or <see cref="Refused"/>.</param>
public sealed record VersioningOnboardingOutcome(string Outcome, string Note, int? PullRequest = null)
{
    public const string Opened = "opened";
    public const string Seeded = "seeded";
    /// <summary>#571 M10d: the onboarding pull request's merge was handled.</summary>
    public const string Merged = "merged";
    public const string Nothing = "nothing";
    public const string Refused = "refused";
}

[Register(typeof(IVersioningOnboardingService), ServiceLifetime.Scoped)]
public partial class VersioningOnboardingService : IVersioningOnboardingService
{
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly IEnumerable<IForgeVersioning> forges;
    [Inject] private readonly IEnumerable<IForgeMergeQueue> queues;
    [Inject] private readonly IGitWriterSidecar writer;
    [Inject] private readonly IForgeIntegrationResolver forgeFiles;
    [Inject] private readonly TimeProvider time;

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    public async Task<bool> PushSeedsAsync(Repository repository, CancellationToken cancellationToken = default)
    {
        var state = await session.LoadAsync<VersioningOnboardingState>(VersioningOnboardingState.ForRepository(repository.Id!), cancellationToken);
        // docs/onboarding_merge_backfill_PRD.md D1: a merge with no seeds still backfills, when the onboarding asked for it.
        if (state is not ({ SeedsPending: true } or { BackfillPending: true }) || repository.DefaultBranch is not { } defaultBranch) return true;
        if (forges.FirstOrDefault(f => f.Provider == repository.Provider) is not { } forge
            || queues.FirstOrDefault(q => q.Provider == repository.Provider) is not { } git)
            return true;

        // The merge commit is read from the forge, never from a webhook: the run that may race the webhook needs it too.
        var pull = await forge.GetPullRequestTextAsync(repository, state.PullRequestNumber!.Value, cancellationToken);
        if (pull is not { Merged: true, MergeCommitSha: { } merge }) return true;

        // A backfill that fails leaves the seeds to carry the current versions, as before #638; its note says why.
        var backfill = await BackfillCoreAsync(repository, git, defaultBranch, state.MoveTags, cancellationToken);
        var seeds = state.Seeds.Where(s => !backfill.Covered.Contains(VersionTag.Prefix(s.PackageId) + s.Version)).ToList();
        if (seeds.Count > 0 && await TagAsync(repository, git, defaultBranch, merge, seeds, cancellationToken) is { } refusal)
        {
            await NoteAsync(state, new(VersioningOnboardingOutcome.Refused, Join(backfill.Note, refusal)), cancellationToken);
            return false;
        }
        state.SeedCommit = merge;
        state.BackfilledAt = merge;
        // The note saves the state too: without it the merge would be handled again on every run.
        var pushed = seeds.Count > 0 ? $"Pushed {seeds.Count} seed tag(s) at the merge commit {Short(merge)}." : null;
        await NoteAsync(state, new(VersioningOnboardingOutcome.Seeded, (backfill.Note, pushed) switch
        {
            (null, null) => "The history backfill found nothing to tag.",
            ({ } note, null) => note,
            var (note, seeded) => Join(note, seeded!),
        }), cancellationToken);
        return true;
    }

    public async Task<string?> BackfillAsync(Repository repository, IReadOnlyCollection<string> move, CancellationToken cancellationToken = default)
    {
        if (repository.DefaultBranch is not { } defaultBranch || queues.FirstOrDefault(q => q.Provider == repository.Provider) is not { } git) return null;
        return (await BackfillCoreAsync(repository, git, defaultBranch, move, cancellationToken)).Note;
    }

    public async Task<IReadOnlyList<GitWriterBackfillCollision>> CollisionsAsync(Repository repository, CancellationToken cancellationToken = default)
    {
        if (repository.DefaultBranch is not { } defaultBranch || queues.FirstOrDefault(q => q.Provider == repository.Provider) is not { } git) return [];
        var (result, _) = await RunBackfillAsync(repository, git, defaultBranch, planOnly: true, [], cancellationToken);
        return result is { Outcome: GitWriterBackfillResult.Planned } ? result.Collisions ?? [] : [];
    }

    /// <summary>The backfill; <c>Covered</c> holds the tags in place afterwards (created, already there, or a collision's).</summary>
    private async Task<(IReadOnlySet<string> Covered, string? Note)> BackfillCoreAsync(Repository repository, IForgeMergeQueue git, string defaultBranch,
        IReadOnlyCollection<string> move, CancellationToken cancellationToken)
    {
        var (result, refusal) = await RunBackfillAsync(repository, git, defaultBranch, planOnly: false, move, cancellationToken);
        if (result is null) return (new HashSet<string>(), refusal);
        if (result.Outcome != GitWriterBackfillResult.Tagged)
            return (new HashSet<string>(), $"The history was not backfilled ({result.Reason}): {result.Detail}");

        var tags = result.Tags ?? [];
        var collisions = result.Collisions ?? [];
        var covered = tags.Where(t => t.Outcome is GitWriterTagResult.Created or GitWriterTagResult.AlreadyPresent).Select(t => t.Name)
            .Concat(collisions.Select(c => c.Name)).ToHashSet(StringComparer.Ordinal);
        var created = tags.Count(t => t.Outcome == GitWriterTagResult.Created);
        var failed = tags.Where(t => t.Outcome is not (GitWriterTagResult.Created or GitWriterTagResult.AlreadyPresent)).ToList();
        var moved = collisions.Count(c => c.Outcome == GitWriterBackfillCollision.Moved);
        var unmoved = collisions.Where(c => move.Contains(c.Name) && c.Outcome != GitWriterBackfillCollision.Moved).ToList();
        var parts = new List<string>();
        if (created > 0) parts.Add($"tagged {created} version(s) from the history");
        if (moved > 0) parts.Add($"moved {moved} tag(s) onto their earliest occurrence");
        if (failed.Count > 0) parts.Add("not tagged: " + string.Join("; ", failed.Take(10).Select(t => $"{t.Name} ({t.Outcome}{(t.Detail is { } d ? $": {d}" : null)})")));
        if (unmoved.Count > 0) parts.Add("not moved: " + string.Join("; ", unmoved.Take(10).Select(c => $"{c.Name} ({c.Outcome}{(c.Detail is { } d ? $": {d}" : null)})")));
        parts.AddRange(result.Notes ?? []);
        return (covered, parts.Count == 0 ? null : "History backfill: " + string.Join("; ", parts) + ".");
    }

    /// <summary>One backfill job; a null result comes with why it did not run (no versioning section is neither).</summary>
    private async Task<(GitWriterBackfillResult? Result, string? Refusal)> RunBackfillAsync(Repository repository, IForgeMergeQueue git, string defaultBranch,
        bool planOnly, IReadOnlyCollection<string> move, CancellationToken cancellationToken)
    {
        // D17 of #153: the section is read from the default branch, as a versioning run reads it.
        VersioningYmlResult parsed;
        try
        {
            parsed = VersioningYml.Parse(await forgeFiles.For(repository).GetFileContentAsync(repository, defaultBranch, Feedback.CoverageYml.FileName, cancellationToken));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (null, $"The history was not backfilled: {Feedback.CoverageYml.FileName} could not be read ({ex.Message}).");
        }
        if (parsed is not { Config: { } config, Errors.Count: 0 }) return (null, null);

        // A plan reads only, but the job fetches with the same credential as a write.
        var push = await git.GetWriterAccessAsync(repository, null, false, cancellationToken);
        if (push.Access is not { } access)
            return (null, $"The history was not backfilled: the App could not push tags ({push.RefusalReason}).");
        GitWriterBackfillResult result;
        try
        {
            result = await writer.VersioningBackfillAsync(new GitWriterBackfillRequest(
                repository.Provider.ToCanonicalString(), repository.GitHubId.ToString(CultureInfo.InvariantCulture), access.CloneUrl,
                new CommitGraph.SidecarCredential(access.Username, access.Password), defaultBranch, VersioningController.Tagger, config.ToSettings(),
                planOnly, [.. move]), cancellationToken);
        }
        catch (GitWriterRefusedException ex)
        {
            return (null, $"The history was not backfilled: {ex.Message}");
        }
        finally
        {
            await git.RevokeWriterAccessAsync(repository, access, CancellationToken.None);
        }
        if (result.Outcome is GitWriterOutcomes.Busy or GitWriterOutcomes.Failed)
            throw new GitWriterUnavailableException($"The git writer answered the history backfill with {result.Outcome}: {result.Detail}");
        return (result, null);
    }

    private static string Join(string? first, string second) => first is null ? second : first + " " + second;

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
