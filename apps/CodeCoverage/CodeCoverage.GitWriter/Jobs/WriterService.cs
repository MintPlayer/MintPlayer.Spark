using System.Globalization;
using System.Text;
using CodeCoverage.Git;
using CodeCoverage.Git.MergeQueue;
using Microsoft.Extensions.Options;

namespace CodeCoverage.GitWriter.Jobs;

/// <summary>A validated prepare request.</summary>
public sealed record PrepareJob(
    string Repo, Uri CloneUrl, GitCredential Credential, string BaseBranch, string? BaseSha, string HeadRef, string HeadSha,
    string QueueBranch, string? ExpectedQueueSha, IReadOnlyList<AutoResolveRule> Rules, GitIdentity Author, GitIdentity Committer, string Message);

/// <summary>A validated publish request; <see cref="Patch"/> is empty when the regenerate step changed nothing.</summary>
public sealed record PublishJob(
    string Repo, Uri CloneUrl, Uri PushUrl, GitCredential Credential, string QueueBranch, string PreparedSha, string HeadRef,
    string PrBranch, string ExpectedPrHeadSha, byte[] Patch, IReadOnlyList<PathGlob> RegeneratedPaths, GitIdentity Committer);

/// <summary>A validated queue-branch deletion.</summary>
public sealed record DeleteQueueBranchJob(string Repo, Uri CloneUrl, GitCredential Credential, string QueueBranch, string? ExpectedSha);

/// <summary>
/// The merge queue's writes (docs/coverage_merge_queue_PRD.md §5.1 steps 1 and 3, D2, D5, D12, T3, T4, T8). Every job
/// starts from an empty directory: a bare, blob-less clone of one branch (<c>--filter=blob:none --no-tags
/// --single-branch</c>, S8), so there is no checkout, no worktree, no hook and no in-repo attribute anywhere (the shared
/// <see cref="GitRunner"/>), and objects received are fsck'd. Commits are built with <c>commit-tree</c> and published with
/// a <c>--force-with-lease</c> push, a compare-and-swap on the forge.
/// </summary>
public sealed class WriterService(GitRunner git, JobRunner jobs, IOptions<GitWriterOptions> options)
{
    private const string HeadTracking = "refs/mq/head";

    /// <summary>Where a branch's current commit is fetched to when a lease was rejected (§0 D52).</summary>
    private const string TipTracking = "refs/mq/tip";

    /// <summary>Step [1]: squash and rebase the pull request onto its base and push the result to the queue branch.</summary>
    public Task<PrepareResponse> PrepareAsync(PrepareJob job) => jobs.RunAsync("prepare", job.Repo, async context =>
    {
        PrepareResponse Answer(string outcome, string? reason = null, string? baseSha = null, string? headSha = null, string? prepared = null,
            IReadOnlyList<string>? resolved = null, IReadOnlyList<string>? paths = null, string? detail = null)
            => new(outcome, reason, baseSha, headSha, prepared, resolved ?? [], paths ?? [], detail, context.ElapsedMilliseconds);

        await CloneAsync(context, job.CloneUrl, job.Credential, job.BaseBranch, required: true);
        await FetchHeadAsync(context, job.Credential, job.HeadRef);
        context.EnsureWithinDisk();
        var baseSha = await RevParseAsync(context, $"refs/heads/{job.BaseBranch}");
        var headSha = await RevParseAsync(context, HeadTracking);
        if (job.BaseSha is { } pinnedBase && baseSha != pinnedBase)
            return Answer(JobOutcomes.BaseMoved, baseSha: baseSha, headSha: headSha, detail: $"{job.BaseBranch} is at {baseSha}, not {pinnedBase}.");
        if (headSha != job.HeadSha)
            return Answer(JobOutcomes.HeadMismatch, baseSha: baseSha, headSha: headSha, detail: $"{job.HeadRef} is at {headSha}, not the pinned {job.HeadSha} (T3).");

        // D12: a dispatched run uses the workflow files of the mq/ ref, so a pull request that changes .github/** never
        // reaches the regenerate step, whatever the conflict situation.
        var workflows = (await NetChangeAsync(context, baseSha, headSha)).Where(TouchesWorkflows).ToList();
        if (workflows.Count > 0)
            return Answer(JobOutcomes.Evicted, JobReasons.TouchesWorkflows, baseSha, headSha, paths: workflows,
                detail: "The pull request changes .github/**; the queue never runs a workflow a pull request has changed (D12).");

        var resolver = new MergeQueueResolver(git) { Timeout = context.Remaining, ScratchDirectory = context.Scratch, Config = JobContext.WriterConfig };
        var merged = await resolver.MergeAsync(context.GitDirectory, baseSha, headSha, job.Rules, context.Deadline, job.Credential);
        context.EnsureWithinDisk();
        if (merged is MergeOutcome.Evicted e)
            return Answer(JobOutcomes.Evicted, Kebab(e.Reason.ToString()), baseSha, headSha, paths: e.Paths, detail: e.Detail);
        var (tree, resolvedPaths) = merged switch
        {
            MergeOutcome.Clean clean => (clean.Tree, (IReadOnlyList<string>)[]),
            MergeOutcome.Resolved resolved => (resolved.Tree, resolved.Paths),
            _ => throw new InvalidOperationException("Unknown merge outcome."),
        };

        var commit = await resolver.CommitAsync(context.GitDirectory, tree, baseSha, job.Author, job.Committer, job.Message, context.Deadline);
        switch (await PushAsync(context, job.CloneUrl, job.Credential, commit, $"refs/heads/{job.QueueBranch}", job.ExpectedQueueSha))
        {
            case Push.Done:
                return Answer(JobOutcomes.Prepared, baseSha: baseSha, headSha: headSha, prepared: commit, resolved: resolvedPaths);
            case Push.LeaseRejected:
                // §0 D52: an earlier attempt's push may have landed without the saga learning of it.
                if (await FetchTipAsync(context, "origin", job.Credential, job.QueueBranch) is { } tip
                    && await IsOwnCommitAsync(context, tip, commit, job.Author, job.Committer))
                    return Answer(JobOutcomes.Prepared, JobReasons.AlreadyPushed, baseSha, headSha, tip, resolvedPaths,
                        detail: $"{job.QueueBranch} already holds this job's commit {tip}, pushed by an earlier attempt.");
                return Answer(JobOutcomes.QueueBranchMoved, baseSha: baseSha, headSha: headSha, detail: $"{job.QueueBranch} is not where the lease expected it.");
            case Push.Rejected rejected:
                return Answer(JobOutcomes.PushRejected, baseSha: baseSha, headSha: headSha, detail: rejected.Detail);
            default:
                throw new InvalidOperationException("Unknown push outcome.");
        }
    }, (outcome, reason, detail, ms) => new PrepareResponse(outcome, reason, null, null, null, [], [], detail, ms));

    /// <summary>Step [3]: validate the regenerate patch, apply it to the prepared tree without a worktree and publish ONE
    /// squashed commit (base + the pull request's net change + the regenerated files) to the pull request's branch.</summary>
    public Task<PublishResponse> PublishAsync(PublishJob job)
    {
        // T4: validated before anything is cloned or applied.
        var verdict = job.Patch.Length == 0
            ? PatchVerdict.Accept([])
            : PatchValidator.Validate(job.Patch, job.RegeneratedPaths, options.Value.MaxPatchBytes);
        if (!verdict.Accepted)
            return Task.FromResult(new PublishResponse(JobOutcomes.PatchRejected, verdict.Reason, null, verdict.Paths, verdict.Detail, 0));

        return jobs.RunAsync("publish", job.Repo, async context =>
        {
            PublishResponse Answer(string outcome, string? reason = null, string? final = null, IReadOnlyList<string>? paths = null, string? detail = null)
                => new(outcome, reason, final, paths ?? [], detail, context.ElapsedMilliseconds);

            if (!await CloneAsync(context, job.CloneUrl, job.Credential, job.QueueBranch, required: false))
                return Answer(JobOutcomes.QueueBranchMoved, detail: $"{job.QueueBranch} does not exist.");
            var tip = await RevParseAsync(context, $"refs/heads/{job.QueueBranch}");
            if (tip != job.PreparedSha)
                return Answer(JobOutcomes.QueueBranchMoved, detail: $"{job.QueueBranch} is at {tip}, not the prepared {job.PreparedSha}.");
            var prepared = PreparedCommit.Parse((await context.RequireAsync(["cat-file", "commit", job.PreparedSha])).StandardOutput);
            if (prepared is null)
                throw new JobAbortedException(JobReasons.Error, "The prepared commit does not have exactly one parent.");

            // The final commit, built at most once and only when needed: the prepared commit itself when nothing was
            // regenerated, else one commit with the patch applied.
            FinalCommit? memo = null;
            async Task<FinalCommit> FinalAsync() => memo ??= await BuildFinalAsync(context, job, prepared, verdict.Paths);
            string AlreadyThere(string sha) => $"{job.PrBranch} already holds this job's commit {sha}, pushed by an earlier attempt.";

            // The author's branch is checked before anything is pushed; the lease checks it again at the push. A head
            // that moved may be this job's own commit, pushed by an attempt whose answer never reached the saga (D52).
            await FetchHeadAsync(context, job.Credential, job.HeadRef);
            context.EnsureWithinDisk();
            var head = await RevParseAsync(context, HeadTracking);
            if (head != job.ExpectedPrHeadSha)
                return await FinalAsync() is { Sha: { } ours } && await IsOwnCommitAsync(context, head, ours, prepared.Author, job.Committer)
                    ? Answer(JobOutcomes.Published, JobReasons.AlreadyPushed, head, verdict.Paths, AlreadyThere(head))
                    : Answer(JobOutcomes.BranchMoved, detail: $"{job.HeadRef} is at {head}, not {job.ExpectedPrHeadSha}.");

            var built = await FinalAsync();
            if (built.Sha is not { } final)
                return Answer(JobOutcomes.PatchRejected, built.Rejected!.Reason, paths: built.Rejected.Paths, detail: built.Rejected.Detail);

            switch (await PushAsync(context, job.PushUrl, job.Credential, final, $"refs/heads/{job.PrBranch}", job.ExpectedPrHeadSha))
            {
                case Push.Done:
                    return Answer(JobOutcomes.Published, final: final, paths: verdict.Paths);
                case Push.LeaseRejected:
                    // GitHub moves refs/pull/N/head after the push, so the early check can miss an earlier attempt's commit.
                    var remote = job.PushUrl == job.CloneUrl ? "origin" : job.PushUrl.AbsoluteUri;
                    if (await FetchTipAsync(context, remote, job.Credential, job.PrBranch) is { } pushed
                        && await IsOwnCommitAsync(context, pushed, final, prepared.Author, job.Committer))
                        return Answer(JobOutcomes.Published, JobReasons.AlreadyPushed, pushed, verdict.Paths, AlreadyThere(pushed));
                    return Answer(JobOutcomes.BranchMoved, detail: $"{job.PrBranch} moved during the queue run (the lease on {job.ExpectedPrHeadSha} was rejected).");
                case Push.Rejected refused:
                    return Answer(JobOutcomes.PushRejected, detail: refused.Detail);
                default:
                    throw new InvalidOperationException("Unknown push outcome.");
            }
        }, (outcome, reason, detail, ms) => new PublishResponse(outcome, reason, null, [], detail, ms));
    }

    /// <summary>The final commit's id, or why the patch could not make one.</summary>
    private sealed record FinalCommit(string? Sha, PatchVerdict? Rejected);

    /// <summary>
    /// The final commit (D31): the prepared commit itself when there is no patch, else <c>commit-tree</c> of the prepared
    /// tree with the validated patch applied and checked again, on the prepared commit's only parent, with its author
    /// (original date) and message.
    /// </summary>
    private static async Task<FinalCommit> BuildFinalAsync(JobContext context, PublishJob job, PreparedCommit prepared, IReadOnlyList<string> declared)
    {
        // Nothing regenerated: the prepared commit already is base + the net change.
        if (job.Patch.Length == 0)
            return new FinalCommit(job.PreparedSha, null);

        var preparedTree = await RevParseAsync(context, $"{job.PreparedSha}^{{tree}}");
        var tree = await ApplyAsync(context, job, declared);
        if (tree is PatchVerdict rejected)
            return new FinalCommit(null, rejected);
        var newTree = (string)tree;
        if (await CheckTreeAsync(context, preparedTree, newTree, declared) is { } unexpected)
            return new FinalCommit(null, unexpected);
        return new FinalCommit(Oid(await context.RequireAsync(["commit-tree", newTree, "-p", prepared.Parent, "-F", "-"],
            input: prepared.Message, author: prepared.Author, committer: job.Committer), "commit-tree"), null);
    }

    /// <summary>
    /// §0 D52: whether <paramref name="candidate"/> is a commit this job could have written, i.e. what an earlier attempt
    /// pushed before its answer was lost (the app died before saving it, or the response never arrived). It must equal
    /// <paramref name="ours"/>, the commit this attempt built, in everything the job sets: the tree, the one parent (the
    /// base), the message, no other header, the author and the committer, whose dates only count where the job fixed
    /// them. Only a date git stamped itself may differ. Recognising such a commit merges nothing the queue did not build:
    /// its content is the tree this job computed (T3), and the merge is still pinned to its sha (D13).
    /// </summary>
    private static async Task<bool> IsOwnCommitAsync(JobContext context, string candidate, string ours, GitIdentity author, GitIdentity committer)
    {
        if (candidate == ours)
            return true;
        if (await ReadCommitAsync(context, candidate) is not { } theirs || await ReadCommitAsync(context, ours) is not { } mine)
            return false;
        return theirs.Tree == mine.Tree
            && theirs.Parents.SequenceEqual(mine.Parents, StringComparer.Ordinal)
            && theirs.OtherHeaders.SequenceEqual(mine.OtherHeaders, StringComparer.Ordinal)
            && string.Equals(theirs.Message, mine.Message, StringComparison.Ordinal)
            && Matches(theirs.Author, author)
            && Matches(theirs.Committer, committer);

        static bool Matches(GitIdentity? actual, GitIdentity expected)
            => actual is not null && actual.Name == expected.Name && actual.Email == expected.Email
               && (expected.When is not { } when
                   || actual.When is { } stamped && stamped.ToUnixTimeSeconds() == when.ToUnixTimeSeconds() && stamped.Offset == when.Offset);
    }

    private static async Task<CommitObject?> ReadCommitAsync(JobContext context, string sha)
    {
        var raw = await context.GitAsync(["cat-file", "commit", sha], null);
        return raw.Succeeded ? CommitObject.Parse(raw.StandardOutput) : null;
    }

    /// <summary>Where <paramref name="branch"/> of <paramref name="remote"/> is now, fetched into the job's repository; null
    /// when the branch does not exist. Any other failure throws (the saga retries the job).</summary>
    /// <remarks>
    /// ⚠️ <c>--filter=blob:none</c> even for a fork's URL, which is not the clone's promisor remote. Without it, the fork
    /// sends a pack that leans on everything reachable from the commits both sides have, blobs included, which a
    /// blob-less clone does not hold: <c>index-pack</c> then fails with "did not receive expected object" (§0 D52).
    /// </remarks>
    private static async Task<string?> FetchTipAsync(JobContext context, string remote, GitCredential credential, string branch)
    {
        var fetch = await context.GitAsync(["fetch", "--quiet", "--no-tags", "--no-write-fetch-head", "--filter=blob:none", remote, $"+refs/heads/{branch}:{TipTracking}"], credential);
        if (!fetch.Succeeded)
        {
            if (fetch.StandardError.Contains("couldn't find remote ref", StringComparison.OrdinalIgnoreCase))
                return null;
            throw new GitCommandException("fetch", fetch);
        }
        context.EnsureWithinDisk();
        return await RevParseAsync(context, TipTracking);
    }

    /// <summary>Removes the queue branch once its entry ended (D5). A branch that is already gone counts as deleted.</summary>
    public Task<DeleteQueueBranchResponse> DeleteQueueBranchAsync(DeleteQueueBranchJob job) => jobs.RunAsync("delete", job.Repo, async context =>
    {
        await context.RequireAsync(["init", "--bare", "--quiet", "--template=", context.GitDirectory], inRepository: false);
        return await PushAsync(context, job.CloneUrl, job.Credential, null, $"refs/heads/{job.QueueBranch}", job.ExpectedSha, lease: job.ExpectedSha is not null) switch
        {
            Push.Done => new DeleteQueueBranchResponse(JobOutcomes.Deleted, null, null, context.ElapsedMilliseconds),
            Push.LeaseRejected => new DeleteQueueBranchResponse(JobOutcomes.QueueBranchMoved, null, $"{job.QueueBranch} is not at {job.ExpectedSha}.", context.ElapsedMilliseconds),
            Push.Rejected rejected => new DeleteQueueBranchResponse(JobOutcomes.PushRejected, null, rejected.Detail, context.ElapsedMilliseconds),
            _ => throw new InvalidOperationException("Unknown push outcome."),
        };
    }, (outcome, reason, detail, ms) => new DeleteQueueBranchResponse(outcome, reason, detail, ms));

    /// <summary>A bare, blob-less, single-branch clone with the hardened configuration persisted into it and an empty
    /// template (no hooks, no info/). False when <paramref name="branch"/> does not exist and it was not
    /// <paramref name="required"/>.</summary>
    private async Task<bool> CloneAsync(JobContext context, Uri url, GitCredential credential, string branch, bool required)
    {
        var arguments = new List<string> { "clone", "--bare", "--quiet", "--filter=blob:none", "--no-tags", "--single-branch", "--branch", branch, "--template=" };
        foreach (var (key, value) in git.HardenedConfig())
            arguments.Add($"--config={key}={value}");
        arguments.AddRange([url.AbsoluteUri, context.GitDirectory]);
        var clone = await context.GitAsync(arguments, credential, inRepository: false);
        if (clone.Succeeded)
        {
            context.EnsureWithinDisk();
            return true;
        }
        if (!required && clone.StandardError.Contains("not found in upstream", StringComparison.OrdinalIgnoreCase))
            return false;
        throw new GitCommandException("clone", clone);
    }

    private static Task FetchHeadAsync(JobContext context, GitCredential credential, string headRef)
        => context.RequireAsync(["fetch", "--quiet", "--no-tags", "--no-write-fetch-head", "origin", $"+{headRef}:{HeadTracking}"], credential);

    private static async Task<string> RevParseAsync(JobContext context, string revision)
        => Oid(await context.RequireAsync(["rev-parse", "--verify", "--quiet", "--end-of-options", revision]), "rev-parse");

    /// <summary>The paths the pull request changes against its merge base (renames as two paths).</summary>
    private static async Task<IReadOnlyList<string>> NetChangeAsync(JobContext context, string baseSha, string headSha)
    {
        var mergeBase = Oid(await context.RequireAsync(["merge-base", baseSha, headSha]), "merge-base");
        var diff = await context.RequireAsync(["diff-tree", "-r", "--no-renames", "--name-only", "-z", mergeBase, headSha]);
        return diff.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>D12, case-insensitively: a file system that folds case would treat <c>.GitHub</c> as <c>.github</c>.</summary>
    internal static bool TouchesWorkflows(string path) => path.StartsWith(".github/", StringComparison.OrdinalIgnoreCase);

    /// <summary>The validated patch applied to the prepared tree in a throwaway index (<c>git apply --cached</c> works in
    /// a bare repository; blobs the patch needs are fetched lazily). Returns the new tree's id, or the rejection.</summary>
    private static async Task<object> ApplyAsync(JobContext context, PublishJob job, IReadOnlyList<string> paths)
    {
        var index = Path.Combine(context.Scratch, "publish.index");
        var patch = Path.Combine(context.Scratch, "regenerate.patch");
        await File.WriteAllBytesAsync(patch, job.Patch, context.Deadline);
        await context.RequireAsync(["read-tree", job.PreparedSha], index: index);
        var apply = await context.GitAsync(["apply", "--cached", "--whitespace=nowarn", "--", patch], job.Credential, index: index);
        if (!apply.Succeeded)
            return PatchVerdict.Reject(PatchReasons.ApplyFailed, apply.StandardError.Trim(), [.. paths]);
        // --missing-ok: the prepared tree names blobs the blob-less clone never fetched; apply fetched the ones it needed.
        return Oid(await context.RequireAsync(["write-tree", "--missing-ok"], index: index), "write-tree");
    }

    /// <summary>Defence in depth after the apply: the trees differ only in declared paths, and every declared path that
    /// still exists is a regular, non-executable file.</summary>
    private static async Task<PatchVerdict?> CheckTreeAsync(JobContext context, string before, string after, IReadOnlyList<string> declared)
    {
        var changed = (await context.RequireAsync(["diff-tree", "-r", "--no-renames", "--name-only", "-z", before, after])).StandardOutput
            .Split('\0', StringSplitOptions.RemoveEmptyEntries);
        var undeclared = changed.Where(p => !declared.Contains(p, StringComparer.Ordinal)).ToArray();
        if (undeclared.Length > 0)
            return PatchVerdict.Reject(PatchReasons.UnexpectedChange, "The applied tree changed paths the patch did not declare.", undeclared);
        if (changed.Length == 0)
            return null;
        var listing = (await context.RequireAsync(["ls-tree", "-z", after, "--", .. changed])).StandardOutput;
        foreach (var entry in listing.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var mode = entry[..entry.IndexOf(' ')];
            if (mode != "100644")
                return PatchVerdict.Reject(PatchReasons.UnsupportedMode, $"mode {mode}", entry[(entry.IndexOf('\t') + 1)..]);
        }
        return null;
    }

    private abstract record Push
    {
        public sealed record Done : Push;
        public sealed record LeaseRejected : Push;
        public sealed record Rejected(string Detail) : Push;
    }

    /// <summary>A push of <paramref name="source"/> (null deletes) to <paramref name="remoteRef"/>, by default with a lease:
    /// the ref must still be at <paramref name="expected"/>, or not exist when that is null. <c>--porcelain</c> tells a
    /// rejected lease ("stale info") from a forge's refusal; anything else (auth, network) throws.</summary>
    private static async Task<Push> PushAsync(JobContext context, Uri url, GitCredential credential, string? source, string remoteRef, string? expected, bool lease = true)
    {
        var arguments = new List<string> { "push", "--porcelain", "--no-verify", "--no-recurse-submodules" };
        if (lease)
            arguments.Add($"--force-with-lease={remoteRef}:{expected ?? ""}");
        arguments.AddRange([url.AbsoluteUri, $"{source ?? ""}:{remoteRef}"]);
        var result = await context.GitAsync(arguments, credential);
        var refused = result.StandardOutput.Split('\n').FirstOrDefault(l => l.StartsWith('!') && l.Contains(remoteRef, StringComparison.Ordinal));
        if (refused is not null)
            return refused.Contains("(stale info)", StringComparison.Ordinal) ? new Push.LeaseRejected() : new Push.Rejected($"{refused.Trim()} {result.StandardError.Trim()}".Trim());
        if (result.Succeeded)
            return new Push.Done();
        if (source is null && result.StandardError.Contains("remote ref does not exist", StringComparison.OrdinalIgnoreCase))
            return new Push.Done();
        throw new GitCommandException("push", result);
    }

    private static string Oid(GitResult result, string verb)
    {
        var oid = result.StandardOutput.Trim();
        return WireValidation.ObjectId(oid) ?? throw new GitCommandException(verb, "did not print an object id.");
    }

    /// <summary><c>ConflictOutsideAllowList</c> → <c>conflict-outside-allow-list</c>.</summary>
    internal static string Kebab(string name)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < name.Length; i++)
        {
            if (char.IsUpper(name[i]) && i > 0)
                builder.Append('-');
            builder.Append(char.ToLowerInvariant(name[i]));
        }
        return builder.ToString();
    }
}

/// <summary>The prepared commit as <c>cat-file commit</c> prints it: its single parent (the base), its author with the
/// original date and offset, and its message, so the final commit preserves all three.</summary>
internal sealed record PreparedCommit(string Parent, GitIdentity Author, string Message)
{
    public static PreparedCommit? Parse(string raw)
        => CommitObject.Parse(raw) is { Parents: [var parent], Author: { } author } commit && WireValidation.ObjectId(parent) is not null
            ? new PreparedCommit(parent, author, commit.Message)
            : null;
}

/// <summary>
/// A commit object as <c>cat-file commit</c> prints it: its headers (an identity that does not parse is null; every header
/// other than tree, parent, author and committer is kept verbatim, continuation lines included) and its message.
/// </summary>
internal sealed record CommitObject(string Tree, IReadOnlyList<string> Parents, GitIdentity? Author, GitIdentity? Committer, IReadOnlyList<string> OtherHeaders, string Message)
{
    public static CommitObject? Parse(string raw)
    {
        var split = raw.IndexOf("\n\n", StringComparison.Ordinal);
        if (split < 0)
            return null;
        string? tree = null;
        GitIdentity? author = null, committer = null;
        List<string> parents = [], other = [];
        foreach (var header in raw[..split].Split('\n'))
        {
            if (header.StartsWith("tree ", StringComparison.Ordinal))
                tree = header["tree ".Length..];
            else if (header.StartsWith("parent ", StringComparison.Ordinal))
                parents.Add(header["parent ".Length..]);
            else if (header.StartsWith("author ", StringComparison.Ordinal))
                author = Identity(header["author ".Length..]);
            else if (header.StartsWith("committer ", StringComparison.Ordinal))
                committer = Identity(header["committer ".Length..]);
            else
                other.Add(header);
        }
        return WireValidation.ObjectId(tree) is { } id ? new CommitObject(id, parents, author, committer, other, raw[(split + 2)..]) : null;
    }

    /// <summary><c>Name &lt;email&gt; 1767225600 +0200</c>.</summary>
    private static GitIdentity? Identity(string value)
    {
        var open = value.LastIndexOf('<');
        var close = value.LastIndexOf('>');
        if (open <= 0 || close < open)
            return null;
        var stamp = value[(close + 1)..].Trim().Split(' ');
        if (stamp.Length != 2 || !long.TryParse(stamp[0], NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            || stamp[1].Length != 5 || stamp[1][0] is not ('+' or '-')
            || !int.TryParse(stamp[1].AsSpan(1, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var hours)
            || !int.TryParse(stamp[1].AsSpan(3, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var minutes))
            return null;
        var offset = new TimeSpan(hours, minutes, 0) * (stamp[1][0] == '-' ? -1 : 1);
        try
        {
            return new GitIdentity(value[..open].Trim(), value[(open + 1)..close], DateTimeOffset.FromUnixTimeSeconds(seconds).ToOffset(offset));
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
