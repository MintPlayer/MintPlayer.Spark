using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CodeCoverage.Git;
using CodeCoverage.Git.Backports;
using CodeCoverage.Git.MergeQueue;
using CodeCoverage.Git.MergeQueue.Resolution;
using Microsoft.Extensions.Options;
using MintPlayer.SourceGenerators.Attributes;

namespace CodeCoverage.GitWriter.Jobs;

/// <summary>A validated <c>POST /jobs/versioning/backport</c>.</summary>
public sealed record BackportJob(
    string Repo, Uri CloneUrl, GitCredential Credential, string SourceBranch, string Commit, int PullRequest, string Title,
    IReadOnlyList<string> Subjects, GitIdentity Committer, IReadOnlyList<BackportTarget> Targets, PlatformMarkerOptions? Markers = null);

/// <summary>A validated <c>POST /jobs/versioning/backport/regenerated</c> (#570 M6).</summary>
public sealed record BackportRegeneratedJob(
    string Repo, Uri CloneUrl, GitCredential Credential, string Head, string ExpectedSha, byte[] Patch, IReadOnlyList<PathGlob> RegeneratedPaths, GitIdentity Committer);

/// <summary>A release line, the branch its backport goes to, the auto-resolve rules for its conflicts (#570 M5),
/// whether a conflict is pushed as a draft (D7), and the paths the line regenerates instead of merging (D9, M6).</summary>
public sealed record BackportTarget(string Branch, string Head, IReadOnlyList<AutoResolveRule> Rules, bool Draft = false,
    IReadOnlyList<PathGlob>? RegeneratedPaths = null);

/// <summary>A validated <c>POST /jobs/versioning/backport/trial</c> (segment G): <c>Base..Commit</c> tried on each target,
/// with <see cref="Commit"/> fetched through <see cref="HeadRef"/>.</summary>
public sealed record BackportTrialJob(string Repo, Uri CloneUrl, GitCredential Credential, string HeadRef, string Base, string Commit,
    IReadOnlyList<BackportTrialTarget> Targets, PlatformMarkerOptions? Markers = null);

/// <summary>A line a trial pick tries, merged as its backport would be.</summary>
public sealed record BackportTrialTarget(string Branch, IReadOnlyList<AutoResolveRule> Rules, IReadOnlyList<PathGlob>? RegeneratedPaths = null);

/// <summary>
/// A backport (#153 M10, PRD D10): the merged pull request's change applied to each release branch as one new commit,
/// pushed to a new <c>backport/…</c> branch for the app to open a pull request from. Spike S8 showed the merge queue's
/// replay does not fit (it rebases a branch onto a moving base); this is plumbing instead. Each target is a three-way
/// <c>merge-tree</c> of the change against its own parent, so a cherry-pick in memory, after the platform values of the
/// parent and the change are rewritten to the line's (#570 S2, D8), so a marker-only hunk vanishes. Every target has
/// its own outcome (#570 D6, superseding #153 D10's all-or-nothing): the source pull request is merged already, so a
/// conflict on one line buys nothing by holding back another.
/// </summary>
/// <remarks>
/// How the pull request was merged decides what "the change" is. A merge commit's is <c>c^1..c</c>; a squash's is the
/// commit itself; a rebase merge left N commits whose subjects are the pull request's, and the change is <c>c~N..c</c>,
/// applied as one commit. The message is built here: the title and pull request, then one
/// <c>(cherry picked from commit …)</c> trailer per original commit, the same trailer <c>git cherry-pick -x</c> writes.
/// </remarks>
public sealed partial class BackportService
{
    [Inject] private readonly JobRunner jobs;
    [Inject] private readonly GitRunner git;
    [Inject] private readonly ConflictResolverRegistry registry;
    [Inject] private readonly IOptions<GitWriterOptions> options;

    public const string Merge = "merge";
    public const string Squash = "squash";
    public const string Rebase = "rebase";

    public const string Created = "created";
    public const string Exists = "exists";
    public const string Empty = "empty";
    public const string Rejected = "rejected";
    public const string Conflicted = "conflicted";
    public const string Drafted = "drafted";

    public Task<VersioningBackportResponse> RunAsync(BackportJob job) => jobs.RunAsync("versioning-backport", job.Repo, async context =>
    {
        await context.RequireAsync(["init", "--bare", "--quiet", "--template=", context.GitDirectory], inRepository: false);
        string[] fetch = ["fetch", "--quiet", "--no-tags", "--no-write-fetch-head", job.CloneUrl.AbsoluteUri,
            .. new[] { job.SourceBranch }.Concat(job.Targets.Select(t => t.Branch)).Distinct(StringComparer.Ordinal).Select(b => $"+refs/heads/{b}:refs/heads/{b}")];
        await context.RequireAsync(fetch, job.Credential);
        context.EnsureWithinDisk();

        var onSource = await context.GitAsync(["merge-base", "--is-ancestor", job.Commit, "refs/heads/" + job.SourceBranch], null);
        if (onSource.ExitCode != 0)
            throw new JobAbortedException(JobReasons.Unplannable, $"The commit {job.Commit} is not on {job.SourceBranch}.");

        var commit = await ReadAsync(context, job.Commit);
        var (mode, @base, picked) = await ChangeAsync(context, job, commit);
        var changed = await ChangedPathsAsync(context, @base, job.Commit);

        var message = Message(job, commit, picked);
        var author = Author(commit.Raw) ?? job.Committer;
        var results = new List<BackportTargetResult>();
        var raisesByBranch = new Dictionary<string, IReadOnlyList<DependencyRaise>>(StringComparer.Ordinal);
        foreach (var (branch, head, rules, draft, regeneratedPaths) in job.Targets)
        {
            var tip = await TipAsync(context, branch);
            var (mergeBase, theirs, regenerated, platformChanges, raises, merged) = await PickAsync(context, job.Credential, job.Markers, job.Committer, tip, @base,
                job.Commit, changed, rules, regeneratedPaths);
            raisesByBranch[branch] = raises;

            // Each line stands on its own (#570 D6): a conflict here holds back no other line.
            if (merged is MergeOutcome.Evicted evicted)
            {
                var detail = evicted.Reason == EvictionReason.ConflictOutsideAllowList ? null : evicted.Detail;
                if (draft && evicted.Reason == EvictionReason.ConflictOutsideAllowList
                    && await ConflictTreeAsync(context, mergeBase, tip, theirs) is { } conflictTree)
                {
                    // D7 draft: git's own merge of the normalized sides, conflict markers and all, rules not applied; the
                    // draft pull request stays red on NoConflictMarkers until somebody resolves it.
                    var drafted = WriterService.Oid(await context.RequireAsync(["commit-tree", conflictTree, "-p", tip, "-F", "-"], input: message, author: author,
                        committer: job.Committer), "commit-tree");
                    var pushed = await WriterService.PushAsync(context, job.CloneUrl, job.Credential, drafted, "refs/heads/" + head, expected: null);
                    results.Add(pushed switch
                    {
                        WriterService.Push.Done or WriterService.Push.UpToDate => new BackportTargetResult(branch, head, drafted, Drafted, evicted.Paths, platformChanges, []),
                        WriterService.Push.LeaseRejected => new BackportTargetResult(branch, head, drafted, Exists, evicted.Paths, platformChanges, []),
                        WriterService.Push.Rejected rejected => new BackportTargetResult(branch, head, drafted, Rejected, evicted.Paths, platformChanges, [], rejected.Detail),
                        _ => throw new InvalidOperationException(),
                    });
                    continue;
                }
                results.Add(new BackportTargetResult(branch, head, null, Conflicted, evicted.Paths, platformChanges, [], detail));
                continue;
            }
            var (tree, resolutions) = merged switch
            {
                MergeOutcome.Clean clean => (clean.Tree, (IReadOnlyList<ResolutionWire>)[]),
                MergeOutcome.Resolved resolved => (resolved.Tree, [.. resolved.Resolutions.Select(r => new ResolutionWire(r.Path, r.Strategy, r.Regenerate, r.Rule))]),
                _ => throw new InvalidOperationException("Unknown merge outcome."),
            };
            if (await TreeAsync(context, tip) == tree)
            {
                results.Add(new BackportTargetResult(branch, head, null, Empty, [], platformChanges, resolutions, Regenerate: []));
                continue;
            }
            IReadOnlyList<string> regenerate = [.. regenerated.Concat(resolutions.Where(r => r.Regenerate).Select(r => r.Path)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
            var built = WriterService.Oid(await context.RequireAsync(["commit-tree", tree, "-p", tip, "-F", "-"], input: message, author: author, committer: job.Committer),
                "commit-tree");
            var push = await WriterService.PushAsync(context, job.CloneUrl, job.Credential, built, "refs/heads/" + head, expected: null);
            results.Add(push switch
            {
                WriterService.Push.Done or WriterService.Push.UpToDate => new BackportTargetResult(branch, head, built, Created, [], platformChanges, resolutions, Regenerate: regenerate),
                WriterService.Push.LeaseRejected => new BackportTargetResult(branch, head, built, Exists, [], platformChanges, resolutions, Regenerate: regenerate),
                WriterService.Push.Rejected rejected => new BackportTargetResult(branch, head, built, Rejected, [], platformChanges, resolutions, rejected.Detail, regenerate),
                _ => throw new InvalidOperationException(),
            });
        }
        // #570 D11: whatever the line's outcome, the first-party raises normalization dropped go with it.
        var withRaises = results.Select(r => raisesByBranch.TryGetValue(r.Branch, out var dropped) ? r with { Raises = dropped } : r).ToList();
        return new VersioningBackportResponse(JobOutcomes.Backported, null, mode, @base, withRaises, null, context.ElapsedMilliseconds);
    }, (outcome, reason, detail, ms) => new VersioningBackportResponse(outcome, reason, null, null, [], detail, ms));

    /// <summary>The identity of the throwaway commits a trial pick writes; they never leave the job.</summary>
    private static readonly GitIdentity TrialIdentity = new("CodeCoverage trial pick", "trial-pick@coverage.invalid");

    /// <summary>
    /// Segment G: the backport a merge would start, tried before the merge and pushed nowhere. The change is
    /// <c>Base..Commit</c>, the queued commit against the default branch it was tested on, fetched through
    /// <see cref="BackportTrialJob.HeadRef"/> since it is not on the default branch yet. Each line is merged the way the
    /// backport merges it (platform markers normalized, the line's auto-resolve rules, its regenerated paths kept), so a line
    /// the trial calls clean is one the backport opens a pull request for.
    /// </summary>
    public Task<BackportTrialResponse> TrialAsync(BackportTrialJob job) => jobs.RunAsync("versioning-backport-trial", job.Repo, async context =>
    {
        const string head = "refs/heads/coverage-trial-head";
        await context.RequireAsync(["init", "--bare", "--quiet", "--template=", context.GitDirectory], inRepository: false);
        await context.RequireAsync(["fetch", "--quiet", "--no-tags", "--no-write-fetch-head", job.CloneUrl.AbsoluteUri, $"+{job.HeadRef}:{head}",
            .. job.Targets.Select(t => $"+refs/heads/{t.Branch}:refs/heads/{t.Branch}")], job.Credential);
        context.EnsureWithinDisk();

        var fetched = await TipAsync(context, "coverage-trial-head");
        if (!string.Equals(fetched, job.Commit, StringComparison.OrdinalIgnoreCase))
            return new BackportTrialResponse(JobOutcomes.HeadMismatch, null, [], $"{job.HeadRef} is at {fetched}, not {job.Commit}.", context.ElapsedMilliseconds);
        if ((await context.GitAsync(["merge-base", "--is-ancestor", job.Base, job.Commit], null)).ExitCode != 0)
            throw new JobAbortedException(JobReasons.Unplannable, $"The base {job.Base} is not an ancestor of {job.Commit}.");

        var changed = await ChangedPathsAsync(context, job.Base, job.Commit);
        var results = new List<BackportTrialTargetResult>();
        foreach (var (branch, rules, regeneratedPaths) in job.Targets)
        {
            var tip = await TipAsync(context, branch);
            var pick = await PickAsync(context, job.Credential, job.Markers, TrialIdentity, tip, job.Base, job.Commit, changed, rules, regeneratedPaths);
            results.Add(pick.Merged switch
            {
                MergeOutcome.Evicted evicted => new BackportTrialTargetResult(branch, Conflicted, evicted.Paths,
                    evicted.Reason == EvictionReason.ConflictOutsideAllowList ? null : evicted.Detail),
                MergeOutcome.Clean clean => new BackportTrialTargetResult(branch, await TreeAsync(context, tip) == clean.Tree ? Empty : Clean, []),
                MergeOutcome.Resolved resolved => new BackportTrialTargetResult(branch, await TreeAsync(context, tip) == resolved.Tree ? Empty : Clean, []),
                _ => throw new InvalidOperationException("Unknown merge outcome."),
            });
        }
        return new BackportTrialResponse(JobOutcomes.Tried, null, results, null, context.ElapsedMilliseconds);
    }, (outcome, reason, detail, ms) => new BackportTrialResponse(outcome, reason, [], detail, ms));

    /// <summary>A trial target the change applies to, cleanly or through the line's rules.</summary>
    public const string Clean = "clean";

    private sealed record Pick(string MergeBase, string Theirs, IReadOnlyList<string> Regenerated, IReadOnlyList<PlatformChange> PlatformChanges,
        IReadOnlyList<DependencyRaise> Raises, MergeOutcome Merged);

    /// <summary>One line's in-memory merge of the change, shared by the backport and its trial.</summary>
    private async Task<Pick> PickAsync(JobContext context, GitCredential credential, PlatformMarkerOptions? markers, GitIdentity committer, string tip,
        string @base, string commit, IReadOnlyList<string> changed, IReadOnlyList<AutoResolveRule> rules, IReadOnlyList<PathGlob>? regeneratedPaths)
    {
        // D9 (M6): a regenerated path the change touches is never merged. The change's copy is reset to its parent's,
        // so the merge keeps the line's, and the line's own workflow regenerates it before the pull request opens.
        var regenerated = regeneratedPaths is { Count: > 0 } globs ? changed.Where(p => globs.Any(g => g.Matches(p))).ToList() : [];
        var markerPaths = changed.Where(p => !regenerated.Contains(p, StringComparer.Ordinal) && PlatformMarkers.Handles(p, markers)).ToList();
        var (mergeBase, theirs, platformChanges, raises) = await NormalizeAsync(context, markers, committer, tip, @base, commit, markerPaths, regenerated);

        // The merge queue's resolver (PRD §5.3 step 2), stage 2 the line: a conflict the line's rules allow is resolved
        // the way the queue would; any other conflict is reported. With no rules every conflict is reported.
        var resolver = new MergeQueueResolver(git, registry) { Timeout = context.Remaining, ScratchDirectory = context.Scratch, Config = JobContext.WriterConfig };
        var merged = await resolver.MergeAsync(context.GitDirectory, tip, theirs, rules, context.Deadline, credential, mergeBase);
        context.EnsureWithinDisk();
        return new Pick(mergeBase, theirs, regenerated, platformChanges, raises, merged);
    }

    private static async Task<string> TipAsync(JobContext context, string branch)
        => WriterService.Oid(await context.RequireAsync(["rev-parse", "--verify", "refs/heads/" + branch + "^{commit}"]), "rev-parse");

    private static async Task<string> TreeAsync(JobContext context, string commit)
        => WriterService.Oid(await context.RequireAsync(["rev-parse", "--verify", commit + "^{tree}"]), "rev-parse");

    /// <summary>
    /// #570 M6 (D9): the line's regenerate patch folded into the backport commit. Validated before anything is fetched
    /// (T4, the merge queue's publish rules), applied to the commit's tree without a worktree, checked to change nothing
    /// undeclared, and committed in place of the backport commit: same parent, message and author, so the line still gets
    /// one commit with its <c>cherry picked from</c> trailers. Pushed with a lease on the commit the workflow built.
    /// </summary>
    public Task<BackportRegeneratedResponse> RegeneratedAsync(BackportRegeneratedJob job)
    {
        var verdict = job.Patch.Length == 0 ? PatchVerdict.Accept([]) : PatchValidator.Validate(job.Patch, job.RegeneratedPaths, options.Value.MaxPatchBytes);
        if (!verdict.Accepted)
            return Task.FromResult(new BackportRegeneratedResponse(JobOutcomes.PatchRejected, verdict.Reason, null, verdict.Paths, verdict.Detail, 0));

        return jobs.RunAsync("versioning-backport-regenerated", job.Repo, async context =>
        {
            BackportRegeneratedResponse Answer(string outcome, string? commit = null, IReadOnlyList<string>? paths = null, string? reason = null, string? detail = null)
                => new(outcome, reason, commit, paths ?? [], detail, context.ElapsedMilliseconds);

            await context.RequireAsync(["init", "--bare", "--quiet", "--template=", context.GitDirectory], inRepository: false);
            await context.RequireAsync(["fetch", "--quiet", "--no-tags", "--no-write-fetch-head", job.CloneUrl.AbsoluteUri, $"+refs/heads/{job.Head}:refs/heads/{job.Head}"],
                job.Credential);
            context.EnsureWithinDisk();
            var tip = WriterService.Oid(await context.RequireAsync(["rev-parse", "--verify", $"refs/heads/{job.Head}^{{commit}}"]), "rev-parse");
            if (!string.Equals(tip, job.ExpectedSha, StringComparison.OrdinalIgnoreCase))
                return Answer(JobOutcomes.BranchMoved, detail: $"{job.Head} is at {tip}, not {job.ExpectedSha}.");
            if (verdict.Paths.Count == 0)
                return Answer(JobOutcomes.Regenerated, tip);

            var commit = await ReadAsync(context, tip);
            if (commit.Parents.Count != 1)
                return Answer(JobOutcomes.BranchMoved, detail: $"{job.Head} is at {tip}, which is not a backport commit.");
            var tree = WriterService.Oid(await context.RequireAsync(["rev-parse", "--verify", tip + "^{tree}"]), "rev-parse");
            var applied = await WriterService.ApplyPatchAsync(context, job.Patch, job.Credential, tree, verdict.Paths, reverse: false);
            if (applied is PatchVerdict rejected)
                return Answer(JobOutcomes.PatchRejected, paths: rejected.Paths, reason: rejected.Reason, detail: rejected.Detail);
            var after = (string)applied;
            if (await WriterService.CheckTreeAsync(context, tree, after, verdict.Paths) is { } unexpected)
                return Answer(JobOutcomes.PatchRejected, paths: unexpected.Paths, reason: unexpected.Reason, detail: unexpected.Detail);
            if (after == tree)
                return Answer(JobOutcomes.Regenerated, tip);

            var changedPaths = (await context.RequireAsync(["diff-tree", "-r", "--no-renames", "--name-only", "-z", tree, after])).StandardOutput
                .Split('\0', StringSplitOptions.RemoveEmptyEntries);
            var folded = WriterService.Oid(await context.RequireAsync(["commit-tree", after, "-p", commit.Parents[0], "-F", "-"], input: commit.Message,
                author: Author(commit.Raw) ?? job.Committer, committer: job.Committer), "commit-tree");
            return await WriterService.PushAsync(context, job.CloneUrl, job.Credential, folded, $"refs/heads/{job.Head}", tip) switch
            {
                WriterService.Push.Done or WriterService.Push.UpToDate => Answer(JobOutcomes.Regenerated, folded, changedPaths),
                WriterService.Push.LeaseRejected => Answer(JobOutcomes.BranchMoved, detail: $"{job.Head} moved during the job."),
                WriterService.Push.Rejected pushRejected => Answer(JobOutcomes.PushRejected, detail: pushRejected.Detail),
                _ => throw new InvalidOperationException("Unknown push outcome."),
            };
        }, (outcome, reason, detail, ms) => new BackportRegeneratedResponse(outcome, reason, null, [], detail, ms));
    }

    /// <summary>Every path the change touches.</summary>
    private static async Task<IReadOnlyList<string>> ChangedPathsAsync(JobContext context, string @base, string commit)
    {
        var diff = await context.RequireAsync(["diff", "--name-only", "-z", "--no-renames", @base, commit]);
        return diff.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>The tree of a plain <c>merge-tree</c> of the change onto the line, conflict markers included; null when git
    /// wrote no tree (exit code above 1).</summary>
    private static async Task<string?> ConflictTreeAsync(JobContext context, string mergeBase, string tip, string theirs)
    {
        var merge = await context.GitAsync(["merge-tree", "--write-tree", "--no-messages", "--merge-base=" + mergeBase, tip, theirs], null);
        if (merge.ExitCode is not (0 or 1))
            return null;
        var first = merge.StandardOutput.Split('\n', 2)[0].Trim();
        return Regex.IsMatch(first, "^[0-9a-f]{40}([0-9a-f]{24})?$") ? first : null;
    }

    /// <summary>
    /// The merge base and the change with their platform values rewritten to the line's (<see cref="PlatformMarkers"/>),
    /// as throwaway commits that are never pushed; the originals when nothing needed rewriting. Plus the platform values
    /// the change edits on the line's own platform, which are left to the merge.
    /// </summary>
    private static async Task<(string Base, string Theirs, IReadOnlyList<PlatformChange> Changes, IReadOnlyList<DependencyRaise> Raises)> NormalizeAsync(
        JobContext context, PlatformMarkerOptions? markers, GitIdentity committer, string tip, string @base, string theirs, IReadOnlyList<string> paths,
        IReadOnlyList<string> regenerated)
    {
        var changes = new List<PlatformChange>();
        var raises = new List<DependencyRaise>();
        var baseFiles = new List<(string Path, string Mode, string Oid)>();
        var theirsFiles = new List<(string Path, string Mode, string Oid)>();
        foreach (var path in regenerated)
        {
            // The parent's entry, or none: the change then leaves the path as its parent had it.
            var entry = (await context.RequireAsync(["ls-tree", "-z", @base, "--", path])).StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            theirsFiles.Add(entry is not null && entry.Split(' ', 3) is [var mode, _, var rest] && rest.Split('\t', 2) is [var oid, _]
                ? (path, mode, oid)
                : (path, "0", new string('0', 40)));
        }
        foreach (var path in paths)
        {
            var ours = await BlobAsync(context, tip, path);
            var before = await BlobAsync(context, @base, path);
            var after = await BlobAsync(context, theirs, path);
            if (ours is { Text: null } || before is { Text: null } || after is { Text: null })
                continue;   // not UTF-8 text: left to the plain merge
            if (PlatformMarkers.Normalize(path, ours?.Text, before?.Text, after?.Text, markers) is not { } normalized)
                continue;
            changes.AddRange(normalized.PlatformChanges);
            raises.AddRange(normalized.Raises);
            if (before is not null && normalized.Base != before.Text)
                baseFiles.Add((path, before.Mode, await HashAsync(context, normalized.Base!)));
            if (after is not null && normalized.Theirs != after.Text)
                theirsFiles.Add((path, after.Mode, await HashAsync(context, normalized.Theirs!)));
        }
        return (await RewriteAsync(context, committer, @base, baseFiles), await RewriteAsync(context, committer, theirs, theirsFiles), changes, raises);
    }

    private static async Task<string> HashAsync(JobContext context, string content)
        => WriterService.Oid(await context.RequireAsync(["hash-object", "-w", "--stdin"], input: content), "hash-object");

    private sealed record Blob(string Mode, string? Text);

    /// <summary>The file at <paramref name="commit"/>; null when absent. <see cref="Blob.Text"/> is null for content that
    /// is not UTF-8, which must not be decoded and written back. A byte order mark is kept.</summary>
    private static async Task<Blob?> BlobAsync(JobContext context, string commit, string path)
    {
        var entry = (await context.RequireAsync(["ls-tree", "-z", commit, "--", path])).StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (entry is null || entry.Split(' ', 3) is not [var mode, "blob", var rest] || rest.Split('\t', 2) is not [var oid, _])
            return null;
        var text = (await context.RequireAsync(["cat-file", "blob", oid])).StandardOutput;
        var size = long.Parse((await context.RequireAsync(["cat-file", "-s", oid])).StandardOutput.Trim(), CultureInfo.InvariantCulture);
        var bytes = Encoding.UTF8.GetByteCount(text);
        if (size == bytes + 3)
            text = "﻿" + text;   // the reader drops the mark; put it back so the rewrite keeps it
        else if (size != bytes || text.Contains('�'))
            return new Blob(mode, null);
        return new Blob(mode, text);
    }

    /// <summary>A throwaway commit of <paramref name="commit"/>'s tree with <paramref name="files"/> set; mode <c>0</c>
    /// removes the path.</summary>
    private static async Task<string> RewriteAsync(JobContext context, GitIdentity committer, string commit, List<(string Path, string Mode, string Oid)> files)
    {
        if (files.Count == 0)
            return commit;
        var index = Path.Combine(context.Scratch, $"normalize-{Guid.NewGuid():N}.index");
        await context.RequireAsync(["read-tree", commit], index: index);
        var info = new StringBuilder();
        foreach (var (path, mode, oid) in files)
            info.Append(mode).Append(' ').Append(oid).Append('\t').Append(path).Append('\0');
        await context.RequireAsync(["update-index", "-z", "--index-info"], input: info.ToString(), index: index);
        var tree = WriterService.Oid(await context.RequireAsync(["write-tree"], index: index), "write-tree");
        return WriterService.Oid(await context.RequireAsync(["commit-tree", tree, "-m", "normalized platform markers"], author: committer, committer: committer),
            "commit-tree");
    }

    private sealed record Commit(string Sha, IReadOnlyList<string> Parents, string Message, string Raw);

    private static async Task<Commit> ReadAsync(JobContext context, string sha)
    {
        var raw = (await context.RequireAsync(["cat-file", "commit", sha])).StandardOutput;
        var parsed = CommitObject.Parse(raw) ?? throw new GitCommandException("cat-file", $"{sha} is not a well-formed commit.");
        return new Commit(sha, parsed.Parents, parsed.Message, raw);
    }

    /// <summary>The mode, the base the change is taken against, and the original commits for the trailers.</summary>
    private static async Task<(string Mode, string Base, IReadOnlyList<string> Picked)> ChangeAsync(JobContext context, BackportJob job, Commit commit)
    {
        switch (commit.Parents.Count)
        {
            case 0:
                throw new JobAbortedException(JobReasons.Unplannable, $"The commit {commit.Sha} has no parent to take the change against.");
            case >= 2:
                return (Merge, commit.Parents[0], [commit.Sha]);
        }

        // A rebase merge: the last N first-parent commits, each with one parent, carry the pull request's subjects in order.
        var count = job.Subjects.Count;
        if (count >= 2)
        {
            var run = new List<Commit> { commit };
            while (run.Count < count && run[^1].Parents.Count == 1)
                run.Add(await ReadAsync(context, run[^1].Parents[0]));
            run.Reverse();
            if (run.Count == count && run.All(c => c.Parents.Count == 1) && run.Select(c => Subject(c.Message)).SequenceEqual(job.Subjects, StringComparer.Ordinal))
                return (Rebase, run[0].Parents[0], run.Select(c => c.Sha).ToList());
        }
        return (Squash, commit.Parents[0], [commit.Sha]);
    }

    internal static string Subject(string message)
    {
        var end = message.IndexOf('\n');
        return (end < 0 ? message : message[..end]).Trim();
    }

    /// <summary>A merge or a rebase run gets the pull request's title; a squash keeps its own message, which is the
    /// title and the body the forge wrote. Either way the trailers follow, one per original commit.</summary>
    private static string Message(BackportJob job, Commit commit, IReadOnlyList<string> picked)
    {
        var builder = new StringBuilder();
        if (picked.Count == 1 && commit.Parents.Count == 1)
            builder.Append(commit.Message.TrimEnd());
        else
            builder.Append(CultureInfo.InvariantCulture, $"{job.Title} (#{job.PullRequest})");
        builder.Append("\n\n");
        foreach (var sha in picked)
            builder.Append(CultureInfo.InvariantCulture, $"(cherry picked from commit {sha})\n");
        return builder.ToString();
    }

    /// <summary>The original author with the original date; null when the header is not one a <see cref="GitIdentity"/>
    /// accepts, and then the committer authors the backport.</summary>
    internal static GitIdentity? Author(string raw)
    {
        foreach (var header in raw.Split('\n'))
        {
            if (header.Length == 0) break;
            if (AuthorLine().Match(header) is not { Success: true } match) continue;
            try
            {
                var offset = match.Groups[4].Value;
                var span = new TimeSpan(int.Parse(offset[1..3], CultureInfo.InvariantCulture), int.Parse(offset[3..5], CultureInfo.InvariantCulture), 0);
                var when = DateTimeOffset.FromUnixTimeSeconds(long.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture)).ToOffset(offset[0] == '-' ? -span : span);
                return new GitIdentity(match.Groups[1].Value, match.Groups[2].Value, when);
            }
            catch (Exception e) when (e is ArgumentException or FormatException or OverflowException)
            {
                return null;
            }
        }
        return null;
    }

    [GeneratedRegex(@"^author (.+) <([^<>]*)> (\d{1,12}) ([+-]\d{4})$", RegexOptions.CultureInvariant)]
    private static partial Regex AuthorLine();
}
