using CodeCoverage.ApiTokens;
using CodeCoverage.Entities;
using CodeCoverage.Forge;
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

    /// <summary>
    /// The OIDC vocabulary this request's token speaks.
    /// </summary>
    /// <remarks>
    /// ⚠️ Temporary: fixed to GitHub because it is the only scheme registered. When a second forge
    /// registers one, this selects by the authenticated scheme
    /// (<c>User.Identity.AuthenticationType</c>) rather than assuming — and it is a single
    /// expression precisely so that change is one line rather than eleven claim lookups.
    /// </remarks>
    private static ForgeOidcProfile Oidc => ForgeOidcProfile.GitHub;
    [Inject] private readonly IRepositoryResolver repositories;
    [Inject] private readonly IMessageBus messageBus;
    [Inject] private readonly IBaseResolver baseResolver;
    [Inject] private readonly ILogger<UploadsController> logger;
    [Inject] private readonly IConfiguration configuration;
    [Inject] private readonly IUploadIngestor ingestor;

    /// <summary>Caps the COMPRESSED multipart body. The decompressed bound lives in the parser.</summary>
    private const long MaxReportBytes = 50 * 1024 * 1024;

    /// <summary>
    /// Reports accepted in one upload. Each costs an attachment store here and a parse
    /// later, and they are small on the wire — a gzipped cobertura is a few KB — so the
    /// byte limit alone does not bound the work. The largest real upload seen is a
    /// monorepo sending one report per project, in the low hundreds.
    /// </summary>
    private const int MaxReportsPerUpload = 512;

    /// <summary>
    /// Characters in the `git ls-files` payload. It is held in memory and becomes two
    /// lookups per parsed session. 8M characters is roughly 100k paths, comfortably
    /// above any real repository.
    /// </summary>
    private const int MaxFileListChars = 8 * 1024 * 1024;

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
        "carry-forward",     // commit assembly: fileList with blob OIDs, carryForward, zero-report partial uploads, assembly{} on status
        "pr-base-ref",       // baseRef + prBaseSha: the branch a PR targets and its tip
        // #417. Two guarantees a client may rely on once this is advertised:
        // per-report ingest outcomes (ingest{} and sessions[].reports[], naming which
        // file was rejected and why), and a CoverageSummary on EVERY terminal build —
        // zeroed rather than null — so files-count is "0" and never the empty string.
        "ingest-outcomes",
        // #420. Branch data from every supported format merges into one result,
        // and the stored result does not depend on the order reports arrive in,
        // so a client may upload several formats for one commit without one
        // silently shadowing another. Also implies Clover and Istanbul parse.
        "cross-format-branches",
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
        // A partial upload with nothing to report is legitimate (every project
        // cached or unaffected): it still carries the file list the assembler
        // needs to fill the commit in from the base.
        if (form.Files.Count == 0 && !(form.Partial && !string.IsNullOrWhiteSpace(form.FileList)))
            return BadRequest(new { error = "No coverage report files in the upload." });
        if (string.IsNullOrWhiteSpace(form.Repository) || !form.Repository.Contains('/'))
            return BadRequest(new { error = "repository must be owner/name." });
        if (string.IsNullOrWhiteSpace(form.CommitSha) || form.CommitSha.Length < 7)
            return BadRequest(new { error = "commitSha is required (full SHA preferred)." });
        // Bound the work before doing any of it (#417). MaxReportBytes caps the
        // compressed body; these cap the shapes that are small on the wire and
        // expensive afterwards — one attachment store and one parse per file, and a
        // file list that is held in memory and turned into two lookups per session.
        if (form.Files.Count > MaxReportsPerUpload)
            return BadRequest(new { error = $"Too many report files in one upload ({form.Files.Count}); the limit is {MaxReportsPerUpload}. Split the upload across sessions." });
        if (form.FileList is { Length: > MaxFileListChars })
            return BadRequest(new { error = $"The file list is too large ({form.FileList.Length} characters); the limit is {MaxFileListChars}." });

        var repository = await ResolveAuthorizedRepository(form.Repository, provision: true, cancellationToken);
        if (repository is null)
            return NotFound(new { error = $"Repository '{form.Repository}' is unknown here (is the GitHub App installed?) or the token doesn't grant it." });

        // OIDC claims are GitHub-signed and unforgeable — they override the
        // body's copies so a workflow can't attach its coverage to someone
        // else's run. The `sha` claim is NOT used: on pull_request events it
        // is the ephemeral merge commit, while the body carries the PR head.
        if (long.TryParse(User.FindFirst(Oidc.RunIdClaim)?.Value, out var claimRunId))
            form.RunId = claimRunId;
        if (int.TryParse(User.FindFirst(Oidc.RunAttemptClaim!)?.Value, out var claimRunAttempt))
            form.RunAttempt = claimRunAttempt;

        var result = await ingestor.IngestAsync(new UploadIngestRequest(
            Repository: repository,
            CommitSha: form.CommitSha,
            Branch: form.Branch,
            PullRequestNumber: form.PullRequestNumber,
            BaseRef: form.BaseRef,
            PrBaseSha: form.PrBaseSha,
            ParentSha: form.ParentSha,
            RunId: form.RunId,
            RunAttempt: form.RunAttempt,
            Workflow: form.Workflow,
            EventName: form.EventName,
            JobName: form.JobName,
            Flags: form.Flags,
            RootDir: form.RootDir,
            FileList: form.FileList,
            Partial: form.Partial,
            CarryForward: form.CarryForward,
            BaseSha: form.BaseSha,
            Files: form.Files,
            // First-party by construction: this action authenticated the caller against this
            // repository. The fork path is a different controller precisely so that this stays a
            // literal rather than something derived from the request.
            ContributedFromFork: false), cancellationToken);

        return Accepted(new UploadResponse(result.BuildId, result.SessionId));
    }

    /// <summary>Explicitly closes the run's build instead of waiting for the debounce.</summary>
    [HttpPost("finish")]
    public async Task<IActionResult> Finish([FromBody] FinishRequest request, CancellationToken cancellationToken)
    {
        var repository = await ResolveAuthorizedRepository(request.Repository, provision: false, cancellationToken);
        if (repository is null)
            return NotFound();

        var buildId = Build.DocumentId(EForgeProvider.GitHub, repository.GitHubId, request.CommitSha, request.RunId, request.RunAttempt);
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

        var buildId = Build.DocumentId(EForgeProvider.GitHub, repo.GitHubId, commitSha, runId, runAttempt);
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

        var unmatched = await ResolveUnmatched(buildId, cancellationToken);

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
                s.SessionId, s.JobName, s.Flags, s.ParseStatus, s.Error, s.FilesCount,
                [.. s.Reports.Select(ToStatusReport)]))],
            baseUrl is null ? null : $"{baseUrl}/{repo.Provider.ToCanonicalString()}/r/{repo.FullName}/c/{commitSha}",
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
                assembly.AssembledAtUtc),
            unmatched,
            ResolveIngest(build)));
    }

    private static UploadStatusReport ToStatusReport(ReportIngestOutcome outcome)
        => new(outcome.FileName, outcome.Parsed, outcome.Format, outcome.FilesCount, outcome.Reason, outcome.Detail);

    /// <summary>
    /// The build-level ingest verdict, unioned across sessions. Always present, so a
    /// consumer can tell "nothing was rejected" from "this server does not report it"
    /// — the distinction that made #415 take a day.
    /// </summary>
    private static UploadStatusIngest ResolveIngest(Build build)
    {
        var reports = build.Sessions.SelectMany(s => s.Reports).ToList();
        var rejected = reports.Where(r => !r.Parsed).ToList();
        return new UploadStatusIngest(
            reports.Count - rejected.Count,
            rejected.Count,
            [.. rejected.Select(ToStatusReport).Take(UnmatchedSampleSize)]);
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

        // Never ratchet against fork-contributed coverage — same reasoning as BaseResolver's
        // chokepoint, and this baseline is computed independently of it.
        var query = session.Query<Commits_ByRepository.Result, Commits_ByRepository>()
            .Where(r => r.Repository == repo.Id && r.HasCoverage && r.ContributedFromFork != true);
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

    /// <summary>Bound on the unmatched sample, matching the browse endpoint's.</summary>
    private const int UnmatchedSampleSize = 50;

    /// <summary>
    /// Reads the build's materialized tree and counts what failed to resolve.
    /// Returns null before finalize — the tree does not exist yet, and reporting
    /// zero unmatched then would be a lie a poller could act on.
    /// </summary>
    private async Task<UploadStatusUnmatched?> ResolveUnmatched(string buildId, CancellationToken cancellationToken)
    {
        var tree = await session.LoadAsync<BuildTreeSummary>(
            BuildTreeSummary.DocumentId(buildId), cancellationToken);
        if (tree is null) return null;

        var unmatched = tree.Files.Where(f => !f.Matched).ToList();
        return new UploadStatusUnmatched(
            unmatched.Count,
            tree.Files.Count,
            [.. unmatched.Select(f => f.Path).Take(UnmatchedSampleSize)]);
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
        UploadStatusAssembly? Assembly = null,
        UploadStatusUnmatched? Unmatched = null,
        UploadStatusIngest? Ingest = null);

    /// <summary>
    /// How many of the build's files could not be resolved to a repository path,
    /// with a bounded sample of them.
    /// <para>
    /// An unmatched file is retained but excluded from the build's summary, so a
    /// build whose files all failed to match is indistinguishable, from the
    /// outside, from a healthy one: accepted, finalized, and empty. That silence
    /// is what made issue #415 an investigation rather than a five-minute fix.
    /// The uploader is the right audience — it is the only party that can still
    /// do something about it while the run is in progress — so the counts travel
    /// here rather than only in <c>projection.incompleteReasons</c>, where they
    /// were computed but never surfaced for the plain whole-upload case.
    /// </para>
    /// <para>
    /// Null when the build has not finalized yet, since the tree it is derived
    /// from is materialized at finalize. Null is "not known", never "none".
    /// </para>
    /// </summary>
    public sealed record UploadStatusUnmatched(int Files, int TotalFiles, string[] Sample);

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
        string SessionId, string? JobName, string[] Flags, string ParseStatus, string? Error, int FilesCount,
        UploadStatusReport[] Reports);

    /// <summary>
    /// What happened to one uploaded report file (#417). A rejection names the file
    /// and a reason from a closed set — <c>empty | unrecognizedFormat | malformed |
    /// truncated | tooLarge | noFiles | missing</c> — so a consumer can turn "5 of 6
    /// ingested" into a specific warning instead of discovering a blank page.
    /// Empty for sessions ingested before this existed.
    /// </summary>
    public sealed record UploadStatusReport(
        string FileName, bool Parsed, string? Format, int FilesCount, string? Reason, string? Detail);

    /// <summary>
    /// The build-level ingest verdict: how many uploaded reports were accepted and
    /// how many were rejected, with the rejections themselves. Present on every
    /// build, so "zero reports rejected" and "we did not look" are distinguishable.
    /// </summary>
    public sealed record UploadStatusIngest(
        int ReportsAccepted, int ReportsRejected, UploadStatusReport[] Rejected);

    public sealed class UploadForm
    {
        public required string Repository { get; set; }
        public required string CommitSha { get; set; }
        public string? Branch { get; set; }
        public int? PullRequestNumber { get; set; }
        /// <summary>The branch the PR targets, from GITHUB_BASE_REF. Absent on pushes.</summary>
        public string? BaseRef { get; set; }
        /// <summary>
        /// Tip of the target branch, from <c>pull_request.base.sha</c>. Distinct from
        /// <see cref="BaseSha"/>, which is the caller's declared affected-computation base.
        /// </summary>
        public string? PrBaseSha { get; set; }
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
        var oidcRepository = User.FindFirst(Oidc.RepositoryClaim)?.Value;
        if (oidcRepository is not null)
        {
            if (!string.Equals(oidcRepository, fullName, StringComparison.OrdinalIgnoreCase))
                return null;
            return await ResolveOidcRepository(provision, cancellationToken);
        }

        // Resolved, so a CI workflow whose repository was renamed keeps uploading against the name
        // still written in its config. The scope check below is unaffected: it compares the
        // token's claims against the resolved repository, not against the name that was asked for.
        var nameParts = fullName.Split('/');
        if (nameParts.Length != 2)
            return null;
        // ⚠️ Explicit, not defaulted. Uploads arrive with a repository full name and a
        // credential, and neither carries a forge today — GitHub is the only integration that can
        // authenticate an upload at all. When a second one can, the forge must come from the
        // credential rather than from here (M16), which is why this names GitHub out loud instead
        // of letting a default decide.
        var repository = (await repositories.ResolveAsync(
            EForgeProvider.GitHub, nameParts[0], nameParts[1], cancellationToken)).Repository;
        if (repository is null)
            return null;

        var scope = User.FindFirst(ApiTokenAuthenticationHandler.ScopeClaim)?.Value;
        var account = User.FindFirst(ApiTokenAuthenticationHandler.AccountClaim)?.Value;

        // ⚠️ FindAll, not FindFirst. A token may be scoped to several repositories, and each is its
        // own claim — FindFirst would silently authorize only one of them and refuse the rest.
        var repoIds = User.FindAll(ApiTokenAuthenticationHandler.RepositoryClaim)
            .Select(c => c.Value)
            .ToArray();

        // Account scope compares numeric owner ids when the token carries one. A login comparison
        // is wrong in both directions once a repository is transferred: the old owner's token keeps
        // working for a repository they no longer own, and the new owner's does not work for one
        // they do. Tokens issued before the id existed fall back to the login, so a deploy
        // invalidates nothing.
        var accountId = User.FindFirst(ApiTokenAuthenticationHandler.AccountIdClaim)?.Value;

        // ⚠️ The forge comes from the TOKEN, not from a literal. A numeric account id is unique only
        // within a forge, so comparing one against a GitHub-shaped document id — which this did until
        // 2026-09-22 — authorizes a token against whichever forge the code assumed. An unparseable or
        // absent provider fails the match rather than defaulting, because defaulting is the bug.
        var tokenProvider = ForgeProviders.TryParse(
            User.FindFirst(ApiTokenAuthenticationHandler.ProviderClaim)?.Value, out var parsedProvider)
            ? parsedProvider
            : (EForgeProvider?)null;

        var authorized = scope switch
        {
            "Account" when accountId is not null =>
                tokenProvider is { } provider
                && long.TryParse(accountId, out var ownerId)
                && repository.Account == Entities.Account.DocumentId(provider, ownerId),
            // ⚠️ `OwnerKey`, not `OwnerLogin` — both sides qualified. Comparing bare logins unions
            // forges, so a GitLab group called `acme` would authorize uploads to GitHub's `acme`.
            // `Repository.OwnerKey` is computed from the repository's own `Provider`, so the two
            // sides can only match when the forge matches too.
            "Account" => string.Equals(account, repository.OwnerKey, StringComparison.OrdinalIgnoreCase),
            // Membership, not equality — the claims carry document ids, one per repository the
            // token was scoped to.
            "Repository" => repoIds.Contains(
                Entities.Repository.DocumentId(EForgeProvider.GitHub, repository.GitHubId), StringComparer.Ordinal),
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
        if (!long.TryParse(User.FindFirst(Oidc.RepositoryIdClaim)?.Value, out var gitHubRepoId))
            return null;

        var repository = await session.LoadAsync<Repository>(Repository.DocumentId(EForgeProvider.GitHub, gitHubRepoId), cancellationToken);
        if (repository is not null)
        {
            // Gated on `provision`, which is true only on the upload itself. The status endpoint is
            // a GET and resolves through here too; reconnecting from a poll would make a read
            // mutate, and worse, silently — a GET saves nothing, so the change would appear to
            // work and then vanish. "An upload reconnects" means an upload.
            if (!provision)
                return repository;

            // A workflow that still runs and still uploads is proof the repository is alive and
            // ours, and it is the only such proof for one that moved to an owner where the App is
            // not installed. So an upload reconnects, symmetrically with the reconciler's
            // disconnect-on-absence. The OIDC claims are GitHub-signed and current, so they are
            // also the freshest name we will get.
            if (repository.Connection == RepositoryConnection.Disconnected)
            {
                logger.LogInformation("Reconnecting {FullName} on an OIDC upload (was {Reason})",
                    repository.FullName, repository.DisconnectedReason);
                repository.MarkConnected();
            }

            var claimedFullName = User.FindFirst(Oidc.RepositoryClaim)?.Value;
            if (!string.IsNullOrEmpty(claimedFullName) && claimedFullName != repository.FullName)
            {
                if (!repository.PreviousFullNames.Contains(repository.FullName, StringComparer.OrdinalIgnoreCase))
                    repository.PreviousFullNames.Add(repository.FullName);
                repository.FullName = claimedFullName;
                repository.Name = claimedFullName.Split('/')[1];
                repository.OwnerLogin = User.FindFirst(Oidc.OwnerClaim)?.Value
                    ?? claimedFullName.Split('/')[0];
            }

            return repository;
        }

        if (!provision)
            return null;

        if (User.FindFirst(Oidc.VisibilityClaim)?.Value != Oidc.PublicVisibilityValue)
            return null;

        var fullName = User.FindFirst(Oidc.RepositoryClaim)!.Value;
        var ownerLogin = User.FindFirst(Oidc.OwnerClaim)?.Value ?? fullName.Split('/')[0];

        Account? account = null;
        if (long.TryParse(User.FindFirst(Oidc.OwnerIdClaim)?.Value, out var ownerId))
        {
            account = await session.LoadAsync<Account>(Account.DocumentId(EForgeProvider.GitHub, ownerId), cancellationToken);
            if (account is null)
            {
                account = new Account { GitHubId = ownerId, Login = ownerLogin };
                await session.StoreAsync(account, Account.DocumentId(EForgeProvider.GitHub, ownerId), cancellationToken);
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
        await session.StoreAsync(repository, Repository.DocumentId(EForgeProvider.GitHub, gitHubRepoId), cancellationToken);
        logger.LogInformation("Auto-provisioned public repository {FullName} from OIDC upload", fullName);
        return repository;
    }
}
