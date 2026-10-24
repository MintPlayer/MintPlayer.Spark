using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CodeCoverage.Git;
using CodeCoverage.Git.MergeQueue;

namespace CodeCoverage.GitWriter.Jobs;

/// <summary>A validated <c>POST /jobs/versioning/backport</c>.</summary>
public sealed record BackportJob(
    string Repo, Uri CloneUrl, GitCredential Credential, string SourceBranch, string Commit, int PullRequest, string Title,
    IReadOnlyList<string> Subjects, GitIdentity Committer, IReadOnlyList<(string Branch, string Head)> Targets);

/// <summary>
/// A backport (#153 M10, PRD D10): the merged pull request's change applied to each release branch as one new commit,
/// pushed to a new <c>backport/…</c> branch for the app to open a pull request from. Spike S8 showed the merge queue's
/// replay does not fit (it rebases a branch onto a moving base); this is plumbing instead. Each target is a three-way
/// <c>merge-tree</c> of the change against its own parent, so a cherry-pick in memory; a conflict refuses everything,
/// because a half-applied backport across lines is worse than none, and no conflict resolver runs on old lines.
/// </summary>
/// <remarks>
/// How the pull request was merged decides what "the change" is. A merge commit's is <c>c^1..c</c>; a squash's is the
/// commit itself; a rebase merge left N commits whose subjects are the pull request's, and the change is <c>c~N..c</c>,
/// applied as one commit. The message is built here: the title and pull request, then one
/// <c>(cherry picked from commit …)</c> trailer per original commit, the same trailer <c>git cherry-pick -x</c> writes.
/// </remarks>
public sealed partial class BackportService(JobRunner jobs)
{
    public const string Merge = "merge";
    public const string Squash = "squash";
    public const string Rebase = "rebase";

    public const string Created = "created";
    public const string Exists = "exists";
    public const string Empty = "empty";
    public const string Rejected = "rejected";

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

        var trees = new List<(string Branch, string Head, string Tip, string? Tree, IReadOnlyList<string> Conflicts)>();
        foreach (var (branch, head) in job.Targets)
        {
            var tip = WriterService.Oid(await context.RequireAsync(["rev-parse", "--verify", "refs/heads/" + branch + "^{commit}"]), "rev-parse");
            var merge = await context.GitAsync(["merge-tree", "--write-tree", "-z", "--messages", "--merge-base=" + @base, tip, job.Commit], null);
            if (merge.ExitCode is not (0 or 1))
                throw new GitCommandException("merge-tree", merge);
            MergeTreeOutput output;
            try
            {
                output = MergeTreeOutput.Parse(merge.StandardOutput);
            }
            catch (FormatException e)
            {
                throw new GitCommandException("merge-tree", e.Message);
            }
            var conflicts = output.Stages.Select(s => s.Path)
                .Concat(output.Messages.Where(m => m.Type.StartsWith("CONFLICT", StringComparison.Ordinal)).SelectMany(m => m.Paths))
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
            trees.Add((branch, head, tip, merge.ExitCode == 0 && conflicts.Count == 0 ? output.Tree : null, conflicts));
        }

        // Nothing is pushed unless every target applies (D10).
        if (trees.Any(t => t.Tree is null))
            return new VersioningBackportResponse(JobOutcomes.Conflicted, null, mode, @base,
                trees.Select(t => new BackportTargetResult(t.Branch, t.Head, null, null, t.Conflicts)).ToList(), null, context.ElapsedMilliseconds);

        var message = Message(job, commit, picked);
        var author = Author(commit.Raw) ?? job.Committer;
        var results = new List<BackportTargetResult>();
        foreach (var (branch, head, tip, tree, _) in trees)
        {
            var tipTree = WriterService.Oid(await context.RequireAsync(["rev-parse", "--verify", tip + "^{tree}"]), "rev-parse");
            if (tipTree == tree)
            {
                results.Add(new BackportTargetResult(branch, head, null, Empty, []));
                continue;
            }
            var built = WriterService.Oid(await context.RequireAsync(["commit-tree", tree!, "-p", tip, "-F", "-"], input: message, author: author, committer: job.Committer),
                "commit-tree");
            var push = await WriterService.PushAsync(context, job.CloneUrl, job.Credential, built, "refs/heads/" + head, expected: null);
            results.Add(push switch
            {
                WriterService.Push.Done or WriterService.Push.UpToDate => new BackportTargetResult(branch, head, built, Created, []),
                WriterService.Push.LeaseRejected => new BackportTargetResult(branch, head, built, Exists, []),
                WriterService.Push.Rejected rejected => new BackportTargetResult(branch, head, built, Rejected, [], rejected.Detail),
                _ => throw new InvalidOperationException(),
            });
        }
        return new VersioningBackportResponse(JobOutcomes.Backported, null, mode, @base, results, null, context.ElapsedMilliseconds);
    }, (outcome, reason, detail, ms) => new VersioningBackportResponse(outcome, reason, null, null, [], detail, ms));

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
