using CodeCoverage.Git;
using CodeCoverage.Versioning.Git;
using CodeCoverage.Versioning.Graph;
using CodeCoverage.Versioning.Releases;

namespace CodeCoverage.GitWriter.Jobs;

/// <summary>A validated <c>POST /jobs/versioning/run</c>.</summary>
public sealed record VersioningRunJob(
    string Repo, Uri CloneUrl, GitCredential Credential, string Branch, string DefaultBranch, GitIdentity Tagger, ReleaseRunRequest Run, bool PlanOnly = false);

/// <summary>A validated <c>POST /jobs/versioning/plan</c>.</summary>
public sealed record VersioningPlanJob(
    string Repo, Uri CloneUrl, GitCredential Credential, string Branch, string DefaultBranch, string Commit, int? BranchMajor, VersioningSettings Settings);

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
            var (plan, warnings) = await ReleaseRun.PlanAsync(git, job.Commit, job.Settings, job.Branch == job.DefaultBranch, job.BranchMajor, context.Deadline);
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
            (result.BranchCuts ?? []).Select(c => new BranchCutWire(c.Branch, c.Major, c.Commit, c.Packages, c.PullRequest)).ToList());
    }
}
