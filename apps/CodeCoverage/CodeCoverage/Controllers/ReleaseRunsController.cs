using CodeCoverage.Forge;
using CodeCoverage.Services;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Caching.Memory;
using MintPlayer.Spark.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Services;

namespace CodeCoverage.Controllers;

/// <summary>
/// The commit graph's release overlay (#544 D5): the status of the workflow run a commit's version tags name
/// (<see cref="CommitGraph.CommitGraphRow.ReleaseRun"/>), and the button that re-runs its failed jobs.
/// </summary>
/// <remarks>
/// <para>
/// <b>Who.</b> Everyone who may browse the repository sees the status; both halves resolve it through
/// <see cref="IRepositoryScope"/> and refuse as the browse API does (#453). The re-run is for a viewer whose own forge
/// login has write access to the repository, read with the viewer's linked sign-in exactly as the merge-queue panel
/// reads it; it is checked again here, whatever the page offered.
/// </para>
/// <para>
/// <b>The cancel trap.</b> A release workflow cancels its older run when a newer one starts (D3). Re-running an older
/// commit's run while a newer run of the same workflow is unfinished would therefore cancel the newer one, so the button
/// is refused until it has finished.
/// </para>
/// </remarks>
[ApiController]
public partial class ReleaseRunsController : ControllerBase
{
    [Inject] private readonly IRepositoryScope scope;
    [Inject] private readonly IEnumerable<IForgeAccessService> accounts;
    [Inject] private readonly IEnumerable<IForgeMergeQueue> queues;
    [Inject] private readonly IMemoryCache cache;
    [Inject] private readonly ILogger<ReleaseRunsController> logger;

    /// <summary>The most runs one request asks about: a graph page's tagged commits.</summary>
    public const int MaxRuns = 20;

    /// <summary>A finished attempt does not change until someone re-runs it.</summary>
    private static readonly TimeSpan FinishedCacheDuration = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan UnfinishedCacheDuration = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan RoleCacheDuration = TimeSpan.FromMinutes(1);

    /// <param name="CanRerun">The viewer may re-run the failed jobs: signed in, write access, a run that failed or was
    /// cancelled, and no newer run of its workflow unfinished.</param>
    /// <param name="Blocked">Why a failed run cannot be re-run now (a newer run is unfinished); null otherwise.</param>
    public sealed record ReleaseRunView(long Id, string Status, string? Conclusion, int Attempt, string? HtmlUrl, bool CanRerun, string? Blocked);

    public sealed record RerunRefused(string Error);

    /// <summary>The runs <paramref name="ids"/> name (comma-separated, at most <see cref="MaxRuns"/>); a run the forge does not know is left out.</summary>
    [HttpGet("api/browse/repos/{provider}/{owner}/{name}/release-runs")]
    [EnableRateLimiting("browse")]
    [SparkAuthorize("Browse", "Coverage")]
    public async Task<ActionResult<IReadOnlyList<ReleaseRunView>>> Get(string provider, string owner, string name, [FromQuery] string? ids, CancellationToken cancellationToken)
    {
        var repository = await scope.ResolveAsync(provider, owner, name, cancellationToken);
        if (repository is null) return Refuse();
        if (queues.FirstOrDefault(q => q.Provider == repository.Provider) is not { } queue) return Ok(Array.Empty<ReleaseRunView>());

        var runIds = (ids ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(id => long.TryParse(id, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var run) ? run : 0)
            .Where(id => id > 0).Distinct().Take(MaxRuns).ToList();
        if (runIds.Count == 0) return Ok(Array.Empty<ReleaseRunView>());

        var mayRerun = await HasWriteAccessAsync(repository, queue, cancellationToken);
        var views = new List<ReleaseRunView>();
        try
        {
            foreach (var id in runIds)
            {
                if (await RunAsync(repository, queue, id, cancellationToken) is not { } run) continue;
                var blocked = mayRerun && Failed(run) && await NewerUnfinishedAsync(repository, queue, run, cancellationToken) ? NewerRunBlocks : null;
                views.Add(new ReleaseRunView(run.Id, run.Status, run.Conclusion, run.Attempt, run.HtmlUrl, mayRerun && Failed(run) && blocked is null, blocked));
            }
        }
        catch (Exception ex) when (ex is ForgeAccessDeniedException or ForgeRequestException or HttpRequestException)
        {
            // The overlay is a convenience: a forge that cannot answer leaves the rows without a mark, never a broken graph.
            logger.LogDebug(ex, "Release runs: could not read the runs of {Repository}", repository.FullName);
        }
        return Ok(views);
    }

    /// <summary>Re-runs the failed and cancelled jobs of a release run (D5).</summary>
    // RequireAntiforgeryToken, not MVC's attribute (#300): see RepoSettingsController.RotateBadgeToken.
    [HttpPost("api/repos/{provider}/{owner}/{name}/release-runs/{runId:long}/rerun")]
    [SparkAuthorize("Manage", "RepoSettings")]
    [RequireAntiforgeryToken]
    public async Task<IActionResult> Rerun(string provider, string owner, string name, long runId, CancellationToken cancellationToken)
    {
        var repository = await scope.ResolveAsync(provider, owner, name, cancellationToken);
        if (repository is null) return Refuse();
        if (runId <= 0) return BadRequest(new RerunRefused("A run id is a positive whole number."));
        if (queues.FirstOrDefault(q => q.Provider == repository.Provider) is not { } queue)
            return Conflict(new RerunRefused("This repository's forge cannot re-run workflow runs from here (GitHub only for now)."));
        if (!await HasWriteAccessAsync(repository, queue, cancellationToken))
            return StatusCode(StatusCodes.Status403Forbidden, new RerunRefused("Re-running a release needs write access to the repository, through your linked GitHub sign-in."));

        try
        {
            var run = await queue.GetWorkflowRunAsync(repository, runId, cancellationToken);
            if (run is null) return NotFound(new RerunRefused($"{repository.FullName} has no workflow run {runId}."));
            if (!Failed(run)) return Conflict(new RerunRefused("The run has no failed or cancelled jobs to re-run."));
            if (await queue.HasNewerUnfinishedRunAsync(repository, run, cancellationToken)) return Conflict(new RerunRefused(NewerRunBlocks));
            if (!await queue.RerunFailedJobsAsync(repository, runId, cancellationToken))
                return UnprocessableEntity(new RerunRefused("GitHub would not re-run it; a run older than a month can no longer be re-run."));
        }
        catch (ForgeAccessDeniedException ex)
        {
            logger.LogInformation(ex, "Release runs: re-run of {Run} in {Repository} refused by the forge", runId, repository.FullName);
            return StatusCode(StatusCodes.Status403Forbidden, new RerunRefused(
                "The GitHub App may not re-run workflows in this repository: accept its request for Actions (write) access in the installation's settings."));
        }
        catch (Exception ex) when (ex is ForgeRequestException or HttpRequestException)
        {
            logger.LogWarning(ex, "Release runs: re-run of {Run} in {Repository} failed", runId, repository.FullName);
            return StatusCode(StatusCodes.Status502BadGateway, new RerunRefused("GitHub did not answer. Try again in a minute."));
        }
        cache.Remove(RunKey(repository, runId));
        logger.LogInformation("Release runs: re-ran the failed jobs of {Run} in {Repository}", runId, repository.FullName);
        return Accepted();
    }

    private const string NewerRunBlocks = "A newer run of this workflow is still going; re-running this one now would cancel it. Re-run once it has finished.";

    /// <summary>A run that ended without success, cancelled included: what <c>rerun-failed-jobs</c> restarts.</summary>
    private static bool Failed(ForgeWorkflowRun run) => run.IsCompleted && run.Conclusion is not ("success" or "skipped" or "neutral");

    private static string RunKey(Entities.Repository repository, long runId) => $"release-run/{repository.Id}/{runId}";

    private async Task<ForgeWorkflowRun?> RunAsync(Entities.Repository repository, IForgeMergeQueue queue, long runId, CancellationToken cancellationToken)
    {
        var key = RunKey(repository, runId);
        if (cache.TryGetValue(key, out ForgeWorkflowRun? run)) return run;
        run = await queue.GetWorkflowRunAsync(repository, runId, cancellationToken);
        cache.Set(key, run, run is { IsCompleted: true } ? FinishedCacheDuration : UnfinishedCacheDuration);
        return run;
    }

    private async Task<bool> NewerUnfinishedAsync(Entities.Repository repository, IForgeMergeQueue queue, ForgeWorkflowRun run, CancellationToken cancellationToken)
    {
        var key = $"release-run-newer/{repository.Id}/{run.Id}";
        if (cache.TryGetValue(key, out bool newer)) return newer;
        newer = await queue.HasNewerUnfinishedRunAsync(repository, run, cancellationToken);
        cache.Set(key, newer, UnfinishedCacheDuration);
        return newer;
    }

    /// <summary>The viewer's own forge login has write access (or more); false for anyone anonymous, unlinked or unknown.</summary>
    private async Task<bool> HasWriteAccessAsync(Entities.Repository repository, IForgeMergeQueue queue, CancellationToken cancellationToken)
    {
        if (User.Identity?.IsAuthenticated != true || accounts.FirstOrDefault(a => a.Provider == repository.Provider) is not { } access) return false;
        if ((await access.GetViewerAccountAsync(cancellationToken)).Login is not { } login) return false;

        var key = $"release-run-role/{repository.Id}/{login.ToLowerInvariant()}";
        if (cache.TryGetValue(key, out EForgeRepositoryRole role)) return role >= EForgeRepositoryRole.Write;
        try
        {
            role = await queue.GetActorRoleAsync(repository, login, cancellationToken);
            cache.Set(key, role, RoleCacheDuration);
            return role >= EForgeRepositoryRole.Write;
        }
        catch (Exception ex) when (ex is ForgeAccessDeniedException or ForgeRequestException or HttpRequestException)
        {
            logger.LogDebug(ex, "Release runs: could not read @{Login}'s role on {Repository}", login, repository.FullName);
            return false;
        }
    }

    /// <summary>The browse API's refusal (#453): anonymous 401, signed-in 404, one constant body.</summary>
    private JsonResult Refuse()
    {
        var (body, status) = SparkDenial.Refuse(HttpContext);
        return new JsonResult(body) { StatusCode = status };
    }
}
