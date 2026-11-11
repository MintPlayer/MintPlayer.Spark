using System.Globalization;
using System.Text;
using CodeCoverage.Entities;
using CodeCoverage.Feedback;
using CodeCoverage.Forge;
using CodeCoverage.MergeQueue;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MintPlayer.SourceGenerators.Attributes;

namespace CodeCoverage.PackageVersioning;

/// <summary>What the merge queue does with a trial pick's conflicts: <see cref="Refuse"/> evicts, otherwise the merge goes on
/// and <see cref="Message"/> is posted on the pull request.</summary>
public sealed record BackportTrialVerdict(bool Refuse, string Message, IReadOnlyList<string> Paths);

/// <summary>Segment G: the backports a merge into the default branch would start, tried before it merges.</summary>
public interface IBackportTrialPicks
{
    /// <summary>
    /// Tries <paramref name="baseSha"/>..<paramref name="commit"/> on every line the pull request is labelled
    /// <c>backport: &lt;line&gt;</c> for. Null when no line conflicts, when <c>backport.trialPick</c> is <c>off</c>, or when the
    /// trial could not run: a trial never blocks a merge it could not judge, and the backport after the merge reports what
    /// the trial would have.
    /// </summary>
    Task<BackportTrialVerdict?> RunAsync(Repository repository, int number, string baseSha, string commit, CancellationToken cancellationToken = default);
}

/// <summary>
/// Segment G (owner, 2026-11-11: "In the queue, pre-merge"; "Labelled lines, PR comment"). Before the merge queue merges a
/// pull request into the default branch, the git writer merges its change onto each labelled line exactly as the backport
/// will (<c>POST /jobs/versioning/backport/trial</c>), and pushes nothing. A conflict is said while the author is still
/// around: under <c>backport.trialPick: warn</c> (the default) as one comment, under <c>refuse</c> by the queue not merging.
/// </summary>
/// <remarks>
/// The lines come from the labels, which are set when the pull request opens from the supported lines of the ecosystems it
/// touches (<see cref="BackportTargets"/>), and which a maintainer removes where the change should not go. A current line
/// that is promoted takes no backport (segment P), so it is never tried.
/// </remarks>
[Register(typeof(IBackportTrialPicks), ServiceLifetime.Scoped)]
public partial class BackportTrialPicks : IBackportTrialPicks
{
    [Inject] private readonly IForgeIntegrationResolver integrations;
    [Inject] private readonly IEnumerable<IForgeVersioning> forges;
    [Inject] private readonly IEnumerable<IForgeMergeQueue> queues;
    [Inject] private readonly IGitWriterSidecar writer;
    [Inject] private readonly ILogger<BackportTrialPicks> logger;

    /// <summary>The writer's limit on targets per job (<c>GitWriterVersioningBackportEndpoint.MaxTargets</c>).</summary>
    private const int MaxTargets = 10;

    public async Task<BackportTrialVerdict?> RunAsync(Repository repository, int number, string baseSha, string commit, CancellationToken cancellationToken = default)
    {
        if (repository.DefaultBranch is not { } defaultBranch || !writer.IsConfigured) return null;
        if (forges.FirstOrDefault(f => f.Provider == repository.Provider) is not { } forge) return null;
        if (queues.FirstOrDefault(q => q.Provider == repository.Provider) is not { } git) return null;

        var yml = await integrations.For(repository).GetFileContentAsync(repository, defaultBranch, CoverageYml.FileName, cancellationToken);
        var backport = BackportYml.Parse(yml).Section;
        if (backport.TrialPick == BackportYml.Off && backport.TrialPicks.Values.All(m => m == BackportYml.Off)) return null;
        if (VersioningYml.Parse(yml).Config is not { } config) return null;

        if (await forge.GetPullRequestTextAsync(repository, number, cancellationToken) is not { } pull) return null;
        var labelled = pull.Labels.Where(l => l.StartsWith(BackportTargets.LabelPrefix, StringComparison.Ordinal))
            .Select(l => l[BackportTargets.LabelPrefix.Length..].Trim()).Where(l => l.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        if (labelled.Count == 0) return null;

        var queue = BackportRecipient.QueueSection(yml, defaultBranch);
        var rules = BackportRecipient.AutoResolveRules(queue);
        var regeneratedPaths = BackportRecipient.LineSidePaths(queue);
        var promoted = await BackportRecipient.PromotedBranchesAsync(config, git, repository, cancellationToken);
        var lines = new Dictionary<string, string>(StringComparer.Ordinal);   // branch → the line its label names
        var modes = new Dictionary<string, string>(StringComparer.Ordinal);   // branch → its trial pick mode (Q10, per line)
        foreach (var line in labelled)
        {
            var branch = config.EffectiveLines is { } pattern ? BackportRecipient.ResolveLine(pattern, line) : BackportRecipient.Resolve(config.EffectiveReleaseBranches, line);
            // A label that names no line, or a line without its branch, is the backport's to report after the merge.
            if (branch is null || branch == defaultBranch || promoted.Contains(branch) || lines.ContainsKey(branch)) continue;
            var name = config.EffectiveLines is { } named && named.TryMatch(branch, out var parsed) && parsed is not null ? parsed.ToString() : line;
            var mode = backport.TrialPickFor(name);
            if (mode == BackportYml.Off) continue;
            if (string.IsNullOrEmpty(await git.GetBranchHeadAsync(repository, branch, cancellationToken))) continue;
            lines[branch] = line;
            modes[branch] = mode;
            if (lines.Count == MaxTargets) break;
        }
        if (lines.Count == 0) return null;

        if (await TryAsync(repository, git, number, baseSha, commit,
                [.. lines.Keys.Select(b => new GitWriterBackportTrialTarget(b, rules, regeneratedPaths))], backport, cancellationToken) is not { } result)
            return null;
        var conflicted = (result.Targets ?? []).Where(t => t.Outcome == GitWriterBackportTrialTargetResult.Conflicted && lines.ContainsKey(t.Branch)).ToList();
        if (conflicted.Count == 0) return null;

        // One line under `refuse` that conflicts refuses the merge; the comment still names every conflicting line.
        var refuse = conflicted.Any(t => modes[t.Branch] == BackportYml.Refuse);
        return new BackportTrialVerdict(refuse, Message(conflicted, lines, refuse),
            [.. conflicted.SelectMany(t => t.Conflicts ?? []).Distinct(StringComparer.Ordinal).Take(50)]);
    }

    private async Task<GitWriterBackportTrialResult?> TryAsync(Repository repository, IForgeMergeQueue git, int number, string baseSha, string commit,
        IReadOnlyList<GitWriterBackportTrialTarget> targets, BackportSection backport, CancellationToken cancellationToken)
    {
        var push = await git.GetWriterAccessAsync(repository, null, false, cancellationToken);
        if (push.Access is not { } access)
        {
            logger.LogWarning("Trial pick: no writer access for {Repository}: {Reason}", repository.FullName, push.RefusalReason);
            return null;
        }
        try
        {
            var result = await writer.VersioningBackportTrialAsync(new GitWriterBackportTrialRequest(
                repository.Provider.ToCanonicalString(), repository.GitHubId.ToString(CultureInfo.InvariantCulture), access.CloneUrl,
                new CommitGraph.SidecarCredential(access.Username, access.Password), $"refs/pull/{number.ToString(CultureInfo.InvariantCulture)}/head", baseSha, commit,
                targets, backport.Markers), cancellationToken);
            if (result.Outcome == GitWriterBackportTrialResult.Tried) return result;
            logger.LogWarning("Trial pick of #{Number} in {Repository}: {Outcome} {Reason} {Detail}", number, repository.FullName, result.Outcome, result.Reason, result.Detail);
            return null;
        }
        catch (GitWriterRefusedException ex)
        {
            logger.LogWarning(ex, "Trial pick: the git writer refused #{Number} of {Repository}", number, repository.FullName);
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or NotSupportedException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Trial pick: the git writer did not answer for {Repository}", repository.FullName);
            return null;
        }
        finally
        {
            await git.RevokeWriterAccessAsync(repository, access, CancellationToken.None);
        }
    }

    internal static string Message(IReadOnlyList<GitWriterBackportTrialTargetResult> conflicted, IReadOnlyDictionary<string, string> lines, bool refuse)
    {
        var names = string.Join(", ", conflicted.Select(t => $"`{t.Branch}`"));
        var builder = new StringBuilder(refuse
            ? $"its backport onto {names} would conflict, and `backport.trialPick` refuses that.\n"
            : $"Its backport onto {names} will conflict once this merges. The merge goes ahead; the backport then reports the conflict with the commands to apply it by hand.\n");
        foreach (var target in conflicted)
        {
            builder.Append(CultureInfo.InvariantCulture, $"\n`{target.Branch}` conflicts on:\n");
            foreach (var path in (target.Conflicts ?? []).Take(50))
                builder.Append(CultureInfo.InvariantCulture, $"- `{path}`\n");
            if (target.Detail is { Length: > 0 } why)
                builder.Append(CultureInfo.InvariantCulture, $"\n{why}\n");
        }
        var labels = string.Join(", ", conflicted.Select(t => $"`{BackportTargets.LabelPrefix}{lines[t.Branch]}`"));
        builder.Append(refuse
            ? $"\nRemove {labels} to leave the line out, or change the pull request so it applies there, then queue it again."
            : $"\nRemove {labels} before it merges to leave the line out.");
        return builder.ToString();
    }
}
