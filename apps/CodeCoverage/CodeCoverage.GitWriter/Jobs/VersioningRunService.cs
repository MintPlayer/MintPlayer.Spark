using CodeCoverage.Git;
using CodeCoverage.Versioning.Git;
using CodeCoverage.Versioning.Graph;
using CodeCoverage.Versioning.Releases;
using CodeCoverage.Versioning.Versions;

namespace CodeCoverage.GitWriter.Jobs;

/// <summary>A validated <c>POST /jobs/versioning/run</c>.</summary>
public sealed record VersioningRunJob(
    string Repo, Uri CloneUrl, GitCredential Credential, string Branch, string DefaultBranch, GitIdentity Tagger, ReleaseRunRequest Run, bool PlanOnly = false);

/// <summary>A validated <c>POST /jobs/versioning/plan</c>.</summary>
public sealed record VersioningPlanJob(
    string Repo, Uri CloneUrl, GitCredential Credential, string Branch, string DefaultBranch, string Commit, int? BranchMajor, VersioningSettings Settings)
{
    /// <summary>The release line of <see cref="Branch"/> when lines are on (#568 D1).</summary>
    public ReleaseLine? Line { get; init; }
}

/// <summary>A validated <c>POST /jobs/versioning/backfill</c> (#638 D5-D8).</summary>
public sealed record VersioningBackfillJob(
    string Repo, Uri CloneUrl, GitCredential Credential, string DefaultBranch, GitIdentity Tagger, VersioningSettings Settings, bool PlanOnly,
    IReadOnlySet<string> Move);

/// <summary>A validated <c>POST /jobs/versioning/ecosystems</c> (#570 M8b).</summary>
public sealed record VersioningEcosystemsJob(
    string Repo, Uri CloneUrl, GitCredential Credential, string BaseBranch, string HeadRef, string HeadSha, VersioningSettings Settings);

/// <summary>
/// The server host's whole versioning run (#153 M6, PRD D2). The plan and the tags are the engine's
/// <see cref="ReleaseRun"/>, the same the action's local host runs; this job supplies the repository: an empty bare one
/// into which the run's branch, the default branch and every tag are fetched. Blobs are fetched too, because the graph is
/// read from the built commit's project files.
/// </summary>
/// <remarks>
/// The engine reads that repository with its own <see cref="GitCli"/>: <c>ls-tree</c>, <c>cat-file</c>,
/// <c>diff-tree</c> and <c>for-each-ref</c> on a bare directory this job created, with hooks, fsmonitor and the system
/// configuration off and no credential anywhere. Everything that reaches the forge goes through the job's
/// <see cref="GitRunner"/>, as in <see cref="TagService"/>.
/// </remarks>
public sealed class VersioningRunService(JobRunner jobs)
{
    public Task<VersioningRunResponse> RunAsync(VersioningRunJob job) => jobs.RunAsync("versioning-run", job.Repo, async context =>
    {
        await context.RequireAsync(["init", "--bare", "--quiet", "--template=", context.GitDirectory], inRepository: false);
        string[] fetch = ["fetch", "--quiet", "--no-tags", "--no-write-fetch-head", job.CloneUrl.AbsoluteUri,
            .. new[] { job.Branch, job.DefaultBranch }.Distinct(StringComparer.Ordinal).Select(b => $"+refs/heads/{b}:refs/heads/{b}"),
            "+refs/tags/*:refs/tags/*"];
        await context.RequireAsync(fetch, job.Credential);
        context.EnsureWithinDisk();

        var git = new GitCli(context.GitDirectory);
        var commit = job.Run.Manifest.Commit!;
        // The token's sha is the commit the workflow built; a branch pushed since still holds it, a force-push does not.
        var onBranch = await git.RunAsync(["merge-base", "--is-ancestor", commit, "refs/heads/" + job.Branch], cancellationToken: context.Deadline);
        if (onBranch.ExitCode != 0)
            throw new JobAbortedException(JobReasons.Unplannable, $"The built commit {commit} is not on {job.Branch}.");

        var writer = new TagWriter(new TagService.JobTagGit(context, job.Credential), job.CloneUrl.AbsoluteUri, ["refs/heads/" + job.Branch],
            new Tagger(job.Tagger.Name, job.Tagger.Email));
        ReleaseRunResult result;
        TagPlan? plan = null;
        try
        {
            // #552 D1: the same run with a recording writer; the tags are written later through /jobs/tags.
            if (job.PlanOnly) (result, plan) = await ReleaseRun.PlanOnlyAsync(git, job.Run, context.Deadline);
            else result = await ReleaseRun.RunAsync(git, writer, job.Run, context.Deadline);
        }
        catch (Exception e) when (e is ReleaseRunException or GraphException)
        {
            throw new JobAbortedException(JobReasons.Unplannable, e.Message);
        }
        catch (GitException e)
        {
            throw new JobAbortedException(JobReasons.Error, e.Message);
        }
        return Response(result, context.ElapsedMilliseconds) with { Plan = plan };
    }, (outcome, reason, detail, ms) => new VersioningRunResponse(outcome, reason, [], [], [], [], detail, ms));

    /// <summary>
    /// #638 D5-D8: the history backfill. Only the default branch and the branches the line pattern matches are fetched
    /// (never a feature branch, D6), with every tag, so a version already tagged is not planned again; the tags are written
    /// in batches of <see cref="Versioning.Graph.Limits.MaxPackagesPerRun"/>, each reachable from the branch it was found on.
    /// </summary>
    public Task<VersioningBackfillResponse> BackfillAsync(VersioningBackfillJob job) => jobs.RunAsync("versioning-backfill", job.Repo, async context =>
    {
        await context.RequireAsync(["init", "--bare", "--quiet", "--template=", context.GitDirectory], inRepository: false);
        var listing = await context.RequireAsync(["ls-remote", "--heads", job.CloneUrl.AbsoluteUri], job.Credential);
        var branches = new List<string> { job.DefaultBranch };
        foreach (var line in listing.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var name = line.Split('\t') is [_, var reference] && reference.StartsWith("refs/heads/", StringComparison.Ordinal) ? reference["refs/heads/".Length..] : null;
            if (name is not null && name != job.DefaultBranch && job.Settings.Lines is { } pattern && pattern.TryMatch(name, out _)) branches.Add(name);
        }
        await context.RequireAsync(["fetch", "--quiet", "--no-tags", "--no-write-fetch-head", job.CloneUrl.AbsoluteUri,
            .. branches.Select(b => $"+refs/heads/{b}:refs/heads/{b}"), "+refs/tags/*:refs/tags/*"], job.Credential);
        context.EnsureWithinDisk();

        BackfillPlan plan;
        try
        {
            plan = await HistoryBackfill.PlanAsync(new GitCli(context.GitDirectory), job.DefaultBranch, job.Settings,
                new Tagger(job.Tagger.Name, job.Tagger.Email), context.Deadline);
        }
        catch (VersioningException e)
        {
            throw new JobAbortedException(JobReasons.Unplannable, e.Message);
        }
        catch (GitException e)
        {
            throw new JobAbortedException(JobReasons.Error, e.Message);
        }

        var outcomes = new Dictionary<string, TagWriteResult>(StringComparer.Ordinal);
        if (!job.PlanOnly)
        {
            var writer = new TagWriter(new TagService.JobTagGit(context, job.Credential), job.CloneUrl.AbsoluteUri, plan.Branches.Select(b => "refs/heads/" + b).ToList(),
                new Tagger(job.Tagger.Name, job.Tagger.Email));
            foreach (var batch in plan.Tags.Chunk(Versioning.Graph.Limits.MaxPackagesPerRun))
                foreach (var result in await writer.WriteAsync([.. batch.Select(t => new TagToWrite(t.Tag, t.Commit, new TagAnnotation(null, null)))], context.Deadline))
                    outcomes[result.Tag.Name] = result;
        }

        // D9: only the collisions the owner chose, each leased on the tag object this job fetched.
        var moves = new Dictionary<string, TagMoveResult>(StringComparer.Ordinal);
        var chosen = plan.Collisions.Where(c => job.Move.Contains(c.Tag.Name)).ToList();
        if (!job.PlanOnly && chosen.Count > 0)
        {
            var mover = new TagMover(new TagService.JobTagGit(context, job.Credential), job.CloneUrl.AbsoluteUri, new Tagger(job.Tagger.Name, job.Tagger.Email));
            foreach (var result in await mover.MoveAsync([.. chosen.Select(c => new TagMove(c.Tag, c.Commit, c.TagObject, new TagAnnotation(null, null)))], context.Deadline))
                moves[result.Tag.Name] = result;
        }

        var tags = plan.Tags.Select(t => outcomes.TryGetValue(t.Tag.Name, out var r)
            ? new BackfillTagWire(t.Tag.Name, t.Commit, t.Branch, TagService.Wire(r.Outcome), r.Detail)
            : new BackfillTagWire(t.Tag.Name, t.Commit, t.Branch, TagService.Wire(TagOutcome.Planned), null)).ToList();
        var collisions = plan.Collisions.Select(c => new BackfillCollisionWire(c.Tag.Name, c.Current, c.Commit, c.Branch,
            job.PlanOnly ? "planned" : moves.TryGetValue(c.Tag.Name, out var m) ? (m.Moved ? "moved" : "conflict") : "left",
            moves.TryGetValue(c.Tag.Name, out var d) ? d.Detail : null)).ToList();
        return new VersioningBackfillResponse(job.PlanOnly ? JobOutcomes.Planned : JobOutcomes.Tagged, null, tags, plan.Branches, plan.Notes, null,
            context.ElapsedMilliseconds) { Collisions = collisions };
    }, (outcome, reason, detail, ms) => new VersioningBackfillResponse(outcome, reason, [], [], [], detail, ms));

    /// <summary>The plan alone (D3 trigger B): the branch and the tags fetched as for a run, nothing written.</summary>
    public Task<VersioningPlanResponse> PlanAsync(VersioningPlanJob job) => jobs.RunAsync("versioning-plan", job.Repo, async context =>
    {
        await context.RequireAsync(["init", "--bare", "--quiet", "--template=", context.GitDirectory], inRepository: false);
        await context.RequireAsync(["fetch", "--quiet", "--no-tags", "--no-write-fetch-head", job.CloneUrl.AbsoluteUri,
            $"+refs/heads/{job.Branch}:refs/heads/{job.Branch}", "+refs/tags/*:refs/tags/*"], job.Credential);
        context.EnsureWithinDisk();

        var git = new GitCli(context.GitDirectory);
        var onBranch = await git.RunAsync(["merge-base", "--is-ancestor", job.Commit, "refs/heads/" + job.Branch], cancellationToken: context.Deadline);
        if (onBranch.ExitCode != 0)
            throw new JobAbortedException(JobReasons.Unplannable, $"The commit {job.Commit} is not on {job.Branch}.");
        try
        {
            var (plan, warnings) = await ReleaseRun.PlanAsync(git, job.Commit, job.Settings, job.Branch == job.DefaultBranch, job.BranchMajor, context.Deadline, job.Line);
            return new VersioningPlanResponse(JobOutcomes.Planned, null, plan.Releases.Select(r => r.Id).ToList(),
                plan.Refused.Select(n => new PackageNoteWire(n.Id, n.Reason)).ToList(), warnings, null, context.ElapsedMilliseconds);
        }
        catch (GraphException e)
        {
            throw new JobAbortedException(JobReasons.Unplannable, e.Message);
        }
        catch (GitException e)
        {
            throw new JobAbortedException(JobReasons.Error, e.Message);
        }
    }, (outcome, reason, detail, ms) => new VersioningPlanResponse(outcome, reason, [], [], [], detail, ms));

    /// <summary>
    /// #570 M8b (PRD D5): the ecosystems a pull request touches. The base branch and the pull request's head are fetched;
    /// the diff runs from their merge base, so the default branch's own changes since the pull request branched off are
    /// not counted, and the graph is the head's.
    /// </summary>
    public Task<VersioningEcosystemsResponse> EcosystemsAsync(VersioningEcosystemsJob job) => jobs.RunAsync("versioning-ecosystems", job.Repo, async context =>
    {
        const string head = "refs/heads/coverage-pr-head";
        await context.RequireAsync(["init", "--bare", "--quiet", "--template=", context.GitDirectory], inRepository: false);
        await context.RequireAsync(["fetch", "--quiet", "--no-tags", "--no-write-fetch-head", job.CloneUrl.AbsoluteUri,
            $"+refs/heads/{job.BaseBranch}:refs/heads/{job.BaseBranch}", $"+{job.HeadRef}:{head}"], job.Credential);
        context.EnsureWithinDisk();

        var git = new GitCli(context.GitDirectory);
        try
        {
            var fetched = (await git.RunAsync(["rev-parse", "--verify", "--quiet", head + "^{commit}"], cancellationToken: context.Deadline)).Text.Trim();
            if (fetched != job.HeadSha)
                return new VersioningEcosystemsResponse(JobOutcomes.HeadMismatch, null, [], $"{job.HeadRef} is at {fetched}, not {job.HeadSha}.", context.ElapsedMilliseconds);
            var mergeBase = (await git.RunAsync(["merge-base", "refs/heads/" + job.BaseBranch, job.HeadSha], cancellationToken: context.Deadline))
                .EnsureSuccess("merge-base").Text.Trim();
            var graph = await ProjectGraphBuilder.BuildAsync(await GitTree.OpenAsync(git, job.HeadSha, context.Deadline), job.Settings.Ignore, context.Deadline);
            var ecosystems = await ChangedEcosystems.OfAsync(graph, new GitInputDiff(git), mergeBase, job.HeadSha, job.Settings, context.Deadline);
            return new VersioningEcosystemsResponse(JobOutcomes.Planned, null, ecosystems, null, context.ElapsedMilliseconds);
        }
        catch (GraphException e)
        {
            throw new JobAbortedException(JobReasons.Unplannable, e.Message);
        }
        catch (GitException e)
        {
            throw new JobAbortedException(JobReasons.Error, e.Message);
        }
    }, (outcome, reason, detail, ms) => new VersioningEcosystemsResponse(outcome, reason, [], detail, ms));

    internal static VersioningRunResponse Response(ReleaseRunResult result, long durationMs)
    {
        var outcomes = result.Tags.ToDictionary(t => t.Tag.Name, StringComparer.Ordinal);
        var releases = result.Plan.Releases
            .Select(r => new ReleaseWire(r.Id, ManifestPackage.Wire(r.Project.Kind), r.Version.ToString(), r.Tag.Name, r.Reasons, r.ApiHash, r.TaggedByHand,
                outcomes.TryGetValue(r.Tag.Name, out var written) ? TagService.Wire(written.Outcome) : null,
                result.Notes?.GetValueOrDefault(r.Tag.Name) is { } notes ? new ReleaseNotesWire(notes.Name, notes.Body, notes.Prerelease) : null))
            .ToList();
        return new VersioningRunResponse(
            result.Plan.Refused.Count > 0 ? JobOutcomes.PlanRefused : JobOutcomes.Versioned, null, releases,
            result.Plan.Skipped.Select(n => new PackageNoteWire(n.Id, n.Reason)).ToList(),
            result.Plan.Refused.Select(n => new PackageNoteWire(n.Id, n.Reason)).ToList(),
            result.Warnings, null, durationMs,
            (result.BranchCuts ?? []).Select(c => new BranchCutWire(c.Branch, c.Major, c.Commit, c.Packages, c.PullRequest, c.Ecosystem)).ToList())
        {
            LineMajors = result.Plan.LineMajors.Count > 0 ? result.Plan.LineMajors : null,
        };
    }
}
