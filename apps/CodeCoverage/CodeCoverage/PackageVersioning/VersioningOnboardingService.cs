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
    Task<bool> PushSeedsAsync(Repository repository, CancellationToken cancellationToken = default);
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
    [Inject] private readonly TimeProvider time;

    private DateTime Now => time.GetUtcNow().UtcDateTime;

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
