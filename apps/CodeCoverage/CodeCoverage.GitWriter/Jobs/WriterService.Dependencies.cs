using System.Text;
using CodeCoverage.Git;
using CodeCoverage.Git.Dependencies;
using CodeCoverage.GitWriter.Jobs.Dependencies;

namespace CodeCoverage.GitWriter.Jobs;

/// <summary>A validated dependency job: the repository, the job's credential, the default branch and <c>expected_sha</c>.</summary>
public sealed record DependencyJob(string Repo, Uri CloneUrl, GitCredential Credential, string BaseBranch, string ExpectedSha);

/// <summary>The commit a dependency job builds: dated identities (§0 D65), so the same request builds the same commit.</summary>
public sealed record DependencyCommit(GitIdentity Author, GitIdentity Committer, string Message);

/// <summary>The apply patch and what it is judged against (the <c>dependencies</c> profile).</summary>
public sealed record DependencyPatchInput(DependencyPlan Plan, byte[] Patch, IReadOnlyCollection<string>? Claimed,
    IReadOnlyDictionary<string, string>? TargetActions, IReadOnlyCollection<string> AllowedHosts);

/// <summary>
/// The dependency updater's writes (docs/code-coverage/dependency-updates-PRD.md R4, R14, R16, P6), on the merge queue's
/// plumbing: a bare blob-less clone of the default branch, commits built with <c>commit-tree</c>, pushes under a lease. The
/// apply and migrate patches come from runners that installed fresh packages, so every job that writes one validates it first
/// (<see cref="DependencyPatchValidator"/>, <see cref="MigratePatchValidator"/>), whatever the saga already checked.
/// </summary>
public sealed partial class WriterService
{
    private const string RollingTracking = "refs/mq/rolling";

    /// <summary>R14: the work ref = <c>expected_sha</c> + the plan commit, or + the validated apply patch (a migrate run's base).</summary>
    public Task<DependencyJobResponse> DependencyWorkRefAsync(DependencyJob job, DependencyCommit commit, string? planJson, DependencyPatchInput? staged, string? expectedWorkSha)
        => jobs.RunAsync("dependencies-work-ref", job.Repo, async context =>
        {
            DependencyJobResponse Answer(string outcome, string? reason = null, string? sha = null, IReadOnlyList<string>? paths = null, string? detail = null)
                => new(outcome, reason, sha, null, paths ?? [], detail, context.ElapsedMilliseconds);

            await OpenDependenciesAsync(context, job);
            var baseTree = await RevParseAsync(context, $"{job.ExpectedSha}^{{tree}}");
            string tree;
            if (planJson is not null)
            {
                tree = await WriteFilesAsync(context, job, baseTree, [(DependencyFiles.PlanPath, planJson)]);
            }
            else
            {
                var verdict = await ValidateDependencyPatchAsync(context, job, staged!);
                if (verdict.Outcome == DependencyPatchOutcome.Rejected)
                    return Answer(JobOutcomes.PatchRejected, verdict.Reason, paths: verdict.Paths, detail: verdict.Detail);
                if (staged!.Patch.Length == 0)
                    tree = baseTree;
                else if (await ApplyPatchAsync(context, job.Credential, baseTree, staged.Patch, "staged.patch") is not { } applied)
                    return Answer(JobOutcomes.PatchRejected, PatchReasons.ApplyFailed, detail: "The apply patch does not apply to the planned commit.");
                else
                    tree = applied;
            }
            var built = await CommitTreeOnAsync(context, tree, job.ExpectedSha, commit);
            if (planJson is not null && await PlanCommitRule.CheckAsync(args => context.GitAsync(args, job.Credential), job.ExpectedSha, built) is { } broken)
                return Answer(DependencyJobOutcomes.PlanRejected, detail: broken);

            return await PushBotRefAsync(context, job, built, DependencyBranches.Work, expectedWorkSha) switch
            {
                Push.Done or Push.UpToDate => Answer(JobOutcomes.Pushed, sha: built),
                Push.LeaseRejected => Answer(DependencyJobOutcomes.BranchMoved, detail: $"{DependencyBranches.Work} is not at {expectedWorkSha}."),
                Push.Rejected rejected => Answer(JobOutcomes.PushRejected, detail: rejected.Detail),
                _ => throw new InvalidOperationException("Unknown push outcome."),
            };
        }, (outcome, reason, detail, ms) => new DependencyJobResponse(outcome, reason, null, null, [], detail, ms));

    /// <summary>The <c>dependencies</c> profile on the apply patch; nothing is pushed.</summary>
    public Task<DependencyJobResponse> DependencyValidateAsync(DependencyJob job, DependencyPatchInput input)
        => jobs.RunAsync("dependencies-validate", job.Repo, async context =>
        {
            await OpenDependenciesAsync(context, job);
            var verdict = await ValidateDependencyPatchAsync(context, job, input);
            return Verdict(verdict, context.ElapsedMilliseconds);
        }, (outcome, reason, detail, ms) => new DependencyJobResponse(outcome, reason, null, null, [], detail, ms));

    /// <summary>
    /// R4: the bot commit (<c>expected_sha</c> + the apply patch + each migration's patch), the human commits of the old
    /// branch replayed onto it (<see cref="RollingBranchReplay"/>), then a push under a lease, or none when the tree is the
    /// branch's already.
    /// </summary>
    /// <remarks>D-N2: when a replayed commit conflicts in a lockfile of the plan, the bot's side is taken and nothing is pushed
    /// to the rolling branch: the replayed head goes to the work ref (under <paramref name="expectedWorkSha"/>) and the answer
    /// is <see cref="DependencyJobOutcomes.RelockNeeded"/>, so the saga regenerates those lockfiles through the apply workflow
    /// and calls this job again on that head (no replay then: <paramref name="lastBotSha"/> is null).</remarks>
    public Task<DependencyJobResponse> DependencyRollingAsync(DependencyJob job, DependencyCommit commit, DependencyPatchInput input,
        IReadOnlyList<(string Group, byte[] Patch)> migrations, string? expectedRollingSha, string? lastBotSha, string? expectedWorkSha = null)
        => jobs.RunAsync("dependencies-rolling", job.Repo, async context =>
        {
            DependencyJobResponse Answer(string outcome, string? reason = null, string? sha = null, string? bot = null, IReadOnlyList<string>? paths = null,
                string? detail = null, IReadOnlyList<HumanManifestValue>? values = null, int replayed = 0)
                => new(outcome, reason, sha, bot, paths ?? [], detail, context.ElapsedMilliseconds)
                {
                    HumanValues = [.. (values ?? []).Select(v => new HumanValueWire(v.DepName, v.PackageFile, v.HeldValue, v.ProposedValue, v.Commit, v.Author))],
                    Replayed = replayed,
                };

            foreach (var (group, patch) in migrations)
            {
                var migrate = MigratePatchValidator.Validate(patch, options.Value.MaxPatchBytes);
                if (!migrate.Accepted)
                    return Answer(JobOutcomes.PatchRejected, migrate.Reason, paths: migrate.Paths, detail: $"The migration of '{group}' was refused: {migrate.Detail}");
            }

            await OpenDependenciesAsync(context, job);
            var verdict = await ValidateDependencyPatchAsync(context, job, input);
            if (verdict.Outcome == DependencyPatchOutcome.Rejected)
                return Answer(JobOutcomes.PatchRejected, verdict.Reason, paths: verdict.Paths, detail: verdict.Detail);

            var tree = await RevParseAsync(context, $"{job.ExpectedSha}^{{tree}}");
            if (input.Patch.Length > 0)
                tree = await ApplyPatchAsync(context, job.Credential, tree, input.Patch, "dependency.patch")
                       ?? throw new JobAbortedException(JobReasons.Error, "The validated apply patch does not apply to the planned commit.");
            var index = 0;
            foreach (var (group, patch) in migrations)
            {
                if (patch.Length == 0)
                    continue;
                if (await ApplyPatchAsync(context, job.Credential, tree, patch, $"migrate-{index++}.patch") is not { } migrated)
                    return Answer(JobOutcomes.PatchRejected, PatchReasons.ApplyFailed, detail: $"The migration of '{group}' does not apply on top of the earlier patches.");
                tree = migrated;
            }
            var bot = await CommitTreeOnAsync(context, tree, job.ExpectedSha, commit);

            string? oldTip = null;
            if (expectedRollingSha is not null)
            {
                var fetched = await context.GitAsync(["fetch", "--quiet", "--no-tags", "--no-write-fetch-head", "origin", $"+refs/heads/{DependencyBranches.Rolling}:{RollingTracking}"], job.Credential);
                oldTip = fetched.Succeeded ? await RevParseAsync(context, RollingTracking) : null;
                if (oldTip != expectedRollingSha)
                    return Answer(DependencyJobOutcomes.BranchMoved, detail: $"{DependencyBranches.Rolling} is at {oldTip ?? "nothing"}, not {expectedRollingSha}.");
                context.EnsureWithinDisk();
            }

            var tip = bot;
            IReadOnlyList<HumanManifestValue> values = [];
            var replayed = 0;
            if (oldTip is not null && lastBotSha is not null)
            {
                var replay = new RollingBranchReplay(git) { Timeout = context.Remaining, ScratchDirectory = context.Scratch, Config = JobContext.WriterConfig };
                var humans = await replay.HumanCommitsAsync(context.GitDirectory, lastBotSha, oldTip, context.Deadline, job.Credential);
                if (humans is null)
                    return Answer(DependencyJobOutcomes.RollingRewritten, sha: oldTip,
                        detail: $"{DependencyBranches.Rolling} no longer holds the bot's commit {lastBotSha}: it was rewritten by hand, so nothing was replayed or pushed.");
                var entries = DependencyPlanEntry.From(input.Plan)
                    .Select(e => new ReplayManifestEntry(e.DepName, e.PackageName, e.PackageFile, e.CurrentValue, e.NewValue)).ToList();
                var relockable = input.Plan.Groups.SelectMany(g => g.Items).SelectMany(i => i.LockFiles).ToHashSet(StringComparer.Ordinal);
                switch (await replay.ReplayAsync(context.GitDirectory, bot, humans, entries, context.Deadline, job.Credential, relockable))
                {
                    case ReplayOutcome.Conflict conflict:
                        return Answer(DependencyJobOutcomes.ReplayConflict, sha: oldTip, paths: conflict.Paths, detail: conflict.Detail, values: conflict.HumanValues);
                    case ReplayOutcome.Replayed { RelockPaths.Count: > 0 } relock:
                        // D-N2: the replayed head goes to the work ref, where the apply workflow regenerates the lockfiles.
                        return await PushBotRefAsync(context, job, relock.Tip, DependencyBranches.Work, expectedWorkSha) switch
                        {
                            Push.Done or Push.UpToDate => Answer(DependencyJobOutcomes.RelockNeeded, sha: relock.Tip, bot: bot, paths: relock.RelockPaths,
                                values: relock.HumanValues, replayed: relock.Commits.Count) with
                            {
                                Verdict = VerdictWord(verdict.Outcome), AppliedGroups = verdict.AppliedGroups, RequiresWorkflowsPermission = verdict.RequiresWorkflowsPermission,
                            },
                            Push.LeaseRejected => Answer(DependencyJobOutcomes.BranchMoved, detail: $"{DependencyBranches.Work} is not at {expectedWorkSha}."),
                            Push.Rejected rejected => Answer(JobOutcomes.PushRejected, detail: rejected.Detail),
                            _ => throw new InvalidOperationException("Unknown push outcome."),
                        };
                    case ReplayOutcome.Replayed done:
                        tip = done.Tip;
                        values = done.HumanValues;
                        replayed = done.Commits.Count;
                        break;
                }
            }

            // R4: no new versions means no push and no CI.
            if (oldTip is not null && await RevParseAsync(context, $"{tip}^{{tree}}") == await RevParseAsync(context, $"{oldTip}^{{tree}}"))
                return Answer(DependencyJobOutcomes.Unchanged, sha: oldTip, bot: lastBotSha ?? bot, values: values, replayed: replayed) with
                {
                    Verdict = VerdictWord(verdict.Outcome), AppliedGroups = verdict.AppliedGroups,
                };

            return await PushAsync(context, job.CloneUrl, job.Credential, tip, $"refs/heads/{DependencyBranches.Rolling}", expectedRollingSha) switch
            {
                Push.Done or Push.UpToDate => Answer(JobOutcomes.Pushed, sha: tip, bot: bot, paths: verdict.Paths, values: values, replayed: replayed) with
                {
                    Verdict = VerdictWord(verdict.Outcome), AppliedGroups = verdict.AppliedGroups, RequiresWorkflowsPermission = verdict.RequiresWorkflowsPermission,
                },
                Push.LeaseRejected => Answer(DependencyJobOutcomes.BranchMoved, detail: $"{DependencyBranches.Rolling} moved during the job."),
                Push.Rejected rejected => Answer(JobOutcomes.PushRejected, detail: rejected.Detail),
                _ => throw new InvalidOperationException("Unknown push outcome."),
            };
        }, (outcome, reason, detail, ms) => new DependencyJobResponse(outcome, reason, null, null, [], detail, ms));

    /// <summary>R16: one commit writing the onboarding files on <c>expected_sha</c>, force-pushed to the onboarding branch.</summary>
    public Task<DependencyJobResponse> DependencyFilesAsync(DependencyJob job, DependencyCommit commit, IReadOnlyList<(string Path, string? Content)> files)
        => jobs.RunAsync("dependencies-files", job.Repo, async context =>
        {
            await OpenDependenciesAsync(context, job);
            var baseTree = await RevParseAsync(context, $"{job.ExpectedSha}^{{tree}}");
            var tree = await WriteFilesAsync(context, job, baseTree, files);
            var built = await CommitTreeOnAsync(context, tree, job.ExpectedSha, commit);
            return await PushBotRefAsync(context, job, built, DependencyBranches.Onboarding, null) switch
            {
                Push.Done or Push.UpToDate => new DependencyJobResponse(JobOutcomes.Pushed, null, built, null, [.. files.Select(f => f.Path)], null, context.ElapsedMilliseconds),
                Push.Rejected rejected => new DependencyJobResponse(JobOutcomes.PushRejected, null, null, null, [], rejected.Detail, context.ElapsedMilliseconds),
                _ => throw new InvalidOperationException("Unknown push outcome."),
            };
        }, (outcome, reason, detail, ms) => new DependencyJobResponse(outcome, reason, null, null, [], detail, ms));

    /// <summary>R14: the work ref is removed after every run, while it is still at <paramref name="expectedSha"/>.</summary>
    public Task<DependencyJobResponse> DependencyDeleteWorkRefAsync(string repo, Uri cloneUrl, GitCredential credential, string? expectedSha)
        => jobs.RunAsync("dependencies-delete", repo, async context =>
        {
            await context.RequireAsync(["init", "--bare", "--quiet", "--template=", context.GitDirectory], inRepository: false);
            return await PushAsync(context, cloneUrl, credential, null, $"refs/heads/{DependencyBranches.Work}", expectedSha, lease: expectedSha is not null) switch
            {
                Push.Done or Push.UpToDate => new DependencyJobResponse(JobOutcomes.Deleted, null, null, null, [], null, context.ElapsedMilliseconds),
                Push.LeaseRejected => new DependencyJobResponse(DependencyJobOutcomes.BranchMoved, null, null, null, [], $"{DependencyBranches.Work} is not at {expectedSha}.", context.ElapsedMilliseconds),
                Push.Rejected rejected => new DependencyJobResponse(JobOutcomes.PushRejected, null, null, null, [], rejected.Detail, context.ElapsedMilliseconds),
                _ => throw new InvalidOperationException("Unknown push outcome."),
            };
        }, (outcome, reason, detail, ms) => new DependencyJobResponse(outcome, reason, null, null, [], detail, ms));

    // ═══ Helpers ════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The default branch, blob-less; <c>expected_sha</c> fetched when the branch no longer holds it.</summary>
    private async Task OpenDependenciesAsync(JobContext context, DependencyJob job)
    {
        await CloneAsync(context, job.CloneUrl, job.Credential, job.BaseBranch);
        var present = await context.GitAsync(["rev-parse", "--verify", "--quiet", "--end-of-options", $"{job.ExpectedSha}^{{commit}}"], job.Credential);
        if (!present.Succeeded)
        {
            var fetched = await context.GitAsync(["fetch", "--quiet", "--no-tags", "--no-write-fetch-head", "--filter=blob:none", "origin", job.ExpectedSha], job.Credential);
            if (!fetched.Succeeded)
                throw new JobAbortedException(JobReasons.NotFound, $"{job.ExpectedSha}, the planned commit, is not a commit of this repository any more.");
        }
        context.EnsureWithinDisk();
    }

    private async Task<DependencyPatchVerdict> ValidateDependencyPatchAsync(JobContext context, DependencyJob job, DependencyPatchInput input)
    {
        var request = DependencyPatchRequest.From(input.Plan) with
        {
            ClaimedAppliedGroups = input.Claimed,
            TargetActions = input.TargetActions,
            MaxBytes = options.Value.MaxPatchBytes,
            Lockfiles = new LockfileScanOptions([.. LockfileScanOptions.PublicRegistries, .. input.AllowedHosts], LockfileScanOptions.Default.MaxNewPackages),
        };
        if (input.Patch.Length == 0)
        {
            // Nothing applied: only a run that claims nothing is consistent with an empty patch (D-V5).
            return input.Claimed is { Count: > 0 } claimed
                ? DependencyPatchVerdict.Reject(DependencyPatchReasons.GroupMismatch, $"The patch is empty but result.json claims [{string.Join(", ", claimed)}].")
                : new DependencyPatchVerdict(DependencyPatchOutcome.Accepted, null, null, [], [], false, []);
        }
        var tree = new GitBaseTree(args => context.GitAsync(args, job.Credential), job.ExpectedSha);
        return await DependencyPatchValidator.ValidateAsync(input.Patch, request, tree, context.Deadline);
    }

    private static DependencyJobResponse Verdict(DependencyPatchVerdict verdict, long ms) => new(DependencyJobOutcomes.Validated, verdict.Reason, null, null, verdict.Paths, verdict.Detail, ms)
    {
        Verdict = VerdictWord(verdict.Outcome),
        AppliedGroups = verdict.AppliedGroups,
        RequiresWorkflowsPermission = verdict.RequiresWorkflowsPermission,
        NewPackages = verdict.NewPackages,
    };

    private static string VerdictWord(DependencyPatchOutcome outcome) => outcome switch
    {
        DependencyPatchOutcome.Accepted => DependencyJobOutcomes.Accepted,
        DependencyPatchOutcome.NeedsHuman => DependencyJobOutcomes.NeedsHuman,
        _ => DependencyJobOutcomes.Rejected,
    };

    /// <summary>A patch applied to <paramref name="tree"/> in a throwaway index; the new tree, or null when it does not apply.</summary>
    private static async Task<string?> ApplyPatchAsync(JobContext context, GitCredential credential, string tree, byte[] patch, string name)
    {
        var index = Path.Combine(context.Scratch, $"{name}.index");
        var file = Path.Combine(context.Scratch, name);
        await File.WriteAllBytesAsync(file, patch, context.Deadline);
        File.Delete(index);
        await context.RequireAsync(["read-tree", tree], index: index);
        var apply = await context.GitAsync(["apply", "--cached", "--whitespace=nowarn", "--", file], credential, index: index);
        if (!apply.Succeeded)
            return null;
        return Oid(await context.RequireAsync(["write-tree", "--missing-ok"], index: index), "write-tree");
    }

    /// <summary><paramref name="tree"/> with each file written (text) or removed (null content).</summary>
    private static async Task<string> WriteFilesAsync(JobContext context, DependencyJob job, string tree, IReadOnlyList<(string Path, string? Content)> files)
    {
        var index = Path.Combine(context.Scratch, $"files-{Guid.NewGuid():N}.index");
        await context.RequireAsync(["read-tree", tree], index: index);
        var info = new StringBuilder();
        foreach (var (path, content) in files)
        {
            if (content is null)
            {
                info.Append("0 ").Append(new string('0', 40)).Append('\t').Append(path).Append('\0');
                continue;
            }
            var blob = Oid(await context.RequireAsync(["hash-object", "-w", "--stdin"], job.Credential, input: content), "hash-object");
            info.Append("100644 ").Append(blob).Append('\t').Append(path).Append('\0');
        }
        await context.RequireAsync(["update-index", "-z", "--index-info"], input: info.ToString(), index: index);
        return Oid(await context.RequireAsync(["write-tree", "--missing-ok"], index: index), "write-tree");
    }

    private static async Task<string> CommitTreeOnAsync(JobContext context, string tree, string parent, DependencyCommit commit)
        => Oid(await context.RequireAsync(["commit-tree", tree, "-p", parent, "-F", "-"], input: commit.Message, author: commit.Author, committer: commit.Committer), "commit-tree");

    /// <summary>A bot-owned ref: under the lease when one is known, else replaced whatever it holds (a crashed run's leftover).</summary>
    private static Task<Push> PushBotRefAsync(JobContext context, DependencyJob job, string sha, string branch, string? expected)
        => expected is null
            ? PushAsync(context, job.CloneUrl, job.Credential, "+" + sha, $"refs/heads/{branch}", null, lease: false)
            : PushAsync(context, job.CloneUrl, job.Credential, sha, $"refs/heads/{branch}", expected);
}

/// <summary>The bot-owned branches the dependency jobs write (§7.3, R14, R16); no request names another.</summary>
public static class DependencyBranches
{
    public const string Rolling = "mintplayer/dependencies";
    public const string Work = "mintplayer/dependencies-work";
    public const string Onboarding = "mintplayer/dependencies-onboarding";
}
