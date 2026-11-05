using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CodeCoverage.Git;
using CodeCoverage.Git.Backports;
using CodeCoverage.Git.MergeQueue;
using CodeCoverage.Git.MergeQueue.Resolution;
using MintPlayer.SourceGenerators.Attributes;

namespace CodeCoverage.GitWriter.Jobs;

/// <summary>A validated <c>POST /jobs/versioning/backport</c>.</summary>
public sealed record BackportJob(
    string Repo, Uri CloneUrl, GitCredential Credential, string SourceBranch, string Commit, int PullRequest, string Title,
    IReadOnlyList<string> Subjects, GitIdentity Committer, IReadOnlyList<BackportTarget> Targets, PlatformMarkerOptions? Markers = null);

/// <summary>A release line, the branch its backport goes to, the auto-resolve rules for its conflicts (#570 M5), and
/// whether a conflict is pushed as a draft (D7).</summary>
public sealed record BackportTarget(string Branch, string Head, IReadOnlyList<AutoResolveRule> Rules, bool Draft = false);

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
        var markerPaths = await MarkerPathsAsync(context, @base, job.Commit, job.Markers);

        var message = Message(job, commit, picked);
        var author = Author(commit.Raw) ?? job.Committer;
        var results = new List<BackportTargetResult>();
        foreach (var (branch, head, rules, draft) in job.Targets)
        {
            var tip = WriterService.Oid(await context.RequireAsync(["rev-parse", "--verify", "refs/heads/" + branch + "^{commit}"]), "rev-parse");
            var (mergeBase, theirs, platformChanges) = await NormalizeAsync(context, job, tip, @base, job.Commit, markerPaths);

            // The merge queue's resolver (PRD §5.3 step 2), stage 2 the line: a conflict the line's rules allow is resolved
            // the way the queue would; any other conflict is reported. With no rules every conflict is reported.
            var resolver = new MergeQueueResolver(git, registry) { Timeout = context.Remaining, ScratchDirectory = context.Scratch, Config = JobContext.WriterConfig };
            var merged = await resolver.MergeAsync(context.GitDirectory, tip, theirs, rules, context.Deadline, job.Credential, mergeBase);
            context.EnsureWithinDisk();

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
            var tipTree = WriterService.Oid(await context.RequireAsync(["rev-parse", "--verify", tip + "^{tree}"]), "rev-parse");
            if (tipTree == tree)
            {
                results.Add(new BackportTargetResult(branch, head, null, Empty, [], platformChanges, resolutions));
                continue;
            }
            var built = WriterService.Oid(await context.RequireAsync(["commit-tree", tree, "-p", tip, "-F", "-"], input: message, author: author, committer: job.Committer),
                "commit-tree");
            var push = await WriterService.PushAsync(context, job.CloneUrl, job.Credential, built, "refs/heads/" + head, expected: null);
            results.Add(push switch
            {
                WriterService.Push.Done or WriterService.Push.UpToDate => new BackportTargetResult(branch, head, built, Created, [], platformChanges, resolutions),
                WriterService.Push.LeaseRejected => new BackportTargetResult(branch, head, built, Exists, [], platformChanges, resolutions),
                WriterService.Push.Rejected rejected => new BackportTargetResult(branch, head, built, Rejected, [], platformChanges, resolutions, rejected.Detail),
                _ => throw new InvalidOperationException(),
            });
        }
        return new VersioningBackportResponse(JobOutcomes.Backported, null, mode, @base, results, null, context.ElapsedMilliseconds);
    }, (outcome, reason, detail, ms) => new VersioningBackportResponse(outcome, reason, null, null, [], detail, ms));

    /// <summary>The paths the change touches that can hold platform markers (#570 S2).</summary>
    private static async Task<IReadOnlyList<string>> MarkerPathsAsync(JobContext context, string @base, string commit, PlatformMarkerOptions? markers)
    {
        var diff = await context.RequireAsync(["diff", "--name-only", "-z", "--no-renames", @base, commit]);
        return diff.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries).Where(path => PlatformMarkers.Handles(path, markers)).ToList();
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
    private static async Task<(string Base, string Theirs, IReadOnlyList<PlatformChange> Changes)> NormalizeAsync(
        JobContext context, BackportJob job, string tip, string @base, string theirs, IReadOnlyList<string> paths)
    {
        var changes = new List<PlatformChange>();
        var baseFiles = new List<(string Path, string Mode, string Content)>();
        var theirsFiles = new List<(string Path, string Mode, string Content)>();
        foreach (var path in paths)
        {
            var ours = await BlobAsync(context, tip, path);
            var before = await BlobAsync(context, @base, path);
            var after = await BlobAsync(context, theirs, path);
            if (ours is { Text: null } || before is { Text: null } || after is { Text: null })
                continue;   // not UTF-8 text: left to the plain merge
            if (PlatformMarkers.Normalize(path, ours?.Text, before?.Text, after?.Text, job.Markers) is not { } normalized)
                continue;
            changes.AddRange(normalized.PlatformChanges);
            if (before is not null && normalized.Base != before.Text)
                baseFiles.Add((path, before.Mode, normalized.Base!));
            if (after is not null && normalized.Theirs != after.Text)
                theirsFiles.Add((path, after.Mode, normalized.Theirs!));
        }
        return (await RewriteAsync(context, job, @base, baseFiles), await RewriteAsync(context, job, theirs, theirsFiles), changes);
    }

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

    private static async Task<string> RewriteAsync(JobContext context, BackportJob job, string commit, List<(string Path, string Mode, string Content)> files)
    {
        if (files.Count == 0)
            return commit;
        var index = Path.Combine(context.Scratch, $"normalize-{Guid.NewGuid():N}.index");
        await context.RequireAsync(["read-tree", commit], index: index);
        var info = new StringBuilder();
        foreach (var (path, mode, content) in files)
        {
            var blob = WriterService.Oid(await context.RequireAsync(["hash-object", "-w", "--stdin"], input: content), "hash-object");
            info.Append(mode).Append(' ').Append(blob).Append('\t').Append(path).Append('\0');
        }
        await context.RequireAsync(["update-index", "-z", "--index-info"], input: info.ToString(), index: index);
        var tree = WriterService.Oid(await context.RequireAsync(["write-tree"], index: index), "write-tree");
        return WriterService.Oid(await context.RequireAsync(["commit-tree", tree, "-m", "normalized platform markers"], author: job.Committer, committer: job.Committer),
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
