using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodeCoverage.ApiTokens;
using CodeCoverage.Entities;
using CodeCoverage.Forge;
using CodeCoverage.Feedback;
using CodeCoverage.MergeQueue;
using CodeCoverage.Versioning.Releases;
using CodeCoverage.Versioning.Versions;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Messaging.Abstractions;
using MintPlayer.Spark.Services;
using Raven.Client.Documents.Session;

namespace CodeCoverage.PackageVersioning;

/// <summary>
/// The server host of package versioning (docs/coverage_package_versioning_PRD.md D2, D5, S6; plan M6): a publish workflow
/// sends the release manifest of the commit it built, and the git writer plans the versions and writes the tags.
/// </summary>
/// <remarks>
/// <para>
/// <b>Who may ask.</b> Only a GitHub Actions OIDC token, never an upload token or a cookie: the token's claims are what say
/// which repository, branch and commit the tags go on, and the body cannot widen them (S6). The repository must be known,
/// the ref a branch (a pull request's <c>refs/pull/N/merge</c> is refused), that branch the default branch or a release
/// branch of the DEFAULT branch's <c>coverage.yml</c>, and the manifest's commit the token's <c>sha</c>.
/// </para>
/// <para>
/// <b>Statuses the action reads.</b> 200 is a run that ended (its body says which tags were written). 400, 403, 409 and 422
/// are answers a retry or the local mode would not change, so the action fails. 502 and 503 mean the server could not run
/// it, and the action falls back to local mode (D2).
/// </para>
/// </remarks>
[ApiController]
[Route("api/versioning")]
[Authorize(AuthenticationSchemes = GitHubOidc.SchemeName)]
[SparkAuthorize("Upload", "Coverage")]
[EnableRateLimiting("uploads")]
public partial class VersioningController : ControllerBase
{
    public const string CheckName = "Versioning";
    internal static readonly GitWriterTagger Tagger = new("CodeCoverage versioning", "versioning@noreply.coverage.mintplayer.com");

    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly IForgeIntegrationResolver forges;
    [Inject] private readonly IEnumerable<IForgeMergeQueue> queues;
    [Inject] private readonly IGitWriterSidecar writer;
    [Inject] private readonly VersioningRunLocks locks;
    [Inject] private readonly IVersioningOnboardingService onboarding;
    [Inject] private readonly IMessageBus messageBus;
    [Inject] private readonly TimeProvider clock;
    [Inject] private readonly ILogger<VersioningController> logger;
    [Inject] private readonly IDataProtectionProvider dataProtection;

    /// <summary>See <c>UploadsController.Oidc</c>: GitHub is the only OIDC scheme registered.</summary>
    private static ForgeOidcProfile Oidc => ForgeOidcProfile.GitHub;

    /// <param name="Outcome">The writer's outcome (<c>versioned</c>, <c>plan-refused</c>, …) or the app's refusal.</param>
    public sealed record VersioningRunResponse(
        string Outcome, string? Reason, string? Detail, IReadOnlyList<GitWriterRelease> Releases, IReadOnlyList<GitWriterPackageNote> Refused,
        int Skipped, IReadOnlyList<string> Warnings, IReadOnlyList<string>? Errors = null, IReadOnlyList<GitWriterBranchCut>? BranchCuts = null)
    {
        /// <summary>#552 D1: the tags <c>plan</c> decided and wrote none of.</summary>
        public TagPlan? Plan { get; init; }

        /// <summary>
        /// #552 D6: this response, sealed with the app's Data Protection keys for a day. <c>release</c> takes only the seal,
        /// so the client cannot change a version, a tag or a Release text between the plan and its release.
        /// </summary>
        public string? Seal { get; init; }
    }

    /// <summary>The body of <c>release</c>: the seal <c>plan</c> answered with.</summary>
    public sealed record VersioningReleaseRequest(string? Seal);

    /// <summary>What a seal holds: the plan's answer and what the release's token must match.</summary>
    internal sealed record SealedPlan(long RepositoryId, long RunId, string Commit, string Branch, VersioningRunResponse Response);

    internal const string SealPurpose = "CodeCoverage.Versioning.Plan.v1";
    internal static readonly TimeSpan SealLifetime = TimeSpan.FromDays(1);

    [RequireAntiforgeryToken(false)]
    [HttpPost("run")]
    [RequestSizeLimit(Versioning.Graph.Limits.MaxManifestBytes)]
    public Task<IActionResult> Run([FromBody] ReleaseManifest? manifest, CancellationToken cancellationToken) => VersionAsync(manifest, planOnly: false, cancellationToken);

    /// <summary>
    /// #552 D1, D6: the run's decision without its tags, checked exactly as <see cref="Run"/>. The answer carries the plan
    /// and its seal; nothing is recorded or reported until <see cref="Release"/> writes the tags.
    /// </summary>
    [RequireAntiforgeryToken(false)]
    [HttpPost("plan")]
    [RequestSizeLimit(Versioning.Graph.Limits.MaxManifestBytes)]
    public Task<IActionResult> Plan([FromBody] ReleaseManifest? manifest, CancellationToken cancellationToken) => VersionAsync(manifest, planOnly: true, cancellationToken);

    private async Task<IActionResult> VersionAsync(ReleaseManifest? manifest, bool planOnly, CancellationToken cancellationToken)
    {
        if (manifest?.Validate() is not { Count: 0 })
            return Answer(StatusCodes.Status400BadRequest, "refused", "manifest", errors: manifest?.Validate() ?? ["The body is not a release manifest."]);

        if (!long.TryParse(User.FindFirst(Oidc.RepositoryIdClaim)?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var repoId)
            || !long.TryParse(User.FindFirst(Oidc.RunIdClaim)?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var runId))
            return Forbidden("The token names no repository or workflow run.");
        var runAttempt = int.TryParse(User.FindFirst(Oidc.RunAttemptClaim!)?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var attempt) ? attempt : 1;

        var repository = await session.LoadAsync<Repository>(Repository.DocumentId(EForgeProvider.GitHub, repoId), cancellationToken);
        if (repository is null)
            return Forbidden("The repository is not known to CodeCoverage: install the GitHub App on it.");
        if (Oidc.Branch(User) is not { } branch)
            return Forbidden("Tags are written only from a branch run, never from a pull request or a tag.");
        if (!string.Equals(User.FindFirst(Oidc.CommitClaim)?.Value, manifest.Commit, StringComparison.Ordinal))
            return Forbidden("The manifest's commit is not the commit the workflow run is for.");
        if (repository.DefaultBranch is not { } defaultBranch)
            return Answer(StatusCodes.Status409Conflict, "refused", "repository", "The repository's default branch is not known yet.");

        // D17: the section is read from the DEFAULT branch, so a branch cannot switch versioning on or widen its own lines.
        VersioningYmlResult parsed;
        try
        {
            parsed = VersioningYml.Parse(await forges.For(repository).GetFileContentAsync(repository, defaultBranch, CoverageYml.FileName, cancellationToken));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Versioning: could not read {File} of {Repository}", CoverageYml.FileName, repository.FullName);
            return Answer(StatusCodes.Status502BadGateway, "failed", "forge", $"{CoverageYml.FileName} could not be read from the forge.");
        }
        if (parsed.Config is not { } config)
            return Answer(StatusCodes.Status409Conflict, "refused", "not-versioned", $"{CoverageYml.FileName} on {defaultBranch} has no versioning section.");
        if (parsed.Errors.Count > 0)
            return Answer(StatusCodes.Status422UnprocessableEntity, "refused", "config", $"The versioning section of {CoverageYml.FileName} on {defaultBranch} is not usable.", errors: parsed.Errors);

        int? branchMajor = null;
        if (branch != defaultBranch && !ReleaseBranches.IsMatch(config.EffectiveReleaseBranches, branch, out branchMajor))
            return Forbidden($"{branch} is neither the default branch nor a release branch ({string.Join(", ", config.EffectiveReleaseBranches)}).");

        if (!writer.IsConfigured)
            return Answer(StatusCodes.Status503ServiceUnavailable, "failed", "unavailable", "The git writer is not configured on this server.");
        if (queues.FirstOrDefault(q => q.Provider == repository.Provider) is not { } forge)
            return Answer(StatusCodes.Status503ServiceUnavailable, "failed", "unavailable", "This forge has no git writer access.");

        using var held = await locks.TryEnterAsync(repository.Id!, cancellationToken);
        if (held is null)
            return Answer(StatusCodes.Status503ServiceUnavailable, "failed", "busy", "Another versioning run of this repository is still going.");

        // D17: the first run after the onboarding merged may beat its webhook; without the seeds it would release every
        // untagged package from scratch, so it pushes them itself, or waits.
        try
        {
            if (!await onboarding.PushSeedsAsync(repository, cancellationToken))
                return Answer(StatusCodes.Status503ServiceUnavailable, "failed", "seeds", "The onboarding's seed tags could not be pushed yet; the run waits for them.");
        }
        catch (Exception ex) when (ex is GitWriterUnavailableException or HttpRequestException or ForgeRequestException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Versioning: could not push the onboarding seeds of {Repository}", repository.FullName);
            return Answer(StatusCodes.Status503ServiceUnavailable, "failed", "seeds", "The onboarding's seed tags could not be pushed yet; the run waits for them.");
        }

        var record = NewRecord(repository, repoId, runId, runAttempt, branch, manifest.Commit!);

        // #544 D5: every tag names this run; the id comes from the OIDC token, never from the request body.
        var runUrl = TagAnnotation.RunUrl("https://github.com", repository.FullName, runId.ToString(CultureInfo.InvariantCulture));
        var (status, response) = await RunOnWriterAsync(repository, forge, branch, defaultBranch, branchMajor, config.ToSettings(), manifest, runUrl, planOnly,
            cancellationToken);
        if (planOnly)
            return StatusCode(status, status == StatusCodes.Status200OK && response.Plan is not null
                ? response with { Seal = Sealer.Protect(JsonSerializer.Serialize(new SealedPlan(repoId, runId, manifest.Commit!, branch, response), SealJson), SealLifetime) }
                : response);
        return await FinishAsync(record, repository, branch, status, response, cancellationToken);
    }

    /// <summary>
    /// #552 D6: writes the tags of a sealed plan as the App, deciding nothing again. The token must be the same
    /// repository's same workflow run (any attempt, so "re-run failed jobs" works) on the plan's commit and branch, and the
    /// writer refuses with <c>stale-plan</c> when the tag history moved since the plan (D3).
    /// </summary>
    [RequireAntiforgeryToken(false)]
    [HttpPost("release")]
    [RequestSizeLimit(Versioning.Graph.Limits.MaxManifestBytes)]
    public async Task<IActionResult> Release([FromBody] VersioningReleaseRequest? request, CancellationToken cancellationToken)
    {
        if (!long.TryParse(User.FindFirst(Oidc.RepositoryIdClaim)?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var repoId)
            || !long.TryParse(User.FindFirst(Oidc.RunIdClaim)?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var runId))
            return Forbidden("The token names no repository or workflow run.");
        var runAttempt = int.TryParse(User.FindFirst(Oidc.RunAttemptClaim!)?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var attempt) ? attempt : 1;

        if (Unseal(request?.Seal) is not { Response.Plan: { } plan } sealedPlan)
            return Answer(StatusCodes.Status400BadRequest, "refused", "seal", "The plan's seal is missing, expired, or was not made by this server.");
        // A run id belongs to exactly one workflow of one repository, so these two also pin the workflow (#552 D6).
        if (sealedPlan.RepositoryId != repoId || sealedPlan.RunId != runId)
            return Forbidden("The plan was made for another repository or workflow run.");
        if (!string.Equals(User.FindFirst(Oidc.CommitClaim)?.Value, sealedPlan.Commit, StringComparison.Ordinal))
            return Forbidden("The plan's commit is not the commit the workflow run is for.");
        if (Oidc.Branch(User) is not { } branch || branch != sealedPlan.Branch)
            return Forbidden("The plan was made on another branch.");

        var repository = await session.LoadAsync<Repository>(Repository.DocumentId(EForgeProvider.GitHub, repoId), cancellationToken);
        if (repository is null)
            return Forbidden("The repository is not known to CodeCoverage: install the GitHub App on it.");
        if (!writer.IsConfigured)
            return Answer(StatusCodes.Status503ServiceUnavailable, "failed", "unavailable", "The git writer is not configured on this server.");
        if (queues.FirstOrDefault(q => q.Provider == repository.Provider) is not { } forge)
            return Answer(StatusCodes.Status503ServiceUnavailable, "failed", "unavailable", "This forge has no git writer access.");

        using var held = await locks.TryEnterAsync(repository.Id!, cancellationToken);
        if (held is null)
            return Answer(StatusCodes.Status503ServiceUnavailable, "failed", "busy", "Another versioning run of this repository is still going.");

        var record = NewRecord(repository, repoId, runId, runAttempt, branch, sealedPlan.Commit);
        var (status, response) = await ReleaseOnWriterAsync(repository, forge, branch, plan, sealedPlan.Response, cancellationToken);
        return await FinishAsync(record, repository, branch, status, response, cancellationToken);
    }

    private VersioningRun NewRecord(Repository repository, long repoId, long runId, int runAttempt, string branch, string commit) => new()
    {
        Id = VersioningRun.DocumentId(repository.Provider, repoId, runId, runAttempt),
        Repository = repository.Id!,
        Branch = branch,
        Commit = commit,
        WorkflowRef = User.FindFirst(Oidc.WorkflowRefClaim)?.Value,
        RunId = runId,
        RunAttempt = runAttempt,
        StartedAtUtc = clock.GetUtcNow().UtcDateTime,
    };

    private async Task<IActionResult> FinishAsync(VersioningRun record, Repository repository, string branch, int status, VersioningRunResponse response,
        CancellationToken cancellationToken)
    {
        Record(record, response);
        await session.StoreAsync(record, record.Id, cancellationToken);
        await session.SaveChangesAsync(cancellationToken);
        // D15: the Releases follow on the versioning lane, one at a time, so a secondary rate limit parks the message
        // instead of holding this request (and the action's fallback clock) open.
        if (record.Releases.Any(r => r.Release is not null))
            await messageBus.BroadcastAsync(new CreateVersionReleasesMessage { RunId = record.Id! }, cancellationToken);
        // D12: so does the cut of a release branch when a platform major moved.
        if (record.BranchCuts.Count > 0)
            await messageBus.BroadcastAsync(new CutReleaseBranchesMessage { RunId = record.Id! }, cancellationToken);
        await ReportAsync(repository, record.Commit, branch, response, cancellationToken);
        return StatusCode(status, response);
    }

    private ITimeLimitedDataProtector Sealer => dataProtection.CreateProtector(SealPurpose).ToTimeLimitedDataProtector();

    private static readonly JsonSerializerOptions SealJson = new(JsonSerializerDefaults.Web);

    private SealedPlan? Unseal(string? seal)
    {
        if (string.IsNullOrEmpty(seal)) return null;
        try
        {
            return JsonSerializer.Deserialize<SealedPlan>(Sealer.Unprotect(seal), SealJson);
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or FormatException)
        {
            return null;
        }
    }

    /// <summary>#552 D6: the plan's tags through the writer's <c>/jobs/tags</c>, which re-checks the tag history first (D3).</summary>
    private async Task<(int Status, VersioningRunResponse Response)> ReleaseOnWriterAsync(
        Repository repository, IForgeMergeQueue forge, string branch, TagPlan plan, VersioningRunResponse planned, CancellationToken cancellationToken)
    {
        if (plan.Tags.Count == 0) return (StatusCodes.Status200OK, Released(planned, []));
        var push = await forge.GetWriterAccessAsync(repository, null, false, cancellationToken);
        if (push.Access is not { } access)
            return (StatusCodes.Status503ServiceUnavailable, Refusal("failed", "auth", push.RefusalReason));
        try
        {
            var result = await writer.TagsAsync(new GitWriterTagsRequest(
                repository.Provider.ToCanonicalString(), repository.GitHubId.ToString(CultureInfo.InvariantCulture), access.CloneUrl,
                new CommitGraph.SidecarCredential(access.Username, access.Password), [branch], Tagger,
                [.. plan.Tags.Select(t => new GitWriterTag(t.Name, plan.Commit, t.ApiHash))], plan.Run, plan.Fingerprint), cancellationToken);
            return (result.Outcome, result.Reason) switch
            {
                (GitWriterTagsResult.Tagged, _) => (StatusCodes.Status200OK, Released(planned, result.Tags ?? [])),
                (_, StalePlan) => (StatusCodes.Status409Conflict, Refusal("refused", StalePlan, result.Detail)),
                ("busy", _) => (StatusCodes.Status503ServiceUnavailable, Refusal(result.Outcome, result.Reason, result.Detail)),
                _ => (StatusCodes.Status502BadGateway, Refusal(result.Outcome, result.Reason, result.Detail)),
            };
        }
        catch (GitWriterRefusedException ex)
        {
            return (StatusCodes.Status422UnprocessableEntity, Refusal("refused", "request", ex.Message));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Versioning: the git writer did not answer for {Repository}", repository.FullName);
            return (StatusCodes.Status502BadGateway, Refusal("failed", "unavailable", "The git writer did not answer."));
        }
        finally
        {
            await forge.RevokeWriterAccessAsync(repository, access, CancellationToken.None);
        }
    }

    internal const string StalePlan = "stale-plan";

    /// <summary>
    /// The plan's answer after its release: each tag's outcome, and only the Release texts and branch cuts of tags now in
    /// place, as a run that wrote its own tags reports them.
    /// </summary>
    internal static VersioningRunResponse Released(VersioningRunResponse planned, IReadOnlyList<GitWriterTagResult> written)
    {
        var outcomes = written.ToDictionary(t => t.Name, StringComparer.Ordinal);
        static bool InPlace(string? outcome) => outcome is GitWriterTagResult.Created or GitWriterTagResult.AlreadyPresent;
        var releases = planned.Releases.Select(r => r.TaggedByHand ? r
            : outcomes.TryGetValue(r.Tag, out var result) ? r with { TagOutcome = result.Outcome, Release = InPlace(result.Outcome) ? r.Release : null }
            : r with { TagOutcome = null, Release = null }).ToList();
        var placed = releases.Where(r => r.Release is not null).Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        var failed = written.Where(t => !InPlace(t.Outcome)).Select(t => $"{t.Name}: {t.Detail ?? t.Outcome}").ToList();
        return planned with
        {
            Releases = releases,
            BranchCuts = planned.BranchCuts?.Where(c => (c.Packages ?? []).Any(placed.Contains)).ToList(),
            Errors = failed.Count > 0 ? failed : planned.Errors,
            Plan = null,
            Seal = null,
        };
    }

    private async Task<(int Status, VersioningRunResponse Response)> RunOnWriterAsync(
        Repository repository, IForgeMergeQueue forge, string branch, string defaultBranch, int? branchMajor, VersioningSettings settings,
        ReleaseManifest manifest, string? runUrl, bool planOnly, CancellationToken cancellationToken)
    {
        var push = await forge.GetWriterAccessAsync(repository, null, false, cancellationToken);
        if (push.Access is not { } access)
            return (StatusCodes.Status503ServiceUnavailable, Refusal("failed", "auth", push.RefusalReason));
        try
        {
            var result = await writer.VersioningRunAsync(new GitWriterVersioningRequest(
                repository.Provider.ToCanonicalString(), repository.GitHubId.ToString(CultureInfo.InvariantCulture), access.CloneUrl,
                new CommitGraph.SidecarCredential(access.Username, access.Password), branch, defaultBranch, branchMajor, Tagger, settings, manifest, runUrl, planOnly),
                cancellationToken);
            var response = new VersioningRunResponse(result.Outcome, result.Reason, result.Detail, result.Releases ?? [], result.Refused ?? [],
                result.Skipped?.Count ?? 0, result.Warnings ?? [], null, result.BranchCuts ?? []) { Plan = result.Plan };
            return (result.Outcome, result.Reason) switch
            {
                (GitWriterVersioningResult.Versioned, _) => (StatusCodes.Status200OK, response),
                (GitWriterVersioningResult.PlanRefused, _) or (_, GitWriterVersioningResult.Unplannable) => (StatusCodes.Status422UnprocessableEntity, response),
                ("busy", _) => (StatusCodes.Status503ServiceUnavailable, response),
                _ => (StatusCodes.Status502BadGateway, response),
            };
        }
        catch (GitWriterRefusedException ex)
        {
            return (StatusCodes.Status422UnprocessableEntity, Refusal("refused", "request", ex.Message));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Versioning: the git writer did not answer for {Repository}", repository.FullName);
            return (StatusCodes.Status502BadGateway, Refusal("failed", "unavailable", "The git writer did not answer."));
        }
        finally
        {
            await forge.RevokeWriterAccessAsync(repository, access, CancellationToken.None);
        }
    }

    private void Record(VersioningRun record, VersioningRunResponse response)
    {
        record.FinishedAtUtc = clock.GetUtcNow().UtcDateTime;
        record.Outcome = response.Outcome;
        record.Reason = response.Reason;
        record.Detail = response.Detail;
        record.Releases = response.Releases.Take(VersioningRun.MaxNotes).Select(r => new VersioningRunRelease
        {
            Id = r.Id, Kind = r.Kind, Version = r.Version, Tag = r.Tag, Reasons = [.. r.Reasons ?? []], TaggedByHand = r.TaggedByHand, TagOutcome = r.TagOutcome,
            Release = r.Release is { } notes ? new VersioningRunReleaseNotes { Name = notes.Name, Body = notes.Body, Prerelease = notes.Prerelease } : null,
        }).ToList();
        record.Refused = response.Refused.Take(VersioningRun.MaxNotes).Select(n => new VersioningRunNote { Id = n.Id, Reason = n.Reason }).ToList();
        record.Skipped = response.Skipped;
        record.Warnings = response.Warnings.Take(VersioningRun.MaxNotes).ToList();
        record.BranchCuts = (response.BranchCuts ?? []).Take(VersioningRun.MaxNotes).Select(c => new VersioningRunBranchCut
        {
            Branch = c.Branch, Major = c.Major, Commit = c.Commit, Packages = [.. c.Packages ?? []], PullRequest = c.PullRequest,
        }).ToList();
    }

    /// <summary>The run's check on the commit. Best effort: the tags are written whether or not the forge takes it.</summary>
    private async Task ReportAsync(Repository repository, string commit, string branch, VersioningRunResponse response, CancellationToken cancellationToken)
    {
        try
        {
            if (forges.For(repository.Provider) is not { } integration) return;
            var versioned = response.Outcome == GitWriterVersioningResult.Versioned;
            var created = response.Releases.Count(r => r.TagOutcome == "created");
            var title = versioned
                ? created == 0 ? "No new versions" : $"{created} version{(created == 1 ? "" : "s")} tagged"
                : $"Versioning {response.Outcome}";
            await integration.PublishStatusAsync(repository, commit, CheckName,
                new ForgeVerdict(versioned ? EForgeOutcome.Success : EForgeOutcome.Failure, title, Summary(branch, response)), null, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Versioning: could not publish the check run for {Repository}", repository.FullName);
        }
    }

    internal static string Summary(string branch, VersioningRunResponse response)
    {
        var text = new StringBuilder($"Branch `{branch}`.\n\n");
        if (response.Detail is { Length: > 0 } detail) text.Append(detail).Append("\n\n");
        if (response.Releases.Count > 0)
        {
            text.Append("| Package | Version | Tag | Why |\n|-|-|-|-|\n");
            foreach (var release in response.Releases.Take(100))
                text.Append($"| `{release.Id}` | {release.Version} | {(release.TaggedByHand ? "by hand" : release.TagOutcome)} | {string.Join("; ", release.Reasons ?? [])} |\n");
        }
        foreach (var refused in response.Refused.Take(100))
            text.Append($"- `{refused.Id}` refused: {refused.Reason}\n");
        if (response.Skipped > 0) text.Append($"\n{response.Skipped} package(s) unchanged.\n");
        foreach (var warning in response.Warnings.Take(50))
            text.Append($"- ⚠️ {warning}\n");
        return text.ToString();
    }

    private static VersioningRunResponse Refusal(string outcome, string? reason, string? detail, IReadOnlyList<string>? errors = null)
        => new(outcome, reason, detail, [], [], 0, [], errors);

    private ObjectResult Answer(int status, string outcome, string reason, string? detail = null, IReadOnlyList<string>? errors = null)
        => StatusCode(status, Refusal(outcome, reason, detail, errors));

    private ObjectResult Forbidden(string detail) => Answer(StatusCodes.Status403Forbidden, "refused", "token", detail);
}
