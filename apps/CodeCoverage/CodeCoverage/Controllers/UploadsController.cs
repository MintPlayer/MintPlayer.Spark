using CodeCoverage.ApiTokens;
using CodeCoverage.Entities;
using CodeCoverage.Indexes;
using CodeCoverage.Ingestion;
using CodeCoverage.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MintPlayer.Spark.Services;
using Microsoft.AspNetCore.RateLimiting;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Messaging.Abstractions;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;

namespace CodeCoverage.Controllers;

/// <summary>
/// Coverage-report ingestion. Authenticated with an upload token (ApiToken
/// scheme); the same action may POST several times per workflow run — each call
/// becomes a session on the run's Build, parsed asynchronously via the message
/// bus. 202 means "accepted for processing", never "parsed".
/// </summary>
[ApiController]
[Route("api/uploads")]
// Both attributes, and they answer different questions. [Authorize] names the two
// schemes that may authenticate here at all — a browser cookie must NOT reach
// ingestion, and dropping that restriction is the one change here that would fail
// open. [SparkAuthorize] then checks the right, so who may upload is an operator
// decision rather than a redeploy. The union of AuthenticationSchemes across both
// is still exactly these two: the second attribute names none.
[Authorize(AuthenticationSchemes = $"{ApiTokenAuthenticationHandler.SchemeName},{GitHubOidc.SchemeName}")]
[SparkAuthorize("Upload", "Coverage")]
[EnableRateLimiting("uploads")]
public partial class UploadsController : ControllerBase
{
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly IMessageBus messageBus;
    [Inject] private readonly IBaseResolver baseResolver;
    [Inject] private readonly ILogger<UploadsController> logger;
    [Inject] private readonly IConfiguration configuration;

    private const long MaxReportBytes = 50 * 1024 * 1024;

    /// <summary>
    /// The upload contract this build implements, reported by
    /// <c>GET /api/uploads/capabilities</c>.
    /// <para>
    /// Bump this ONLY for a change a client cannot absorb in silence: a field
    /// removed, renamed or repurposed, or a newly required endpoint. Adding a
    /// request field does not qualify — unknown form fields are dropped by model
    /// binding — and neither does adding a response field, because clients are
    /// required to tolerate every one of them being absent. When it does move,
    /// the previous behaviour stays for at least one deploy cycle: consumers pin
    /// a git ref and upgrade on their own schedule, so there is no moment when
    /// every client is new.
    /// </para>
    /// </summary>
    private const int UploadContract = 1;

    /// <summary>
    /// Capability names a client may branch on. Additive: a name is never
    /// removed or given a new meaning, because an old action still asks for it.
    /// Only list what is genuinely implemented — this is what a client trusts
    /// instead of trying an input to see whether it worked.
    /// </summary>
    private static readonly string[] SupportedFeatures =
    [
        "partial-uploads",   // partial + baseSha, scoped baseline, projection
        "patch-coverage",    // patch{} on the status response
        "flag-coverage",     // per-flag totals
        "gzip-reports",      // gzipped report parts, detected by magic bytes
        "oidc-auth",         // GitHubOidc scheme, audience = Coverage:BaseUrl
    ];

    public sealed record UploadResponse(string BuildId, string SessionId);

    /// <param name="Contract">
    /// The contract version. A client comparing this against its own must treat a
    /// server that is <em>ahead</em> as fine (every change is additive from the
    /// client's side) and a server that is <em>behind</em> as a reason to degrade,
    /// never to fail.
    /// </param>
    /// <param name="Features">Names from <see cref="SupportedFeatures"/>.</param>
    public sealed record CapabilitiesResponse(int Contract, string[] Features);

    [HttpPost]
    [RequestSizeLimit(MaxReportBytes)]
    public async Task<ActionResult<UploadResponse>> Upload([FromForm] UploadForm form, CancellationToken cancellationToken)
    {
        if (form.Files.Count == 0)
            return BadRequest(new { error = "No coverage report files in the upload." });
        if (string.IsNullOrWhiteSpace(form.Repository) || !form.Repository.Contains('/'))
            return BadRequest(new { error = "repository must be owner/name." });
        if (string.IsNullOrWhiteSpace(form.CommitSha) || form.CommitSha.Length < 7)
            return BadRequest(new { error = "commitSha is required (full SHA preferred)." });

        var repository = await ResolveAuthorizedRepository(form.Repository, provision: true, cancellationToken);
        if (repository is null)
            return NotFound(new { error = $"Repository '{form.Repository}' is unknown here (is the GitHub App installed?) or the token doesn't grant it." });

        // OIDC claims are GitHub-signed and unforgeable — they override the
        // body's copies so a workflow can't attach its coverage to someone
        // else's run. The `sha` claim is NOT used: on pull_request events it
        // is the ephemeral merge commit, while the body carries the PR head.
        if (long.TryParse(User.FindFirst(GitHubOidc.RunIdClaim)?.Value, out var claimRunId))
            form.RunId = claimRunId;
        if (int.TryParse(User.FindFirst(GitHubOidc.RunAttemptClaim)?.Value, out var claimRunAttempt))
            form.RunAttempt = claimRunAttempt;

        var commitId = Entities.Commit.DocumentId(repository.GitHubId, form.CommitSha);
        var commit = await session.LoadAsync<Commit>(commitId, cancellationToken);
        if (commit is null)
        {
            commit = new Commit { Sha = form.CommitSha, Repository = repository.Id, FirstSeenAtUtc = DateTimeOffset.UtcNow };
            await session.StoreAsync(commit, commitId, cancellationToken);
        }
        commit.Branch ??= form.Branch;
        commit.PullRequestNumber ??= form.PullRequestNumber;
        commit.ParentSha ??= form.ParentSha;

        var buildId = Build.DocumentId(repository.GitHubId, form.CommitSha, form.RunId, form.RunAttempt);
        var build = await session.LoadAsync<Build>(buildId, cancellationToken);
        if (build is null)
        {
            build = new Build
            {
                Commit = commitId,
                CiRunId = form.RunId,
                CiRunAttempt = form.RunAttempt,
                Run = Build.ComposeRun(form.RunId, form.RunAttempt),
                WorkflowName = form.Workflow,
                EventName = form.EventName,
                CreatedAtUtc = DateTime.UtcNow,
            };
            await session.StoreAsync(build, buildId, cancellationToken);
        }
        else if (build.Status == "Finalized")
        {
            // A late upload re-opens the build; the finalizer will close it again
            // and recompute — max-merge keeps this correct.
            build.Status = "Open";
            build.FinalizedAtUtc = null;
            build.FinalizeReason = null;
        }

        // One partial job makes the whole build partial: the totals under-count
        // the workspace regardless of what the other jobs measured. The declared
        // base is fixed by the first job that names one (all jobs of a run pass
        // the same inputs; ??= just makes a disagreeing straggler harmless).
        build.Partial |= form.Partial;
        build.DeclaredBaseSha ??= form.BaseSha;
        // One job whose tests failed disables carry-forward for the whole build.
        build.CarryForward &= form.CarryForward ?? true;

        var sessionId = Guid.NewGuid().ToString("N")[..12];
        var buildSession = new BuildSession
        {
            SessionId = sessionId,
            JobName = form.JobName,
            Flags = (form.Flags ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            UploadedAtUtc = DateTime.UtcNow,
            RootDir = form.RootDir,
        };

        var attachmentNames = new List<string>();
        var index = 0;
        foreach (var file in form.Files)
        {
            var name = UploadAttachments.ReportName(sessionId, index++, file.FileName);
            session.Advanced.Attachments.Store(build, name, file.OpenReadStream());
            attachmentNames.Add(name);
        }
        if (!string.IsNullOrEmpty(form.FileList))
        {
            session.Advanced.Attachments.Store(build, UploadAttachments.FileListName(sessionId),
                new MemoryStream(System.Text.Encoding.UTF8.GetBytes(form.FileList)));
        }

        buildSession.RawFileNames = [.. attachmentNames];
        build.Sessions.Add(buildSession);
        build.LastUploadAtUtc = DateTime.UtcNow;

        await session.SaveChangesAsync(cancellationToken);
        await messageBus.BroadcastAsync(new ParseSessionMessage { BuildId = buildId, SessionId = sessionId }, cancellationToken);

        logger.LogInformation("Accepted upload for {Repo}@{Sha} run {RunId}.{Attempt} session {SessionId} ({Files} files)",
            form.Repository, form.CommitSha, form.RunId, form.RunAttempt, sessionId, form.Files.Count);

        return Accepted(new UploadResponse(buildId, sessionId));
    }

    /// <summary>Explicitly closes the run's build instead of waiting for the debounce.</summary>
    [HttpPost("finish")]
    public async Task<IActionResult> Finish([FromBody] FinishRequest request, CancellationToken cancellationToken)
    {
        var repository = await ResolveAuthorizedRepository(request.Repository, provision: false, cancellationToken);
        if (repository is null)
            return NotFound();

        var buildId = Build.DocumentId(repository.GitHubId, request.CommitSha, request.RunId, request.RunAttempt);
        var build = await session.LoadAsync<Build>(buildId, cancellationToken);
        if (build is null)
            return NotFound();

        // Finalization rides the parse queue (FIFO): it runs after every parse
        // enqueued before this call, so it can never promote a stale summary or
        // race a parse's save.
        await messageBus.BroadcastAsync(new FinalizeBuildMessage { BuildId = buildId }, cancellationToken);
        return Accepted(new { status = "Finalizing" });
    }

    /// <summary>
    /// What this deployment can do, so a newer action talking to an older image
    /// can find out rather than guess.
    /// <para>
    /// The action is consumed from a git ref; this server ships as a docker image
    /// the VPS pulls. Those clocks are independent — even an action and a server
    /// built from the same commit are not guaranteed to meet — so "same
    /// repository" is not a compatibility mechanism and this endpoint is.
    /// </para>
    /// <para>
    /// A client MUST treat <b>404 as contract 0</b>: that is precisely what every
    /// image deployed before this endpoint existed answers, which is what makes
    /// an old image self-describing without being modified. Absence is the
    /// baseline, never an error.
    /// </para>
    /// </summary>
    [HttpGet("capabilities")]
    // The uploads policy is sized for 50 MB payloads at 60/minute; a probe that
    // rides in front of every upload would eat that budget. Same partition key.
    [EnableRateLimiting("uploads-status")]
    public ActionResult<CapabilitiesResponse> Capabilities()
        => Ok(new CapabilitiesResponse(UploadContract, SupportedFeatures));

    /// <summary>
    /// How did a workflow run turn out? The endpoint a CI gate polls — and the
    /// reason it exists rather than pointing consumers at <c>/api/browse</c>:
    /// browse authorizes against a signed-in human's GitHub access, so no CI
    /// credential can read a private repository through it, and it cannot tell
    /// "no build yet" apart from "not allowed".
    /// <para>
    /// Documented in <c>docs/code-coverage/upload-api.md</c>, which is a
    /// compatibility promise: fields are added here, never removed or repurposed.
    /// A change that cannot honour that promise bumps
    /// <see cref="UploadContract"/> and keeps the old behaviour for a deploy
    /// cycle; see <c>GET /api/uploads/capabilities</c>.
    /// </para>
    /// </summary>
    [HttpGet("status")]
    // Overrides the controller's "uploads" policy (action-level metadata wins):
    // that one allows 60/minute because it is sized for 50 MB payloads, and a
    // gate polling every 5 seconds from several jobs of one workflow would
    // exhaust it — throttling the poll *and* starving the uploads sharing its
    // per-token bucket. Same partition key, limit sized for polling.
    [EnableRateLimiting("uploads-status")]
    public async Task<ActionResult<UploadStatusResponse>> Status(
        [FromQuery] string repository, [FromQuery] string commitSha,
        [FromQuery] long runId, [FromQuery] int runAttempt = 1,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(repository) || !repository.Contains('/'))
            return BadRequest(new { error = "repository must be owner/name." });
        if (string.IsNullOrWhiteSpace(commitSha))
            return BadRequest(new { error = "commitSha is required." });

        // provision: false — a GET must not have side effects. The upload path
        // auto-provisions a public repository from an OIDC claim (registering is
        // what an upload *is*); doing that here would let a poll for a
        // repository that never uploaded quietly create it.
        var repo = await ResolveAuthorizedRepository(repository, provision: false, cancellationToken);
        if (repo is null)
            return NotFound();

        var buildId = Build.DocumentId(repo.GitHubId, commitSha, runId, runAttempt);
        var build = await session.LoadAsync<Build>(buildId, cancellationToken);
        if (build is null)
        {
            // Authorization is already proven, so this one is safe to spell out —
            // and it is the distinction a poller needs, because it means "you
            // asked the wrong question", never "keep waiting". Anti-enumeration
            // is preserved above, where the repository itself 404s bare.
            return NotFound(new { error = $"No build for run {runId}.{runAttempt} on {commitSha}." });
        }

        var commit = build.Commit is null ? null : await session.LoadAsync<Commit>(build.Commit, cancellationToken);
        var assembly = build.Commit is null ? null : await session.LoadAsync<CommitAssembly>(CommitAssembly.DocumentId(build.Commit), cancellationToken);
        var baseUrl = configuration["Coverage:BaseUrl"]?.TrimEnd('/');

        // A partial build's numbers are honest only against a like-for-like
        // base; a whole build keeps the original whole-workspace baseline (D9).
        var (baseline, baselineScope, projection) = build.Partial
            ? await ResolvePartialComparison(repo, build, commit, cancellationToken)
            : (await ResolveBaseline(repo, commit, cancellationToken), null, null);

        return Ok(new UploadStatusResponse(
            buildId,
            Build.ClassifyState(build),
            build.Status,
            build.FinalizeReason,
            build.CreatedAtUtc,
            build.FinalizedAtUtc,
            build.Coverage,
            baseline,
            [.. build.Sessions.Select(s => new UploadStatusSession(
                s.SessionId, s.JobName, s.Flags, s.ParseStatus, s.Error, s.FilesCount))],
            baseUrl is null ? null : $"{baseUrl}/r/{repo.FullName}/c/{commitSha}",
            build.Partial,
            baselineScope,
            projection,
            build.Patch,
            build.FlagCoverage,
            build.FeedbackState,
            assembly is null ? null : new UploadStatusAssembly(
                assembly.Coverage,
                assembly.Completeness,
                [.. assembly.IncompleteReasons],
                assembly.MeasuredFiles,
                assembly.CarriedFiles,
                assembly.UnmeasuredFiles,
                assembly.BaseSha,
                assembly.BaseResolution,
                assembly.OldestOriginSha,
                [.. assembly.Builds.Select(b => b.BuildId)],
                assembly.AssembledAtUtc)));
    }

    /// <summary>
    /// Everything a partial build's comparison can honestly say, in three
    /// pieces: a scoped <c>baseline</c> (the base restricted to the measured
    /// paths), a <c>baselineScope</c> stating exactly which base resolved and
    /// how far it strayed from what was declared, and a whole-workspace
    /// <c>projection</c> carrying its own completeness verdict. Numbers appear
    /// only once the head build is finalized (its tree summary exists); until
    /// then the scope still reports what the base would be. Never throws for a
    /// missing base — abstaining is the routine case (#11 SP3).
    /// </summary>
    private async Task<(UploadStatusBaseline? Baseline, UploadStatusBaselineScope? Scope, UploadStatusProjection? Projection)>
        ResolvePartialComparison(Repository repo, Build build, Commit? commit, CancellationToken cancellationToken)
    {
        var result = await BuildComparer.CompareAsync(session, baseResolver, repo, build, commit, cancellationToken);
        var resolved = result.Base;

        var scope = new UploadStatusBaselineScope("scoped",
            resolved.RequestedSha, resolved.ResolvedSha, resolved.Mode,
            result.Partial?.FilesInScope, result.Partial?.PrunedFiles);

        if (result.Partial is null)
            return (null, scope, null);

        return (
            new UploadStatusBaseline(resolved.ResolvedSha!, resolved.Branch, result.Partial.ScopedBaseline),
            scope,
            new UploadStatusProjection(result.Partial.Projection, result.IncompleteReasons.Length == 0, result.IncompleteReasons));
    }

    /// <summary>
    /// The number a ratchet compares against: the newest finalized coverage on
    /// the default branch that isn't the commit being polled.
    /// <para>
    /// It deliberately does not read <see cref="Repository.LatestCoverage"/>,
    /// which holds exactly this and would cost nothing. On a push-to-default
    /// gate the finalize that makes the build terminal is the same finalize that
    /// overwrites that field with this very commit — so the poller would compare
    /// the build against itself and every ratchet would pass. The exclusion has
    /// to happen in the query.
    /// </para>
    /// </summary>
    private async Task<UploadStatusBaseline?> ResolveBaseline(Repository repo, Commit? commit, CancellationToken cancellationToken)
    {
        // Repositories auto-provisioned by an OIDC upload never learn their
        // default branch (only the webhooks set it), and that is precisely the
        // population uploading without installing the App — so fall back to the
        // polled commit's own branch rather than returning no baseline at all.
        var branch = repo.DefaultBranch ?? commit?.Branch;

        var query = session.Query<Commits_ByRepository.Result, Commits_ByRepository>()
            .Where(r => r.Repository == repo.Id && r.HasCoverage);
        if (branch is not null)
            query = query.Where(r => r.Branch == branch);

        // Take 2: the newest may be the commit being polled.
        var candidates = await query
            .OrderByDescending(r => r.AuthoredAt)
            .OfType<Commit>()
            .Take(2)
            .ToListAsync(cancellationToken);

        var baseline = candidates.FirstOrDefault(c => !string.Equals(c.Sha, commit?.Sha, StringComparison.OrdinalIgnoreCase));
        return baseline is null ? null : new UploadStatusBaseline(baseline.Sha, baseline.Branch, baseline.Coverage);
    }

    public sealed record UploadStatusResponse(
        string BuildId,
        string State,
        string Status,
        string? FinalizeReason,
        DateTime CreatedAtUtc,
        DateTime? FinalizedAtUtc,
        CoverageSummary? Coverage,
        UploadStatusBaseline? Baseline,
        IReadOnlyList<UploadStatusSession> Sessions,
        string? CommitUrl,
        bool Partial = false,
        UploadStatusBaselineScope? BaselineScope = null,
        UploadStatusProjection? Projection = null,
        PatchCoverage? Patch = null,
        IReadOnlyDictionary<string, CoverageSummary>? Flags = null,
        string? FeedbackState = null,
        UploadStatusAssembly? Assembly = null);

    /// <summary>
    /// The commit-level record: the union of every finalized build of the
    /// commit plus files carried from the base where the git blob is unchanged.
    /// <c>Coverage</c> here is the commit's headline; the response's top-level
    /// <c>coverage</c> stays what this build alone measured. Null until the
    /// first build of the commit finalized (assembly follows finalize on the
    /// same queue) and for commits that predate assemblies.
    /// </summary>
    public sealed record UploadStatusAssembly(
        CoverageSummary Coverage, string Completeness, string[] IncompleteReasons,
        int MeasuredFiles, int CarriedFiles, int UnmeasuredFiles,
        string? BaseSha, string? BaseResolution, string? OldestOriginSha,
        string[] Builds, DateTime AssembledAtUtc);

    public sealed record UploadStatusBaseline(string Sha, string? Branch, CoverageSummary? Coverage);

    /// <summary>
    /// States what the partial comparison's denominator actually is: <c>Mode</c>
    /// is "scoped" (the only value yet — "whole" builds carry no scope object),
    /// <c>BaseResolution</c> is exact | mergeBase | walked | none, and the two
    /// shas make any substitution visible. Null counts mean "not computed yet".
    /// </summary>
    public sealed record UploadStatusBaselineScope(
        string Mode, string? RequestedBaseSha, string? ResolvedBaseSha, string BaseResolution,
        int? FilesInScope, int? PrunedFiles);

    /// <summary>
    /// The patched whole-workspace projection with its completeness verdict
    /// (reasons: baseWalked | noFileList | unmatchedPaths | parseErrors). An
    /// incomplete projection is a best-effort reconstruction — the UI shows a
    /// danger badge and a gate may choose to abstain.
    /// </summary>
    public sealed record UploadStatusProjection(CoverageSummary Coverage, bool Complete, string[] IncompleteReasons);

    public sealed record UploadStatusSession(
        string SessionId, string? JobName, string[] Flags, string ParseStatus, string? Error, int FilesCount);

    public sealed class UploadForm
    {
        public required string Repository { get; set; }
        public required string CommitSha { get; set; }
        public string? Branch { get; set; }
        public int? PullRequestNumber { get; set; }
        public string? ParentSha { get; set; }
        public long RunId { get; set; }
        public int RunAttempt { get; set; } = 1;
        public string? JobName { get; set; }
        public string? Workflow { get; set; }
        public string? EventName { get; set; }
        public string? Flags { get; set; }
        public string? RootDir { get; set; }
        public string? FileList { get; set; }
        public bool Partial { get; set; }
        public string? BaseSha { get; set; }
        /// <summary>Absent means true: only an explicit <c>false</c> (tests failed) disables carry-forward.</summary>
        public bool? CarryForward { get; set; }
        public IFormFileCollection Files { get; set; } = new FormFileCollection();
    }

    public sealed record FinishRequest(string Repository, string CommitSha, long RunId, int RunAttempt);

    /// <param name="provision">
    /// Whether an unknown public repository may be created from the OIDC claim.
    /// True for an upload — registering the repository is part of what an upload
    /// means. False for every read: a GET must not create documents.
    /// </param>
    private async Task<Repository?> ResolveAuthorizedRepository(string fullName, bool provision, CancellationToken cancellationToken)
    {
        // OIDC path: the GitHub-signed `repository` claim IS the authorization —
        // a workflow can only ever upload for the repository it runs in.
        var oidcRepository = User.FindFirst(GitHubOidc.RepositoryClaim)?.Value;
        if (oidcRepository is not null)
        {
            if (!string.Equals(oidcRepository, fullName, StringComparison.OrdinalIgnoreCase))
                return null;
            return await ResolveOidcRepository(provision, cancellationToken);
        }

        var repository = await session.Query<Repository, Indexes.Repositories_Overview>()
            .Where(r => r.FullName == fullName)
            .FirstOrDefaultAsync(cancellationToken);
        if (repository is null)
            return null;

        var scope = User.FindFirst(ApiTokenAuthenticationHandler.ScopeClaim)?.Value;
        var account = User.FindFirst(ApiTokenAuthenticationHandler.AccountClaim)?.Value;
        var repoId = User.FindFirst(ApiTokenAuthenticationHandler.RepositoryClaim)?.Value;

        var authorized = scope switch
        {
            "Account" => string.Equals(account, repository.OwnerLogin, StringComparison.OrdinalIgnoreCase),
            "Repository" => repoId == repository.GitHubId.ToString(),
            _ => false,
        };

        // Unknown and unauthorized look identical to the caller (no existence leak).
        return authorized ? repository : null;
    }

    /// <summary>
    /// Loads the OIDC caller's repository; public repositories auto-provision on
    /// first upload (no App installation needed), private ones must already be
    /// known via the GitHub App.
    /// </summary>
    private async Task<Repository?> ResolveOidcRepository(bool provision, CancellationToken cancellationToken)
    {
        if (!long.TryParse(User.FindFirst(GitHubOidc.RepositoryIdClaim)?.Value, out var gitHubRepoId))
            return null;

        var repository = await session.LoadAsync<Repository>(Repository.DocumentId(gitHubRepoId), cancellationToken);
        if (repository is not null)
            return repository;

        if (!provision)
            return null;

        if (User.FindFirst(GitHubOidc.RepositoryVisibilityClaim)?.Value != "public")
            return null;

        var fullName = User.FindFirst(GitHubOidc.RepositoryClaim)!.Value;
        var ownerLogin = User.FindFirst(GitHubOidc.RepositoryOwnerClaim)?.Value ?? fullName.Split('/')[0];

        Account? account = null;
        if (long.TryParse(User.FindFirst(GitHubOidc.RepositoryOwnerIdClaim)?.Value, out var ownerId))
        {
            account = await session.LoadAsync<Account>(Account.DocumentId(ownerId), cancellationToken);
            if (account is null)
            {
                account = new Account { GitHubId = ownerId, Login = ownerLogin };
                await session.StoreAsync(account, Account.DocumentId(ownerId), cancellationToken);
            }
        }

        repository = new Repository
        {
            GitHubId = gitHubRepoId,
            Account = account?.Id,
            Name = fullName.Split('/')[1],
            FullName = fullName,
            OwnerLogin = ownerLogin,
            IsPrivate = false,
        };
        await session.StoreAsync(repository, Repository.DocumentId(gitHubRepoId), cancellationToken);
        logger.LogInformation("Auto-provisioned public repository {FullName} from OIDC upload", fullName);
        return repository;
    }
}
