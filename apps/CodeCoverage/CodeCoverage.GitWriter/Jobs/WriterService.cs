using System.Text;
using CodeCoverage.Git;
using CodeCoverage.Git.MergeQueue;
using CodeCoverage.Git.MergeQueue.Resolution;
using Microsoft.Extensions.Options;

namespace CodeCoverage.GitWriter.Jobs;

/// <summary>
/// The validated fields every job that builds the prepared commit carries (§0 D65). The commit is a function of them alone:
/// the author's and the committer's dates are the saga's, so the queue-branch push and the publish rebuild it bit for bit.
/// </summary>
/// <param name="BaseSha">Prepare: when set, the base branch must still be at it. The queue-branch push and the publish:
/// always set, the commit the prepared one is built on.</param>
public sealed record PrepareJob(
    string Repo, Uri CloneUrl, GitCredential Credential, string BaseBranch, string? BaseSha, string HeadRef, string HeadSha,
    IReadOnlyList<AutoResolveRule> Rules, GitIdentity Author, GitIdentity Committer, string Message);

/// <summary>A validated queue-branch push: the prepared commit, rebuilt, onto <paramref name="QueueBranch"/>.</summary>
/// <param name="HeadFromFork">The pull request's head lives in a fork: a commit whose <c>.github</c> differs from its base's
/// is refused (O25, §0 D67).</param>
public sealed record QueueBranchJob(PrepareJob Prepare, string PreparedSha, string QueueBranch, string? ExpectedQueueSha, bool HeadFromFork);

/// <summary>A validated publish request; <see cref="Patch"/> is empty when the regenerate step changed nothing.</summary>
public sealed record PublishJob(PrepareJob Prepare, Uri PushUrl, string PreparedSha, string PrBranch, byte[] Patch, IReadOnlyList<PathGlob> RegeneratedPaths)
{
    /// <summary>O36: one patch per regenerate workflow run, combined by <see cref="PatchCombiner"/> into <see cref="Patch"/>
    /// before anything else; null for the single patch of an older app.</summary>
    public IReadOnlyList<byte[]>? Patches { get; init; }
}

/// <summary>A validated queue-branch deletion.</summary>
public sealed record DeleteQueueBranchJob(string Repo, Uri CloneUrl, GitCredential Credential, string QueueBranch, string? ExpectedSha);

/// <summary>
/// The merge queue's writes (docs/coverage_merge_queue_PRD.md §5.1 steps 1 and 3, D2, D5, D12, T3, T4, T8). Every job
/// starts from an empty directory: a bare, blob-less clone of one branch (<c>--filter=blob:none --no-tags
/// --single-branch</c>, S8), so there is no checkout, no worktree, no hook and no in-repo attribute anywhere (the shared
/// <see cref="GitRunner"/>), and objects received are fsck'd. Commits are built with <c>commit-tree</c> and published with
/// a <c>--force-with-lease</c> push, a compare-and-swap on the forge.
/// </summary>
/// <remarks>
/// <para>
/// <b>Only a dispatch writes to the queue branch (§0 D65).</b> A push to a branch of the base repository starts that
/// repository's <c>push</c> workflows on the pushed code, with its secrets (D14, D19), and for a fork's pull request that
/// code is the fork's. So the prepare pushes nothing: it builds the commit from its request alone and answers its id. The
/// queue-branch push (only before a dispatch) and the publish rebuild the same commit from the same fields, check that it
/// is the one the saga holds, and only then push it.
/// </para>
/// <para>
/// <b>A fork's <c>.github</c> never reaches the base repository (O25, §0 D67).</b> The prepare and the publish report a
/// change under <c>.github/</c> and refuse none: the prepare pushes nothing, and the publish pushes to the pull request's
/// own branch (a fork's to the fork), whose CI runs it as it would without the queue. The queue-branch push is the one
/// write of a pull request's code to a branch of the base repository, and it refuses a fork's commit whose <c>.github</c>
/// differs from the base's itself (<c>fork-touches-workflows</c>), behind the saga's own rule.
/// </para>
/// </remarks>
public sealed class WriterService(GitRunner git, JobRunner jobs, IOptions<GitWriterOptions> options, ConflictResolverRegistry registry)
{
    private const string HeadTracking = "refs/mq/head";

    /// <summary>Step [1]: squash and rebase the pull request onto its base. Answers the commit; pushes nothing (§0 D65).</summary>
    public Task<PrepareResponse> PrepareAsync(PrepareJob job) => jobs.RunAsync("prepare", job.Repo, async context =>
    {
        await OpenAsync(context, job);
        return await BuildAsync(context, job, baseMustBeTip: true) switch
        {
            Build.Built built => new PrepareResponse(JobOutcomes.Prepared, null, built.BaseSha, built.HeadSha, built.Commit, built.Resolved, built.Workflows,
                null, context.ElapsedMilliseconds)
            {
                TouchesWorkflows = built.Workflows.Count > 0,
                BaseChangedWorkflows = built.BaseChangedWorkflows,
                Resolutions = built.Resolutions,
                ResolverVersion = await ResolverVersionAsync(),
            },
            Build.Stopped stopped => new PrepareResponse(stopped.Outcome, stopped.Reason, stopped.BaseSha, stopped.HeadSha, null, [], stopped.Paths,
                stopped.Detail, context.ElapsedMilliseconds),
            _ => throw new InvalidOperationException("Unknown build outcome."),
        };
    }, (outcome, reason, detail, ms) => new PrepareResponse(outcome, reason, null, null, null, [], [], detail, ms));

    /// <summary>
    /// §0 D65: the prepared commit, rebuilt and checked, onto the queue branch under a lease. The saga asks for it only
    /// right before it dispatches the regenerate workflow on that branch.
    /// </summary>
    public Task<QueueBranchPushResponse> PushQueueBranchAsync(QueueBranchJob job) => jobs.RunAsync("queue-branch", job.Prepare.Repo, async context =>
    {
        QueueBranchPushResponse Answer(string outcome, string? reason = null, string? sha = null, IReadOnlyList<string>? paths = null, string? detail = null)
            => new(outcome, reason, sha, paths ?? [], detail, context.ElapsedMilliseconds);

        var prepare = job.Prepare;
        await OpenAsync(context, prepare);
        var build = await BuildAsync(context, prepare, baseMustBeTip: false);
        if (build is Build.Stopped stopped)
            return Answer(stopped.Outcome, stopped.Reason, paths: stopped.Paths, detail: stopped.Detail);
        var built = (Build.Built)build;
        // O25, §0 D67: whatever else the request says, a fork's .github never goes to a branch of the base repository, where
        // a push runs the pushed commit's workflow files with the repository's secrets. The saga never asks for it.
        if (job.HeadFromFork && built.Workflows.Count > 0)
            // The detail reaches the eviction message, so it cites no PRD id (§0 D72).
            return Answer(JobOutcomes.ForkTouchesWorkflows, paths: built.Workflows,
                detail: $"The pull request's head is a fork's, and the prepared commit changes .github/ against its base: pushing it to {job.QueueBranch}, a "
                        + "branch of the base repository, would run the fork's workflow files with that repository's secrets. Nothing was pushed.");
        var commit = built.Commit;
        if (commit != job.PreparedSha)
            return Answer(JobOutcomes.PreparedDiffers, detail: Differs(commit, job.PreparedSha)) with { ResolverVersion = await ResolverVersionAsync() };

        switch (await PushAsync(context, prepare.CloneUrl, prepare.Credential, commit, $"refs/heads/{job.QueueBranch}", job.ExpectedQueueSha))
        {
            case Push.Done:
                return Answer(JobOutcomes.Pushed, sha: commit);
            case Push.UpToDate:
                // §0 D52, D65: the branch already holds this very commit, so git sent nothing and had no lease to check.
                return Answer(JobOutcomes.Pushed, JobReasons.AlreadyPushed, commit, detail: $"{job.QueueBranch} already holds this job's commit {commit}, pushed by an earlier attempt.");
            case Push.LeaseRejected:
                // §0 D52: an earlier attempt's push may have landed without the saga learning of it. The commit is a function
                // of the request (§0 D65), so that attempt pushed exactly this one: the branch holding it is the answer.
                if (await RemoteTipAsync(context, "origin", prepare.Credential, job.QueueBranch) == commit)
                    return Answer(JobOutcomes.Pushed, JobReasons.AlreadyPushed, commit, detail: $"{job.QueueBranch} already holds this job's commit {commit}, pushed by an earlier attempt.");
                return Answer(JobOutcomes.QueueBranchMoved, detail: $"{job.QueueBranch} is not where the lease expected it.");
            case Push.Rejected rejected:
                return Answer(JobOutcomes.PushRejected, detail: rejected.Detail);
            default:
                throw new InvalidOperationException("Unknown push outcome.");
        }
    }, (outcome, reason, detail, ms) => new QueueBranchPushResponse(outcome, reason, null, [], detail, ms));

    /// <summary>Step [3]: validate the regenerate patch, rebuild the prepared commit, apply the patch to its tree without a
    /// worktree and publish ONE squashed commit (base + the pull request's net change + the regenerated files) to the pull
    /// request's branch.</summary>
    public Task<PublishResponse> PublishAsync(PublishJob job)
    {
        // O36: the patches of several regenerate workflows become one; two that change a path differently are refused.
        if (job.Patches is { } patches)
        {
            var (combined, combinedVerdict) = PatchCombiner.Combine(patches, job.RegeneratedPaths, options.Value.MaxPatchBytes);
            if (combined is null)
                return Task.FromResult(new PublishResponse(JobOutcomes.PatchRejected, combinedVerdict.Reason, null, combinedVerdict.Paths, combinedVerdict.Detail, 0));
            job = job with { Patch = combined, Patches = null };
        }

        // T4: validated before anything is cloned or applied.
        var verdict = job.Patch.Length == 0
            ? PatchVerdict.Accept([])
            : PatchValidator.Validate(job.Patch, job.RegeneratedPaths, options.Value.MaxPatchBytes);
        if (!verdict.Accepted)
            return Task.FromResult(new PublishResponse(JobOutcomes.PatchRejected, verdict.Reason, null, verdict.Paths, verdict.Detail, 0));

        var prepare = job.Prepare;
        return jobs.RunAsync("publish", prepare.Repo, async context =>
        {
            PublishResponse Answer(string outcome, string? reason = null, string? final = null, IReadOnlyList<string>? paths = null, string? detail = null)
                => new(outcome, reason, final, paths ?? [], detail, context.ElapsedMilliseconds);
            string AlreadyThere(string sha) => $"{job.PrBranch} already holds this job's commit {sha}, pushed by an earlier attempt.";

            await OpenAsync(context, prepare);
            // The author's branch is checked before anything is built; the lease checks it again at the push. A head that
            // moved may be this job's own commit, pushed by an attempt whose answer never reached the saga (D52).
            var head = await RevParseAsync(context, HeadTracking);
            if (head != prepare.HeadSha)
                return await IsOwnFinalAsync(context, job, head, verdict.Paths)
                    ? Answer(JobOutcomes.Published, JobReasons.AlreadyPushed, head, verdict.Paths, AlreadyThere(head))
                    : Answer(JobOutcomes.BranchMoved, detail: $"{prepare.HeadRef} is at {head}, not {prepare.HeadSha}.");

            // §0 D65: the prepared commit is rebuilt from the request, never read from the queue branch, which exists only
            // when a dispatch needed it.
            var build = await BuildAsync(context, prepare, baseMustBeTip: false);
            if (build is Build.Stopped stopped)
                return Answer(stopped.Outcome, stopped.Reason, paths: stopped.Paths, detail: stopped.Detail);
            var prepared = ((Build.Built)build).Commit;
            if (prepared != job.PreparedSha)
                return Answer(JobOutcomes.PreparedDiffers, detail: Differs(prepared, job.PreparedSha)) with { ResolverVersion = await ResolverVersionAsync() };

            var built = await FinalAsync(context, job, prepared, verdict.Paths);
            if (built.Sha is not { } final)
                return Answer(JobOutcomes.PatchRejected, built.Rejected!.Reason, paths: built.Rejected.Paths, detail: built.Rejected.Detail);

            switch (await PushAsync(context, job.PushUrl, prepare.Credential, final, $"refs/heads/{job.PrBranch}", prepare.HeadSha))
            {
                case Push.Done:
                    return Answer(JobOutcomes.Published, final: final, paths: verdict.Paths);
                case Push.UpToDate:
                    // The branch already holds this very commit (GitHub has not moved refs/pull/N/head yet): git sent
                    // nothing and had no lease to check.
                    return Answer(JobOutcomes.Published, JobReasons.AlreadyPushed, final, verdict.Paths, AlreadyThere(final));
                case Push.LeaseRejected:
                    // GitHub moves refs/pull/N/head after the push, so the early check can miss an earlier attempt's commit.
                    // The commit is a function of the request (§0 D65): that attempt pushed exactly this one.
                    var remote = job.PushUrl == prepare.CloneUrl ? "origin" : job.PushUrl.AbsoluteUri;
                    if (await RemoteTipAsync(context, remote, prepare.Credential, job.PrBranch) == final)
                        return Answer(JobOutcomes.Published, JobReasons.AlreadyPushed, final, verdict.Paths, AlreadyThere(final));
                    return Answer(JobOutcomes.BranchMoved, detail: $"{job.PrBranch} moved during the queue run (the lease on {prepare.HeadSha} was rejected).");
                case Push.Rejected refused:
                    return Answer(JobOutcomes.PushRejected, detail: refused.Detail);
                default:
                    throw new InvalidOperationException("Unknown push outcome.");
            }
        }, (outcome, reason, detail, ms) => new PublishResponse(outcome, reason, null, [], detail, ms));
    }

    /// <summary>Removes the queue branch once its entry ended (D5). A branch that is already gone counts as deleted.</summary>
    public Task<DeleteQueueBranchResponse> DeleteQueueBranchAsync(DeleteQueueBranchJob job) => jobs.RunAsync("delete", job.Repo, async context =>
    {
        await context.RequireAsync(["init", "--bare", "--quiet", "--template=", context.GitDirectory], inRepository: false);
        return await PushAsync(context, job.CloneUrl, job.Credential, null, $"refs/heads/{job.QueueBranch}", job.ExpectedSha, lease: job.ExpectedSha is not null) switch
        {
            Push.Done or Push.UpToDate => new DeleteQueueBranchResponse(JobOutcomes.Deleted, null, null, context.ElapsedMilliseconds),
            Push.LeaseRejected => new DeleteQueueBranchResponse(JobOutcomes.QueueBranchMoved, null, $"{job.QueueBranch} is not at {job.ExpectedSha}.", context.ElapsedMilliseconds),
            Push.Rejected rejected => new DeleteQueueBranchResponse(JobOutcomes.PushRejected, null, rejected.Detail, context.ElapsedMilliseconds),
            _ => throw new InvalidOperationException("Unknown push outcome."),
        };
    }, (outcome, reason, detail, ms) => new DeleteQueueBranchResponse(outcome, reason, detail, ms));

    // ═══ The prepared commit ═════════════════════════════════════════════════════════════════════════

    /// <summary>What <see cref="BuildAsync"/> made of a request.</summary>
    private abstract record Build
    {
        /// <summary>The prepared commit, with what the saga learns about it.</summary>
        public sealed record Built(string BaseSha, string HeadSha, string Commit, IReadOnlyList<string> Resolved, IReadOnlyList<string> Workflows,
            bool BaseChangedWorkflows) : Build
        {
            /// <summary>How each resolved path was resolved (§10.6).</summary>
            public IReadOnlyList<ResolutionWire> Resolutions { get; init; } = [];
        }

        /// <summary>No commit, and the outcome the job answers instead (<c>base-moved</c>, <c>head-mismatch</c>, <c>evicted</c>).</summary>
        public sealed record Stopped(string Outcome, string? Reason, string? BaseSha, string? HeadSha, IReadOnlyList<string> Paths, string? Detail) : Build;
    }

    /// <summary>The job's repository: the base branch, blob-less, and the pull request's head at <see cref="HeadTracking"/>.</summary>
    private async Task OpenAsync(JobContext context, PrepareJob job)
    {
        await CloneAsync(context, job.CloneUrl, job.Credential, job.BaseBranch);
        await FetchHeadAsync(context, job.Credential, job.HeadRef);
        context.EnsureWithinDisk();
    }

    /// <summary>
    /// The squashed, rebased commit (§5.1 step 1): <c>merge-tree</c> of the base and the pinned head through the resolver,
    /// then <c>commit-tree</c> on the base with the request's author, committer (both dated) and message. Deterministic
    /// for its request (§0 D65): merge-tree is, and nothing else varies.
    /// </summary>
    /// <param name="baseMustBeTip">
    /// The prepare builds on the base branch's tip (O20: the latest base) and answers <c>base-moved</c> when that is not the
    /// pinned <see cref="PrepareJob.BaseSha"/>. The queue-branch push and the publish rebuild the commit the prepare
    /// made, on its base, wherever the branch is now.
    /// </param>
    private async Task<Build> BuildAsync(JobContext context, PrepareJob job, bool baseMustBeTip)
    {
        var tip = await RevParseAsync(context, $"refs/heads/{job.BaseBranch}");
        var headSha = await RevParseAsync(context, HeadTracking);
        string baseSha;
        if (baseMustBeTip)
        {
            if (job.BaseSha is { } pinnedBase && tip != pinnedBase)
                return new Build.Stopped(JobOutcomes.BaseMoved, null, tip, headSha, [], $"{job.BaseBranch} is at {tip}, not {pinnedBase}.");
            baseSha = tip;
        }
        else
        {
            // Usually an ancestor of the tip, and in the clone already; otherwise the promisor remote is asked for it.
            var pinned = await context.GitAsync(["rev-parse", "--verify", "--quiet", "--end-of-options", $"{job.BaseSha}^{{commit}}"], job.Credential);
            if (!pinned.Succeeded)
                return new Build.Stopped(JobOutcomes.BaseMoved, null, tip, headSha, [], $"{job.BaseSha}, the prepared commit's base, is not a commit of this repository any more.");
            baseSha = job.BaseSha!;
        }
        if (headSha != job.HeadSha)
            // T3: the detail reaches the eviction message, so it explains itself rather than citing the PRD (§0 D72).
            return new Build.Stopped(JobOutcomes.HeadMismatch, null, baseSha, headSha, [], $"{job.HeadRef} is at {headSha}, not the pinned {job.HeadSha}.");

        var resolver = new MergeQueueResolver(git, registry) { Timeout = context.Remaining, ScratchDirectory = context.Scratch, Config = JobContext.WriterConfig };
        var merged = await resolver.MergeAsync(context.GitDirectory, baseSha, headSha, job.Rules, context.Deadline, job.Credential);
        context.EnsureWithinDisk();
        if (merged is MergeOutcome.Evicted e)
            return new Build.Stopped(JobOutcomes.Evicted, Kebab(e.Reason.ToString()), baseSha, headSha, e.Paths, e.Detail);
        var (tree, resolvedPaths) = merged switch
        {
            MergeOutcome.Clean clean => (clean.Tree, (IReadOnlyList<string>)[]),
            MergeOutcome.Resolved resolved => (resolved.Tree, resolved.Paths),
            _ => throw new InvalidOperationException("Unknown merge outcome."),
        };

        // D12, §0 D63, D65: what the commit changes under .github/ against its base, read from the merged tree. The pull
        // request's net change does not tell it whole: a base that renamed a file into .github/ after the pull request
        // branched carries the pull request's edits of that file there, though the net change names no .github/ path
        // (PrepareTests.A_change_a_base_rename_carries_into_github_touches_the_workflows). Reported, never refused here
        // (O25): the saga decides, and the queue-branch push refuses a fork's itself (§0 D67).
        var workflows = await GithubChangesAsync(context, baseSha, tree);

        // §0 D59, D67: what the base changed under .github/workflows since the head, which a fork's branch (holding the head)
        // lacks. The pull request's own change is `workflows`. Since D69 no token permission helps there (GitHub refuses an
        // App's write of a workflow file the fork has on no branch, whatever it asks for); the saga names it when GitHub
        // refuses such a publish (D71).
        var baseChangedWorkflows = await WorkflowsTreeAsync(context, tree) != await WorkflowsTreeAsync(context, headSha);

        var commit = await resolver.CommitAsync(context.GitDirectory, tree, baseSha, job.Author, job.Committer, job.Message, context.Deadline);
        return new Build.Built(baseSha, headSha, commit, resolvedPaths, workflows, baseChangedWorkflows)
        {
            Resolutions = merged is MergeOutcome.Resolved r ? [.. r.Resolutions.Select(x => new ResolutionWire(x.Path, x.Strategy, x.Regenerate, x.Rule))] : [],
        };
    }

    private Task<string>? resolverVersion;

    /// <summary>
    /// The resolver's version and this writer's <c>git --version</c> (merge-queue PRD O35 #4): both decide the prepared
    /// tree, so a job that rebuilds another commit than the prepare under another version is a writer change, not the pull
    /// request's, and the saga restarts the entry. Read once per process.
    /// </summary>
    internal Task<string> ResolverVersionAsync() => resolverVersion ??= ReadVersionAsync();

    private async Task<string> ReadVersionAsync()
    {
        var result = await git.RunAsync(new GitInvocation { Arguments = ["--version"], Config = JobContext.WriterConfig, Timeout = TimeSpan.FromSeconds(30) }, CancellationToken.None);
        var version = result.Succeeded ? result.StandardOutput.Trim() : "git (unknown)";
        return $"{MergeQueueResolver.ResolverVersion}; {version}";
    }

    private static string Differs(string rebuilt, string expected)
        => $"The prepared commit rebuilt from this request is {rebuilt}, not {expected}: the request, the repository or the writer's git changed since the prepare.";

    /// <summary>The <c>.github</c> paths (case-insensitively, the path itself included) where <paramref name="tree"/>
    /// differs from <paramref name="baseSha"/>'s tree. Trees are in the blob-less clone, so this fetches nothing.</summary>
    private static async Task<IReadOnlyList<string>> GithubChangesAsync(JobContext context, string baseSha, string tree)
    {
        var diff = await context.RequireAsync(["diff-tree", "-r", "--no-renames", "--name-only", "-z", baseSha, tree]);
        return [.. diff.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries).Where(TouchesWorkflows)];
    }

    /// <summary>D12, case-insensitively: a file system that folds case would treat <c>.GitHub</c> as <c>.github</c>. The
    /// entry itself counts too: a <c>.github</c> file or symlink replacing the folder is a change of it.</summary>
    internal static bool TouchesWorkflows(string path)
        => path.StartsWith(".github/", StringComparison.OrdinalIgnoreCase) || path.Equals(".github", StringComparison.OrdinalIgnoreCase);

    /// <summary>The tree id of <c>.github/workflows</c> at <paramref name="commit"/>, or empty when it has none (§0 D59).
    /// Trees are in the blob-less clone, so this fetches nothing.</summary>
    private static async Task<string> WorkflowsTreeAsync(JobContext context, string commit)
    {
        var tree = await context.GitAsync(["rev-parse", "--verify", "--quiet", "--end-of-options", $"{commit}:.github/workflows"], null);
        return tree.Succeeded ? tree.StandardOutput.Trim() : string.Empty;
    }

    // ═══ The final commit ═══════════════════════════════════════════════════════════════════════════

    /// <summary>The final commit's id, or why the patch could not make one.</summary>
    private sealed record FinalCommit(string? Sha, PatchVerdict? Rejected);

    /// <summary>
    /// The final commit (D31): the prepared commit itself when there is no patch, else <c>commit-tree</c> of the prepared
    /// tree with the validated patch applied and checked again, on the base, with the request's author, committer and
    /// message (§0 D65: dated, so a retry builds the same commit).
    /// </summary>
    private async Task<FinalCommit> FinalAsync(JobContext context, PublishJob job, string prepared, IReadOnlyList<string> declared)
    {
        // Nothing regenerated: the prepared commit already is base + the net change.
        if (job.Patch.Length == 0)
            return new FinalCommit(prepared, null);

        var preparedTree = await RevParseAsync(context, $"{prepared}^{{tree}}");
        var tree = await ApplyAsync(context, job, preparedTree, declared, reverse: false);
        if (tree is PatchVerdict rejected)
            return new FinalCommit(null, rejected);
        var newTree = (string)tree;
        if (await CheckTreeAsync(context, preparedTree, newTree, declared) is { } unexpected)
            return new FinalCommit(null, unexpected);
        if (await ValidatePatchedAsync(context, job.Prepare, registry, preparedTree, newTree, declared) is { } invalid)
            return new FinalCommit(null, invalid);
        return new FinalCommit(await CommitTreeAsync(context, job.Prepare, newTree), null);
    }

    /// <summary>
    /// Post-apply validation (merge-queue PRD §10.4): every path the patch changed whose <c>autoResolve</c> rule names
    /// validators must pass them after the apply, inputs first: a validator the prepared version already failed does not
    /// judge the regenerated one. A failure is <c>patch-rejected</c> with <c>invalid-content</c>, naming the validator and
    /// the position, never the content. A deleted path is not judged.
    /// </summary>
    private static async Task<PatchVerdict?> ValidatePatchedAsync(JobContext context, PrepareJob job, ConflictResolverRegistry registry,
        string before, string after, IReadOnlyList<string> declared)
    {
        foreach (var path in declared)
        {
            if (job.Rules.FirstOrDefault(r => r.Matches(path)) is not { Validate.Count: > 0 } rule || rule.Strategy == MergeQueueWireRules.Evict)
                continue;
            if (await ReadTextAsync(context, job.Credential, $"{after}:{path}") is not { } patched)
                continue;
            var prepared = await ReadTextAsync(context, job.Credential, $"{before}:{path}");
            foreach (var validator in rule.Validate.Distinct(StringComparer.Ordinal).Select(registry.Validator))
            {
                if (prepared is not null && !validator.Validate(prepared).Valid)
                    continue;
                var verdict = validator.Validate(patched);
                if (!verdict.Valid)
                    return PatchVerdict.Reject(PatchReasons.InvalidContent, $"After the patch, the file fails {validator.Name} at {verdict.Position}: {verdict.Problem}.", path);
            }
        }
        return null;
    }

    /// <summary>A blob's text, or null when the path does not exist there. Over the validators' cap, a placeholder that
    /// fails every validator, so an oversized regenerated file is refused rather than skipped.</summary>
    private static async Task<string?> ReadTextAsync(JobContext context, GitCredential credential, string objectName)
    {
        var size = await context.GitAsync(["cat-file", "-s", objectName], credential);
        if (!size.Succeeded)
            return null;
        if (!long.TryParse(size.StandardOutput.Trim(), out var bytes) || bytes > ResolutionLimits.MaxParsedBytes)
            return "\0";
        return (await context.RequireAsync(["cat-file", "blob", objectName], credential)).StandardOutput;
    }

    /// <summary>
    /// §0 D52, D65: whether <paramref name="candidate"/>, the commit the pull request's branch moved to, is the one this
    /// job builds, pushed by an earlier attempt whose answer never reached the saga. The pinned head that built it is then
    /// no longer on the pull request, so the candidate is checked from its own content: without a patch the final commit
    /// is the prepared one, whose id the saga holds; with one, undoing the patch on the candidate's tree must rebuild the
    /// prepared commit bit for bit, and the patch on that must rebuild the candidate. Recognising it merges nothing the
    /// queue did not build: its content is the tree this job computes (T3), and the merge is still pinned to its sha (D13).
    /// </summary>
    private async Task<bool> IsOwnFinalAsync(JobContext context, PublishJob job, string candidate, IReadOnlyList<string> declared)
    {
        if (job.Patch.Length == 0)
            return candidate == job.PreparedSha;

        // Cheap first: a commit on another parent or with another message is not ours, and needs no blob.
        var raw = await context.GitAsync(["cat-file", "commit", candidate], null);
        if (!raw.Succeeded || CommitObject.Parse(raw.StandardOutput) is not { Parents: [var parent] } commit
            || parent != job.Prepare.BaseSha || !string.Equals(commit.Message, job.Prepare.Message, StringComparison.Ordinal))
            return false;

        if (await ApplyAsync(context, job, commit.Tree, declared, reverse: true) is not string priorTree)
            return false;
        var prior = await CommitTreeAsync(context, job.Prepare, priorTree);
        return prior == job.PreparedSha && (await FinalAsync(context, job, prior, declared)).Sha == candidate;
    }

    /// <summary><c>commit-tree</c> of <paramref name="tree"/> on the request's base with its dated author and committer
    /// and its message: the same inputs make the same commit (§0 D65).</summary>
    private static async Task<string> CommitTreeAsync(JobContext context, PrepareJob job, string tree)
        => Oid(await context.RequireAsync(["commit-tree", tree, "-p", job.BaseSha!, "-F", "-"], input: job.Message, author: job.Author, committer: job.Committer),
            "commit-tree");

    /// <summary>The validated patch applied to <paramref name="tree"/> (undone with <paramref name="reverse"/>) in a
    /// throwaway index: <c>git apply --cached</c> works in a bare repository, and the blobs the patch needs are fetched
    /// lazily. Returns the new tree's id, or the rejection.</summary>
    private static async Task<object> ApplyAsync(JobContext context, PublishJob job, string tree, IReadOnlyList<string> paths, bool reverse)
    {
        var index = Path.Combine(context.Scratch, reverse ? "undo.index" : "publish.index");
        var patch = Path.Combine(context.Scratch, "regenerate.patch");
        if (!File.Exists(patch))
            await File.WriteAllBytesAsync(patch, job.Patch, context.Deadline);
        File.Delete(index);
        await context.RequireAsync(["read-tree", tree], index: index);
        List<string> arguments = ["apply", "--cached", "--whitespace=nowarn"];
        if (reverse)
            arguments.Add("--reverse");
        arguments.AddRange(["--", patch]);
        var apply = await context.GitAsync(arguments, job.Prepare.Credential, index: index);
        if (!apply.Succeeded)
            return PatchVerdict.Reject(PatchReasons.ApplyFailed, apply.StandardError.Trim(), [.. paths]);
        // --missing-ok: the tree names blobs the blob-less clone never fetched; apply fetched the ones it needed.
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

    // ═══ git plumbing ═══════════════════════════════════════════════════════════════════════════════

    /// <summary>A bare, blob-less, single-branch clone with the hardened configuration persisted into it and an empty
    /// template (no hooks, no info/).</summary>
    private async Task CloneAsync(JobContext context, Uri url, GitCredential credential, string branch)
    {
        var arguments = new List<string> { "clone", "--bare", "--quiet", "--filter=blob:none", "--no-tags", "--single-branch", "--branch", branch, "--template=" };
        foreach (var (key, value) in git.HardenedConfig())
            arguments.Add($"--config={key}={value}");
        arguments.AddRange([url.AbsoluteUri, context.GitDirectory]);
        var clone = await context.GitAsync(arguments, credential, inRepository: false);
        if (!clone.Succeeded)
            throw new GitCommandException("clone", clone);
        context.EnsureWithinDisk();
    }

    private static Task FetchHeadAsync(JobContext context, GitCredential credential, string headRef)
        => context.RequireAsync(["fetch", "--quiet", "--no-tags", "--no-write-fetch-head", "origin", $"+{headRef}:{HeadTracking}"], credential);

    private static async Task<string> RevParseAsync(JobContext context, string revision)
        => Oid(await context.RequireAsync(["rev-parse", "--verify", "--quiet", "--end-of-options", revision]), "rev-parse");

    /// <summary>The commit <paramref name="branch"/> of <paramref name="remote"/> is at now, or null when it does not
    /// exist. Read with <c>ls-remote</c>, so nothing is fetched: a commit equal to the one this job built is all that
    /// is asked (§0 D52, D65). Any other failure throws (the saga retries the job).</summary>
    private static async Task<string?> RemoteTipAsync(JobContext context, string remote, GitCredential credential, string branch)
    {
        var name = $"refs/heads/{branch}";
        var listing = await context.RequireAsync(["ls-remote", remote, name], credential);
        foreach (var line in listing.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var tab = line.IndexOf('\t');
            if (tab > 0 && line[(tab + 1)..] == name)
                return WireValidation.ObjectId(line[..tab]);
        }
        return null;
    }

    private abstract record Push
    {
        public sealed record Done : Push;

        /// <summary>The remote ref already was the source: git sent nothing, and a lease is not checked then.</summary>
        public sealed record UpToDate : Push;

        public sealed record LeaseRejected : Push;
        public sealed record Rejected(string Detail) : Push;
    }

    /// <summary>A push of <paramref name="source"/> (null deletes) to <paramref name="remoteRef"/>, by default with a lease:
    /// the ref must still be at <paramref name="expected"/>, or not exist when that is null. <c>--porcelain</c> tells a
    /// rejected lease ("stale info") from a forge's refusal and an up-to-date ref (<c>=</c>) from a push; anything else
    /// (auth, network) throws.</summary>
    private static async Task<Push> PushAsync(JobContext context, Uri url, GitCredential credential, string? source, string remoteRef, string? expected, bool lease = true)
    {
        var arguments = new List<string> { "push", "--porcelain", "--no-verify", "--no-recurse-submodules" };
        if (lease)
            arguments.Add($"--force-with-lease={remoteRef}:{expected ?? ""}");
        arguments.AddRange([url.AbsoluteUri, $"{source ?? ""}:{remoteRef}"]);
        var result = await context.GitAsync(arguments, credential);
        var lines = result.StandardOutput.Split('\n');
        var refused = lines.FirstOrDefault(l => l.StartsWith('!') && l.Contains(remoteRef, StringComparison.Ordinal));
        if (refused is not null)
            return refused.Contains("(stale info)", StringComparison.Ordinal) ? new Push.LeaseRejected() : new Push.Rejected($"{refused.Trim()} {result.StandardError.Trim()}".Trim());
        if (result.Succeeded && source is not null && lines.Any(l => l.StartsWith('=') && l.Contains(remoteRef, StringComparison.Ordinal)))
            return new Push.UpToDate();
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

/// <summary>A commit object as <c>cat-file commit</c> prints it: its tree, its parents and its message (§0 D65's quick
/// check of a moved head before anything is fetched for it).</summary>
internal sealed record CommitObject(string Tree, IReadOnlyList<string> Parents, string Message)
{
    public static CommitObject? Parse(string raw)
    {
        var split = raw.IndexOf("\n\n", StringComparison.Ordinal);
        if (split < 0)
            return null;
        string? tree = null;
        List<string> parents = [];
        foreach (var header in raw[..split].Split('\n'))
        {
            if (header.StartsWith("tree ", StringComparison.Ordinal))
                tree = header["tree ".Length..];
            else if (header.StartsWith("parent ", StringComparison.Ordinal))
                parents.Add(header["parent ".Length..]);
        }
        return WireValidation.ObjectId(tree) is { } id ? new CommitObject(id, parents, raw[(split + 2)..]) : null;
    }
}
