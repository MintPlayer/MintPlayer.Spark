using CodeCoverage.Entities;
using CodeCoverage.Forge;
using CodeCoverage.Ingestion;
using CodeCoverage.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using MintPlayer.SourceGenerators.Attributes;

namespace CodeCoverage.Controllers;

/// <summary>
/// Coverage uploaded from a fork's pull request, by a runner that holds no credential for the
/// target repository — because the forge refuses to give it one (D6f).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is a separate controller rather than an action on <see cref="UploadsController"/>.</b>
/// That controller's class attributes state that a browser cookie must never reach ingestion and
/// that relaxing them is "the one change here that would fail open". An <c>[AllowAnonymous]</c>
/// action inside it would do exactly that relaxing, in a place where the next reader would have to
/// notice an attribute on one method out of five to understand that the class comment no longer
/// holds. A separate type makes the anonymous surface something you can enumerate — which is what
/// M16 needed, since <c>securityPosture.txt</c> records Spark rights only and never sees an MVC
/// attribute.
/// </para>
/// <para>
/// <b>What replaces the credential.</b> Nothing this endpoint is told is trusted. The caller names
/// a repository and a pull-request number in the route; everything else that matters — the head
/// sha, the branch, whether it really is a fork, and the target's default branch — is read back
/// from the forge and overwrites whatever was posted. The pull request is the credential: only
/// someone who actually opened it can name a number whose head sha matches the report they are
/// uploading.
/// </para>
/// <para>
/// ⚠️ <b>The target repository must have the app installed.</b> That is the consent this endpoint
/// rests on: an owner who installed the app asked us to look at their pull requests. It is also
/// what makes the forge read possible at all, and what bounds the blast radius to repositories that
/// opted in, rather than to every public repository on the forge.
/// </para>
/// <para>
/// ⚠️ <b>Nothing written here can move repository-level state.</b> Commits are stored under the
/// <c>pr/{n}/</c> id shape and stamped <see cref="Commit.ContributedFromFork"/>, which
/// <c>CommitAssembler.Promote</c>, <c>BaseResolver</c> and every branch-keyed read refuse. The flag
/// is the guarantee, not the id shape — no query parses ids.
/// </para>
/// </remarks>
[ApiController]
[Route("api/uploads/fork")]
[AllowAnonymous]
[EnableRateLimiting("fork-uploads")]
public partial class ForkUploadsController : ControllerBase
{
    [Inject] private readonly IRepositoryResolver repositories;
    [Inject] private readonly IForgeIntegrationResolver forges;
    [Inject] private readonly IUploadIngestor ingestor;
    [Inject] private readonly ILogger<ForkUploadsController> logger;

    /// <summary>Caps the COMPRESSED multipart body, as on the authenticated endpoint.</summary>
    private const long MaxReportBytes = 50 * 1024 * 1024;

    /// <summary>
    /// Deliberately an order of magnitude below the authenticated endpoint's 512. A fork uploader
    /// is unvouched-for and a pull request is one workspace, not a fanned-out matrix build.
    /// </summary>
    private const int MaxReportsPerUpload = 64;

    /// <summary>Characters in the `git ls-files` payload. Same shape as the authenticated bound, smaller.</summary>
    private const int MaxFileListChars = 1024 * 1024;

    /// <summary>
    /// Accepts coverage for a fork's pull request.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Every failure is 404 with the same body.</b> Unknown repository, app not installed,
    /// no such pull request, not a fork, and wrong sha are indistinguishable from outside. Telling
    /// them apart would turn this into an oracle for which private repositories exist and which
    /// public ones have the app installed — and the caller is anonymous by construction.
    /// </remarks>
    [HttpPost("{provider}/{owner}/{name}/pull/{number:int}")]
    [RequestSizeLimit(MaxReportBytes)]
    public async Task<IActionResult> UploadFromFork(
        string provider,
        string owner,
        string name,
        int number,
        [FromForm] ForkUploadForm form,
        CancellationToken cancellationToken)
    {
        // ⚠️ Repository and pull request travel in the ROUTE, not the body, because the rate
        // limiter partitions before model binding — a body-carried identity cannot be partitioned
        // on, so one caller could exhaust the window for every repository at once.
        if (form.Files.Count == 0)
            return NotFoundLikeEverythingElse();
        if (form.Files.Count > MaxReportsPerUpload)
            return BadRequest(new { error = $"Too many report files in one upload ({form.Files.Count}); the limit is {MaxReportsPerUpload}." });
        if (form.FileList is { Length: > MaxFileListChars })
            return BadRequest(new { error = $"The file list is too large; the limit is {MaxFileListChars} characters." });
        if (string.IsNullOrWhiteSpace(form.CommitSha) || form.CommitSha.Length < 7)
            return NotFoundLikeEverythingElse();
        if (!ForgeProviders.TryParse(provider, out var forgeProvider))
            return NotFoundLikeEverythingElse();

        // 1. The target repository must be one we already know. The resolution's Redirect flag is
        //    ignored on purpose: a 301 on an upload would have the caller re-POST its whole body,
        //    and an alias resolving to the right document is not an error worth correcting here.
        var resolution = await repositories.ResolveAsync(forgeProvider, owner, name, cancellationToken);
        if (resolution.Repository is not { } repository || repository.Connection == RepositoryConnection.Disconnected)
            return NotFoundLikeEverythingElse();

        // 2. Public only. A private repository's coverage must never be writable by someone holding
        //    no credential for it, whatever the pull request says.
        if (repository.IsPrivate)
            return NotFoundLikeEverythingElse();

        // 2b. The repository's fork budget. Checked HERE — after the repository is loaded, before
        //     the forge round trip — so an over-budget repository costs neither an outbound API call
        //     nor a single stored document. It cannot move earlier: the rate limiter runs before
        //     model binding and has no session to read a budget from.
        //
        //     ⚠️ Refused as 404 like everything else. A distinct 429 would tell an anonymous caller
        //     that the repository exists, has the app installed, and is being actively contributed
        //     to — which is exactly the information every other arm here refuses to leak.
        if (!ForkUploadBudget.HasRoom(repository.ForkUploads, DateTime.UtcNow))
        {
            logger.LogWarning(
                "Fork upload refused for {FullName}: {Count} already accepted in the current window.",
                repository.FullName, repository.ForkUploads?.Count);
            return NotFoundLikeEverythingElse();
        }

        var forge = forges.For(repository);

        // 3. Read the pull request. Null is a refusal, never an absence — see IForgeClient. This
        //    also fails when the app is not installed, which is the consent gate.
        var pull = await forge.GetPullRequestAsync(repository, number, cancellationToken);
        if (pull is null)
            return NotFoundLikeEverythingElse();

        // 4. It must actually be a fork. A same-repository pull request has a credential available
        //    and belongs on the authenticated endpoint; accepting it here would be a way to upload
        //    to your own repository while bypassing the token scope check.
        if (!pull.IsFromFork)
            return NotFoundLikeEverythingElse();

        // 5. The sha must be this pull request's current head. This is what makes the pull request
        //    serve as the credential: a caller who did not open it cannot name a number whose head
        //    matches the report they hold. It also pins the upload to a live head, so a superseded
        //    commit cannot be back-filled after the branch has moved on.
        if (!string.Equals(pull.HeadSha, form.CommitSha, StringComparison.OrdinalIgnoreCase))
            return NotFoundLikeEverythingElse();

        // 6. Learn the default branch while we have an authoritative answer. This is the field whose
        //    absence let Promote treat every branch as promotable, and this is often the only place
        //    an OIDC-provisioned repository will ever be told it.
        if (repository.DefaultBranch is null && pull.BaseRepositoryDefaultBranch is not null)
        {
            repository.DefaultBranch = pull.BaseRepositoryDefaultBranch;
            logger.LogInformation("Learned default branch {Branch} for {FullName} from pull request #{Number}.",
                pull.BaseRepositoryDefaultBranch, repository.FullName, number);
        }

        // Spent only once everything above has passed, so a refused request costs nothing from the
        // repository's allowance. The ingestor's SaveChanges persists this along with the documents
        // it writes — the budget and the thing it is paying for commit together or not at all.
        repository.ForkUploads = ForkUploadBudget.Accept(repository.ForkUploads, DateTime.UtcNow);

        var result = await ingestor.IngestAsync(new UploadIngestRequest(
            Repository: repository,
            CommitSha: pull.HeadSha,
            // ⚠️ Branch, pull-request number and base ref come from the FORGE, never from the form.
            // They decide which badge is served and what a later build compares against, so a
            // caller who chose them would be choosing the answer.
            Branch: pull.HeadRef,
            PullRequestNumber: pull.Number,
            BaseRef: pull.BaseRef,
            PrBaseSha: null,
            ParentSha: null,
            RunId: form.RunId,
            RunAttempt: form.RunAttempt,
            Workflow: form.Workflow,
            EventName: "pull_request",
            JobName: form.JobName,
            Flags: form.Flags,
            RootDir: form.RootDir,
            FileList: form.FileList,
            Partial: form.Partial,
            CarryForward: false,
            BaseSha: null,
            Files: form.Files,
            ContributedFromFork: true), cancellationToken);

        return Accepted(new { buildId = result.BuildId, sessionId = result.SessionId });
    }

    /// <summary>
    /// The single refusal. Named rather than inlined so that a reader can see there is exactly one,
    /// and so a future arm cannot quietly return a more specific error.
    /// </summary>
    private NotFoundObjectResult NotFoundLikeEverythingElse()
        => NotFound(new { error = "No such pull request, or coverage cannot be accepted for it." });

    /// <summary>
    /// The fork upload's form. Deliberately a SUBSET of the authenticated one.
    /// </summary>
    /// <remarks>
    /// ⚠️ The omissions are the design. <c>repository</c>, <c>branch</c>, <c>pullRequestNumber</c>
    /// and <c>baseRef</c> are absent because the forge supplies them; <c>parentSha</c>,
    /// <c>baseSha</c>, <c>prBaseSha</c> and <c>carryForward</c> are absent because they steer
    /// comparison and carry-forward, which fork coverage must not do. Model binding drops unknown
    /// fields silently, so an action that sends the full authenticated form is simply ignored on
    /// those — which is the intended behaviour, not a gap.
    /// </remarks>
    public sealed class ForkUploadForm
    {
        public string CommitSha { get; set; } = "";
        public long RunId { get; set; }
        public int RunAttempt { get; set; } = 1;
        public string? JobName { get; set; }
        public string? Workflow { get; set; }
        public string? Flags { get; set; }
        public string? RootDir { get; set; }
        public string? FileList { get; set; }
        public bool Partial { get; set; }
        public List<IFormFile> Files { get; set; } = [];
    }
}
