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
        return await PushAsync(context, job.CloneUrl, job.Credential, commit, $"refs/heads/{job.QueueBranch}", job.ExpectedQueueSha) switch
        {
            Push.Done => Answer(JobOutcomes.Prepared, baseSha: baseSha, headSha: headSha, prepared: commit, resolved: resolvedPaths),
            Push.LeaseRejected => Answer(JobOutcomes.QueueBranchMoved, baseSha: baseSha, headSha: headSha, detail: $"{job.QueueBranch} is not where the lease expected it."),
            Push.Rejected rejected => Answer(JobOutcomes.PushRejected, baseSha: baseSha, headSha: headSha, detail: rejected.Detail),
            _ => throw new InvalidOperationException("Unknown push outcome."),
        };
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

            // The author's branch is checked before anything is built; the lease checks it again at the push.
            await FetchHeadAsync(context, job.Credential, job.HeadRef);
            context.EnsureWithinDisk();
            var head = await RevParseAsync(context, HeadTracking);
            if (head != job.ExpectedPrHeadSha)
                return Answer(JobOutcomes.BranchMoved, detail: $"{job.HeadRef} is at {head}, not {job.ExpectedPrHeadSha}.");

            string final;
            if (job.Patch.Length == 0)
            {
                // Nothing regenerated: the prepared commit already is base + the net change.
                final = job.PreparedSha;
            }
            else
            {
                var preparedTree = await RevParseAsync(context, $"{job.PreparedSha}^{{tree}}");
                var tree = await ApplyAsync(context, job, verdict.Paths);
                if (tree is PatchVerdict rejected)
                    return Answer(JobOutcomes.PatchRejected, rejected.Reason, paths: rejected.Paths, detail: rejected.Detail);
                var newTree = (string)tree;
                if (await CheckTreeAsync(context, preparedTree, newTree, verdict.Paths) is { } unexpected)
                    return Answer(JobOutcomes.PatchRejected, unexpected.Reason, paths: unexpected.Paths, detail: unexpected.Detail);
                final = Oid(await context.RequireAsync(["commit-tree", newTree, "-p", prepared.Parent, "-F", "-"],
                    input: prepared.Message, author: prepared.Author, committer: job.Committer), "commit-tree");
            }

            return await PushAsync(context, job.PushUrl, job.Credential, final, $"refs/heads/{job.PrBranch}", job.ExpectedPrHeadSha) switch
            {
                Push.Done => Answer(JobOutcomes.Published, final: final, paths: verdict.Paths),
                Push.LeaseRejected => Answer(JobOutcomes.BranchMoved, detail: $"{job.PrBranch} moved during the queue run (the lease on {job.ExpectedPrHeadSha} was rejected)."),
                Push.Rejected rejected => Answer(JobOutcomes.PushRejected, detail: rejected.Detail),
                _ => throw new InvalidOperationException("Unknown push outcome."),
            };
        }, (outcome, reason, detail, ms) => new PublishResponse(outcome, reason, null, [], detail, ms));
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
    {
        var split = raw.IndexOf("\n\n", StringComparison.Ordinal);
        if (split < 0)
            return null;
        var headers = raw[..split].Split('\n');
        var parents = headers.Where(h => h.StartsWith("parent ", StringComparison.Ordinal)).Select(h => h["parent ".Length..]).ToList();
        var author = headers.FirstOrDefault(h => h.StartsWith("author ", StringComparison.Ordinal));
        if (parents.Count != 1 || WireValidation.ObjectId(parents[0]) is null || author is null || Identity(author["author ".Length..]) is not { } identity)
            return null;
        return new PreparedCommit(parents[0], identity, raw[(split + 2)..]);
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
