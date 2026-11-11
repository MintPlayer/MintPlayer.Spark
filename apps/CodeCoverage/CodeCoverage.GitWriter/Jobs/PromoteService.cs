using CodeCoverage.Git;
using CodeCoverage.Versioning.Git;
using CodeCoverage.Versioning.Graph;
using CodeCoverage.Versioning.Releases;

namespace CodeCoverage.GitWriter.Jobs;

/// <summary>A validated <c>POST /jobs/versioning/promote</c> (segment P).</summary>
/// <param name="Commit">The green default-branch commit every line moves to.</param>
/// <param name="Settings">For the change detection of a line with <see cref="PromoteLine.OnlyIfChanged"/> (P4).</param>
public sealed record PromoteJob(
    string Repo, Uri CloneUrl, GitCredential Credential, string DefaultBranch, string Commit, GitIdentity Committer, IReadOnlyList<PromoteLine> Lines,
    VersioningSettings Settings);

/// <summary>A line to promote: its branch, its ecosystem, and whether it moves only when that ecosystem changed (P4).</summary>
public sealed record PromoteLine(string Branch, string Ecosystem, bool OnlyIfChanged);

/// <summary>
/// The promotion of release lines (segment P, <c>docs/release_lines_recovery_plan.md</c> §2b): each line gets one merge
/// commit whose tree is the default-branch commit's and whose parents are the line's tip and that commit, so the line's
/// tree equals a commit the default branch tested and the line's history keeps every commit it had (Q-A = B).
/// </summary>
/// <remarks>
/// <para>Nothing on a line is dropped silently (P1). The line's own changes since its merge base with the commit are
/// compared path by path: a path the line changed is fine when the commit holds the same content, or when some
/// default-branch commit in between wrote exactly the line's content (an older copy, such as a backport of a change the
/// default branch has since changed again). Any other path is line-only, and the line is refused with those paths and the
/// line commits that touched them.</para>
/// <para>Each line stands on its own: a refusal on one holds back no other. The push carries a lease on the tip that was
/// checked, so a line that moved during the job is reported, never overwritten.</para>
/// </remarks>
public sealed class PromoteService(JobRunner jobs)
{
    public const string Promoted = "promoted";
    /// <summary>The line holds the commit already.</summary>
    public const string Contained = "contained";
    /// <summary>The line's ecosystem has no change since its last promotion (P4), so it was left as it is.</summary>
    public const string Unchanged = "unchanged";
    /// <summary>The line holds changes the default branch does not (P1); nothing was pushed to it.</summary>
    public const string LineOnly = "line-only";
    /// <summary>The line moved during the job.</summary>
    public const string Moved = "moved";
    public const string Rejected = "rejected";

    /// <summary>The most line-only paths and commits a result lists.</summary>
    public const int MaxListed = 50;

    /// <summary>The most suspect paths checked one by one; more is refused as line-only without the copy check.</summary>
    public const int MaxSuspects = 500;

    public Task<VersioningPromoteResponse> RunAsync(PromoteJob job) => jobs.RunAsync("versioning-promote", job.Repo, async context =>
    {
        await context.RequireAsync(["init", "--bare", "--quiet", "--template=", context.GitDirectory], inRepository: false);
        string[] fetch = ["fetch", "--quiet", "--no-tags", "--no-write-fetch-head", job.CloneUrl.AbsoluteUri,
            .. new[] { job.DefaultBranch }.Concat(job.Lines.Select(l => l.Branch)).Distinct(StringComparer.Ordinal).Select(b => $"+refs/heads/{b}:refs/heads/{b}")];
        await context.RequireAsync(fetch, job.Credential);
        context.EnsureWithinDisk();

        if ((await context.GitAsync(["merge-base", "--is-ancestor", job.Commit, "refs/heads/" + job.DefaultBranch], null)).ExitCode != 0)
            throw new JobAbortedException(JobReasons.Unplannable, $"The commit {job.Commit} is not on {job.DefaultBranch}.");
        var commitTree = WriterService.Oid(await context.RequireAsync(["rev-parse", "--verify", job.Commit + "^{tree}"]), "rev-parse");

        var git = new GitCli(context.GitDirectory);
        ProjectGraph? graph = null;
        var results = new List<PromoteLineResult>();
        foreach (var line in job.Lines)
        {
            var tip = WriterService.Oid(await context.RequireAsync(["rev-parse", "--verify", "refs/heads/" + line.Branch + "^{commit}"]), "rev-parse");
            if ((await context.GitAsync(["merge-base", "--is-ancestor", job.Commit, tip], null)).ExitCode == 0)
            {
                results.Add(new PromoteLineResult(line.Branch, Contained, tip, [], []));
                continue;
            }
            var mergeBase = WriterService.Oid(await context.RequireAsync(["merge-base", tip, job.Commit]), "merge-base");

            if (line.OnlyIfChanged)
            {
                try
                {
                    graph ??= await ProjectGraphBuilder.BuildAsync(await GitTree.OpenAsync(git, job.Commit, context.Deadline), job.Settings.Ignore, context.Deadline);
                    var changed = await ChangedEcosystems.OfAsync(graph, new GitInputDiff(git), mergeBase, job.Commit, job.Settings, context.Deadline);
                    if (!changed.Contains(line.Ecosystem, StringComparer.Ordinal))
                    {
                        results.Add(new PromoteLineResult(line.Branch, Unchanged, tip, [], []));
                        continue;
                    }
                }
                catch (GraphException e)
                {
                    throw new JobAbortedException(JobReasons.Unplannable, e.Message);
                }
            }

            var lineOnly = await LineOnlyPathsAsync(context, mergeBase, tip, job.Commit);
            if (lineOnly.Count > 0)
            {
                results.Add(new PromoteLineResult(line.Branch, LineOnly, tip, [.. lineOnly.Take(MaxListed)], await LineCommitsAsync(context, mergeBase, tip, lineOnly)));
                continue;
            }

            var message = $"Promote {line.Branch} to {job.DefaultBranch} {job.Commit[..12]}\n\n"
                + $"The tree is {job.Commit}'s, a commit {job.DefaultBranch}'s workflow passed; the line's history stays as its first parent.\n";
            var promoted = WriterService.Oid(await context.RequireAsync(["commit-tree", commitTree, "-p", tip, "-p", job.Commit, "-F", "-"], input: message,
                author: job.Committer, committer: job.Committer), "commit-tree");
            var push = await WriterService.PushAsync(context, job.CloneUrl, job.Credential, promoted, "refs/heads/" + line.Branch, tip);
            results.Add(push switch
            {
                WriterService.Push.Done or WriterService.Push.UpToDate => new PromoteLineResult(line.Branch, Promoted, promoted, [], []),
                WriterService.Push.LeaseRejected => new PromoteLineResult(line.Branch, Moved, tip, [], [], $"{line.Branch} moved during the promotion."),
                WriterService.Push.Rejected rejected => new PromoteLineResult(line.Branch, Rejected, tip, [], [], rejected.Detail),
                _ => throw new InvalidOperationException("Unknown push outcome."),
            });
        }
        return new VersioningPromoteResponse(JobOutcomes.Promoted, null, results, null, context.ElapsedMilliseconds);
    }, (outcome, reason, detail, ms) => new VersioningPromoteResponse(outcome, reason, [], detail, ms));

    /// <summary>
    /// The paths the line changed since <paramref name="mergeBase"/> whose content neither <paramref name="commit"/> nor
    /// any default-branch commit between the two holds (P1), sorted.
    /// </summary>
    internal static async Task<IReadOnlyList<string>> LineOnlyPathsAsync(JobContext context, string mergeBase, string tip, string commit)
    {
        var lineChanged = await NamesAsync(context, mergeBase, tip);
        if (lineChanged.Count == 0) return [];
        var differs = (await NamesAsync(context, tip, commit)).ToHashSet(StringComparer.Ordinal);
        var suspects = lineChanged.Where(differs.Contains).Order(StringComparer.Ordinal).ToList();
        if (suspects.Count > MaxSuspects) return suspects;

        var lineOnly = new List<string>();
        foreach (var path in suspects)
        {
            var onLine = await BlobAsync(context, tip, path);
            // Every content the default branch gave the path between the merge base and the commit, from --raw's new side.
            var log = await context.RequireAsync(["log", "--format=", "--raw", "--no-abbrev", "--no-renames", $"{mergeBase}..{commit}", "--", path]);
            var written = log.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Split(' ', 5)).Where(f => f.Length == 5).Select(f => f[3]).ToHashSet(StringComparer.Ordinal);
            var copied = onLine is null ? written.Any(o => o.All(c => c == '0')) : written.Contains(onLine);
            if (!copied) lineOnly.Add(path);
        }
        return lineOnly;
    }

    private static async Task<IReadOnlyList<string>> NamesAsync(JobContext context, string from, string to)
        => (await context.RequireAsync(["diff-tree", "-r", "--no-renames", "--name-only", "-z", from, to])).StandardOutput
            .Split('\0', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>The blob at <paramref name="path"/> in <paramref name="commit"/>, or null when the path is absent there.</summary>
    private static async Task<string?> BlobAsync(JobContext context, string commit, string path)
    {
        var result = await context.GitAsync(["rev-parse", "--verify", "--quiet", $"{commit}:{path}"], null);
        return result.ExitCode == 0 ? result.StandardOutput.Trim() : null;
    }

    /// <summary>The line's own commits that touched <paramref name="paths"/>, newest first, as <c>sha subject</c>.</summary>
    private static async Task<IReadOnlyList<string>> LineCommitsAsync(JobContext context, string mergeBase, string tip, IReadOnlyList<string> paths)
    {
        var log = await context.RequireAsync(["log", "--first-parent", "--no-merges", $"--max-count={MaxListed}", "--format=%H %s", $"{mergeBase}..{tip}", "--",
            .. paths.Take(MaxListed)]);
        return log.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();
    }
}
