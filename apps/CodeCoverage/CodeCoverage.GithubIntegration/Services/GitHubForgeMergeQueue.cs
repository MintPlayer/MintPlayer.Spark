using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodeCoverage.Forge;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Webhooks.GitHub.Services;
using Octokit;
using Raven.Client.Documents.Session;
using Account = CodeCoverage.Entities.Account;
using Repository = CodeCoverage.Entities.Repository;

namespace CodeCoverage.Services;

/// <summary>
/// GitHub's <see cref="IForgeMergeQueue"/> (merge-queue PRD M4), as the App installation of the repository's account.
/// </summary>
/// <remarks>
/// <para>
/// <b>REST over raw JSON for the queue's reads and the merge.</b> Several of these endpoints are newer than
/// Octokit 14 (<c>merge-async</c> and its undocumented status route, <c>return_run_details</c>, rulesets'
/// <c>current_user_can_bypass</c>), and <c>merge-async</c> needs its own <c>X-GitHub-Api-Version</c>, which
/// Octokit cannot send per request. The installation token is the bearer, exactly as Octokit would send it.
/// The check run, labels and comments go through Octokit, like the coverage feedback.
/// </para>
/// <para>
/// The response shapes are the ones measured live on 2026-10-14 (the PRD's D13–D19, spike notes
/// <c>spikes_live.md</c> and <c>spikes_app.md</c>); the tests replay them.
/// </para>
/// </remarks>
[Register(typeof(IForgeMergeQueue), ServiceLifetime.Scoped)]
public partial class GitHubForgeMergeQueue : IForgeMergeQueue
{
    [Inject] private readonly IGitHubInstallationService installationService;
    [Inject] private readonly IHttpClientFactory httpClientFactory;
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly ILogger<GitHubForgeMergeQueue> logger;

    private static readonly Uri ApiBase = new("https://api.github.com/");
    private const string DefaultApiVersion = "2022-11-28";

    /// <summary>The version <c>merge-async</c> is documented under (D13).</summary>
    internal const string MergeAsyncApiVersion = "2026-03-10";

    private const int PageSize = 100;

    /// <summary>At most this many pages of any listing (10,000 items): a commit with more checks is not a real case.</summary>
    private const int MaxPages = 100;

    /// <summary>GitHub's limit on a check run's <c>output.summary</c>.</summary>
    private const int MaxSummaryLength = 65535;

    public EForgeProvider Provider => EForgeProvider.GitHub;

    // ── Roles and rules ──────────────────────────────────────────────────────────────────────────

    public async Task<EForgeRepositoryRole> GetActorRoleAsync(Repository repository, string login, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(login) || !GitHubLogin().IsMatch(login))
            return EForgeRepositoryRole.None;

        using var reply = await SendAsync(repository, HttpMethod.Get,
            $"{RepoPath(repository)}/collaborators/{Uri.EscapeDataString(login)}/permission", null, DefaultApiVersion, cancellationToken);
        if (reply.Status == HttpStatusCode.NotFound)
            return EForgeRepositoryRole.None;
        EnsureSuccess(reply);

        return ReadRole(reply.Json!.RootElement);
    }

    /// <summary>
    /// <c>role_name</c> first (it distinguishes triage and maintain); for a custom role, which carries its
    /// own name there, the base <c>permission</c> it inherits (<c>admin</c>, <c>write</c>, <c>read</c>, <c>none</c>).
    /// </summary>
    internal static EForgeRepositoryRole ReadRole(JsonElement payload)
    {
        var role = ParseRole(Str(payload, "role_name"));
        return role ?? ParseRole(Str(payload, "permission")) ?? EForgeRepositoryRole.None;
    }

    private static EForgeRepositoryRole? ParseRole(string? word) => word switch
    {
        "admin" => EForgeRepositoryRole.Admin,
        "maintain" => EForgeRepositoryRole.Maintain,
        "write" => EForgeRepositoryRole.Write,
        "triage" => EForgeRepositoryRole.Triage,
        "read" => EForgeRepositoryRole.Read,
        "none" => EForgeRepositoryRole.None,
        _ => null,
    };

    /// <remarks>
    /// <para>
    /// Rulesets come from <c>GET /rules/branches/{branch}</c> (readable with <c>metadata: read</c>, D16), and
    /// each one's <c>current_user_can_bypass</c> from <c>GET /rulesets/{id}</c>. A 403 "Upgrade to GitHub Pro"
    /// means the plan has no rulesets, not that we may not read them (D15).
    /// </para>
    /// <para>
    /// Classic protection: <c>GET /branches/{branch}</c> gives the required checks to anyone who can read the
    /// repository; the review settings need <c>administration: read</c>, which the App does not ask for, so
    /// a 403 there leaves <see cref="EForgeClassicProtection.ChecksOnly"/>.
    /// </para>
    /// </remarks>
    public async Task<ForgeBranchRules> GetBranchRulesAsync(Repository repository, string branch, CancellationToken cancellationToken = default)
    {
        var branchPath = BranchPath(branch);
        var accumulator = new RulesAccumulator();

        var rulesetsAvailable = true;
        IReadOnlyList<JsonElement> rules;
        try
        {
            rules = await GetPagedAsync(repository, $"{RepoPath(repository)}/rules/branches/{branchPath}", null, cancellationToken);
        }
        catch (ForgeAccessDeniedException ex) when (IsPlanWithoutRulesets(ex.Message))
        {
            rulesetsAvailable = false;
            rules = [];
        }

        var typesByRuleset = new Dictionary<long, List<string>>();
        foreach (var rule in rules)
        {
            var type = Str(rule, "type") ?? string.Empty;
            accumulator.AddRule(type, rule.TryGetProperty("parameters", out var parameters) ? parameters : default);
            if (Num(rule, "ruleset_id") is { } rulesetId)
            {
                if (!typesByRuleset.TryGetValue(rulesetId, out var types))
                    typesByRuleset[rulesetId] = types = [];
                if (!types.Contains(type)) types.Add(type);
            }
        }

        var rulesets = new List<ForgeRuleset>();
        foreach (var (rulesetId, types) in typesByRuleset.OrderBy(pair => pair.Key))
            rulesets.Add(await ReadRulesetAsync(repository, rulesetId, types, cancellationToken));

        var classic = await ReadClassicProtectionAsync(repository, branchPath, accumulator, cancellationToken);

        return accumulator.Build(rulesetsAvailable, rulesets, classic);
    }

    private static bool IsPlanWithoutRulesets(string message)
        => message.Contains("Upgrade to GitHub Pro", StringComparison.OrdinalIgnoreCase);

    private async Task<ForgeRuleset> ReadRulesetAsync(Repository repository, long rulesetId, List<string> types, CancellationToken cancellationToken)
    {
        using var reply = await SendAsync(repository, HttpMethod.Get,
            $"{RepoPath(repository)}/rulesets/{rulesetId.ToString(CultureInfo.InvariantCulture)}", null, DefaultApiVersion, cancellationToken);
        if (reply.Status is HttpStatusCode.NotFound or HttpStatusCode.Forbidden)
        {
            // Unknown, not Never: the enable-time check (T7) must refuse what it could not read.
            logger.LogWarning("Could not read ruleset {RulesetId} on {FullName} ({Status}).", rulesetId, repository.FullName, (int)reply.Status);
            return new ForgeRuleset(rulesetId, null, types, EForgeRulesetBypass.Unknown);
        }
        EnsureSuccess(reply);

        var root = reply.Json!.RootElement;
        return new ForgeRuleset(rulesetId, Str(root, "name"), types, ParseBypass(Str(root, "current_user_can_bypass")));
    }

    internal static EForgeRulesetBypass ParseBypass(string? word) => word switch
    {
        "never" => EForgeRulesetBypass.Never,
        "always" => EForgeRulesetBypass.Always,
        "pull_requests_only" => EForgeRulesetBypass.PullRequestsOnly,
        "exempt" => EForgeRulesetBypass.Exempt,
        _ => EForgeRulesetBypass.Unknown,
    };

    private async Task<EForgeClassicProtection> ReadClassicProtectionAsync(
        Repository repository, string branchPath, RulesAccumulator accumulator, CancellationToken cancellationToken)
    {
        using (var reply = await SendAsync(repository, HttpMethod.Get, $"{RepoPath(repository)}/branches/{branchPath}", null, DefaultApiVersion, cancellationToken))
        {
            if (reply.Status == HttpStatusCode.NotFound)
                return EForgeClassicProtection.None;
            EnsureSuccess(reply);

            var root = reply.Json!.RootElement;
            if (!Flag(root, "protected"))
                return EForgeClassicProtection.None;
            if (root.TryGetProperty("protection", out var protection)
                && protection.TryGetProperty("required_status_checks", out var checks))
                accumulator.AddClassicChecks(checks);
        }

        using var detail = await SendAsync(repository, HttpMethod.Get, $"{RepoPath(repository)}/branches/{branchPath}/protection", null, DefaultApiVersion, cancellationToken);
        switch (detail.Status)
        {
            case HttpStatusCode.NotFound:
                // "Branch not protected": what made the branch read as protected is a ruleset, already read.
                return EForgeClassicProtection.None;
            case HttpStatusCode.Forbidden:
                return EForgeClassicProtection.ChecksOnly;
        }
        EnsureSuccess(detail);

        var protectionRoot = detail.Json!.RootElement;
        if (protectionRoot.TryGetProperty("required_status_checks", out var required))
            accumulator.AddClassicChecks(required);
        if (protectionRoot.TryGetProperty("required_pull_request_reviews", out var reviews))
            accumulator.AddClassicReviews(reviews);
        return EForgeClassicProtection.Read;
    }

    /// <summary>Merges every rule into one <see cref="ForgeBranchRules"/>: the strictest value of each setting wins.</summary>
    private sealed class RulesAccumulator
    {
        private readonly SortedSet<string> requiredChecks = new(StringComparer.Ordinal);
        private int approvals;
        private bool dismissStale;
        private bool lastPush;
        private bool upToDate;
        private bool nativeQueue;

        public void AddRule(string type, JsonElement parameters)
        {
            switch (type)
            {
                case "pull_request":
                    approvals = Math.Max(approvals, (int)(Num(parameters, "required_approving_review_count") ?? 0));
                    dismissStale |= Flag(parameters, "dismiss_stale_reviews_on_push");
                    lastPush |= Flag(parameters, "require_last_push_approval");
                    break;
                case "required_status_checks":
                    upToDate |= Flag(parameters, "strict_required_status_checks_policy");
                    foreach (var check in Items(parameters, "required_status_checks"))
                        if (Str(check, "context") is { Length: > 0 } context) requiredChecks.Add(context);
                    break;
                case "merge_queue":
                    nativeQueue = true;
                    break;
            }
        }

        /// <summary>Both shapes: the branch's <c>protection.required_status_checks</c> and the protection's own.</summary>
        public void AddClassicChecks(JsonElement checks)
        {
            if (checks.ValueKind != JsonValueKind.Object) return;
            upToDate |= Flag(checks, "strict");
            foreach (var context in Items(checks, "contexts"))
                if (context.ValueKind == JsonValueKind.String && context.GetString() is { Length: > 0 } name) requiredChecks.Add(name);
            foreach (var check in Items(checks, "checks"))
                if (Str(check, "context") is { Length: > 0 } name) requiredChecks.Add(name);
        }

        public void AddClassicReviews(JsonElement reviews)
        {
            if (reviews.ValueKind != JsonValueKind.Object) return;
            approvals = Math.Max(approvals, (int)(Num(reviews, "required_approving_review_count") ?? 0));
            dismissStale |= Flag(reviews, "dismiss_stale_reviews");
            lastPush |= Flag(reviews, "require_last_push_approval");
        }

        public ForgeBranchRules Build(bool rulesetsAvailable, IReadOnlyList<ForgeRuleset> rulesets, EForgeClassicProtection classic)
            => new(rulesetsAvailable, [.. requiredChecks], approvals, dismissStale, lastPush, upToDate, nativeQueue, rulesets, classic);
    }

    // ── Checks ───────────────────────────────────────────────────────────────────────────────────

    public async Task<ForgeCheckState> GetCheckStateAsync(Repository repository, string sha, CancellationToken cancellationToken = default)
    {
        RequireSha(sha);
        var commit = $"{RepoPath(repository)}/commits/{sha}";

        var runs = await GetPagedAsync(repository, $"{commit}/check-runs?filter=latest", "check_runs", cancellationToken);
        var statuses = await GetPagedAsync(repository, $"{commit}/status", "statuses", cancellationToken);
        var suites = await GetPagedAsync(repository, $"{commit}/check-suites", "check_suites", cancellationToken);

        return new ForgeCheckState(
            sha,
            [.. runs.Select(run => new ForgeCheckRun(
                Num(run, "id") ?? 0,
                Str(run, "name") ?? string.Empty,
                Str(run, "status") ?? "queued",
                Str(run, "conclusion"),
                run.TryGetProperty("check_suite", out var suite) ? Num(suite, "id") : null,
                run.TryGetProperty("app", out var app) ? Str(app, "slug") : null,
                Str(run, "details_url") ?? Str(run, "html_url")))],
            [.. statuses.Select(status => new ForgeCommitStatus(
                Str(status, "context") ?? string.Empty,
                Str(status, "state") ?? "pending",
                Str(status, "target_url")))],
            [.. suites.Select(suite => new ForgeCheckSuite(
                Num(suite, "id") ?? 0,
                Str(suite, "status") ?? "queued",
                Str(suite, "conclusion"),
                suite.TryGetProperty("app", out var app) ? Str(app, "slug") : null,
                (int)(Num(suite, "latest_check_runs_count") ?? 0)))]);
    }

    public async Task<IReadOnlyList<string>> GetCheckNamesOnCommitAsync(Repository repository, string sha, CancellationToken cancellationToken = default)
    {
        RequireSha(sha);
        var commit = $"{RepoPath(repository)}/commits/{sha}";
        var runs = await GetPagedAsync(repository, $"{commit}/check-runs?filter=latest", "check_runs", cancellationToken);
        var statuses = await GetPagedAsync(repository, $"{commit}/status", "statuses", cancellationToken);

        return [.. runs.Select(run => Str(run, "name"))
            .Concat(statuses.Select(status => Str(status, "context")))
            .OfType<string>()
            .Where(name => name.Length > 0 && name != CheckGate.QueueCheckName)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)];
    }

    /// <summary>
    /// <c>GET /git/ref/heads/{branch}</c>, which answers the one ref only (<c>/git/refs/heads/x</c> would also list
    /// <c>x-y</c> as a prefix match). A 404 is "no such branch".
    /// </summary>
    public async Task<string?> GetBranchHeadAsync(Repository repository, string branch, CancellationToken cancellationToken = default)
    {
        using var reply = await SendAsync(repository, HttpMethod.Get, $"{RepoPath(repository)}/git/ref/heads/{BranchPath(branch)}", null, DefaultApiVersion, cancellationToken);
        if (reply.Status == HttpStatusCode.NotFound) return null;
        EnsureSuccess(reply);

        var root = reply.Json?.RootElement ?? default;
        var sha = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("object", out var target) ? Str(target, "sha") : null;
        return sha is not null && CommitSha().IsMatch(sha) ? sha : null;
    }

    /// <summary>
    /// A GitHub Actions check run is a job, and re-runs through <c>POST /actions/jobs/{id}/rerun</c> (its check-run id
    /// is its job id); any other App's check run is asked again through <c>POST /check-runs/{id}/rerequest</c>, which
    /// that App may honour or not. 403 is a missing permission (<c>actions: write</c>, O17); 404 and 422 (a run too old,
    /// a check that cannot be re-requested) are "cannot".
    /// </summary>
    public async Task<bool> RerunCheckAsync(Repository repository, ForgeCheckRun checkRun, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(checkRun);
        if (checkRun.Id <= 0) return false;

        var id = checkRun.Id.ToString(CultureInfo.InvariantCulture);
        var path = checkRun.AppSlug == "github-actions"
            ? $"{RepoPath(repository)}/actions/jobs/{id}/rerun"
            : $"{RepoPath(repository)}/check-runs/{id}/rerequest";
        using var reply = await SendAsync(repository, HttpMethod.Post, path, null, DefaultApiVersion, cancellationToken);
        if (reply.Status is HttpStatusCode.NotFound or HttpStatusCode.UnprocessableEntity) return false;
        EnsureSuccess(reply);
        return true;
    }

    // ── The regenerate workflow ──────────────────────────────────────────────────────────────────

    public async Task<ForgeWorkflowDispatch> DispatchWorkflowAsync(
        Repository repository, string workflowFile, string branch, IReadOnlyDictionary<string, string> inputs, CancellationToken cancellationToken = default)
    {
        if (!WorkflowFileName().IsMatch(workflowFile))
            throw new ArgumentException($"'{workflowFile}' is not a workflow file name.", nameof(workflowFile));
        ArgumentException.ThrowIfNullOrEmpty(branch);

        var body = new Dictionary<string, object?>
        {
            ["ref"] = branch,
            ["inputs"] = inputs,
            // D14: answers 200 with the run instead of 204 with nothing.
            ["return_run_details"] = true,
        };
        using var reply = await SendAsync(repository, HttpMethod.Post,
            $"{RepoPath(repository)}/actions/workflows/{Uri.EscapeDataString(workflowFile)}/dispatches", body, DefaultApiVersion, cancellationToken);

        switch (reply.Status)
        {
            case HttpStatusCode.NotFound:
                return new ForgeWorkflowDispatch(EForgeDispatchOutcome.WorkflowNotFound, null, null, reply.Message);
            case HttpStatusCode.UnprocessableEntity:
                return new ForgeWorkflowDispatch(EForgeDispatchOutcome.Refused, null, null, reply.Message);
            case HttpStatusCode.NoContent:
                // A GitHub that ignores return_run_details: dispatched, but uncorrelated.
                logger.LogWarning("Dispatching {Workflow} on {FullName} returned no run id.", workflowFile, repository.FullName);
                return new ForgeWorkflowDispatch(EForgeDispatchOutcome.Dispatched, null, null, null);
        }
        EnsureSuccess(reply);

        var root = reply.Json!.RootElement;
        return new ForgeWorkflowDispatch(EForgeDispatchOutcome.Dispatched, Num(root, "workflow_run_id"), Str(root, "html_url"), null);
    }

    public async Task<ForgeWorkflowRun?> GetWorkflowRunAsync(Repository repository, long runId, CancellationToken cancellationToken = default)
    {
        using var reply = await SendAsync(repository, HttpMethod.Get,
            $"{RepoPath(repository)}/actions/runs/{runId.ToString(CultureInfo.InvariantCulture)}", null, DefaultApiVersion, cancellationToken);
        if (reply.Status == HttpStatusCode.NotFound) return null;
        EnsureSuccess(reply);

        var root = reply.Json!.RootElement;
        return new ForgeWorkflowRun(
            Num(root, "id") ?? runId,
            Str(root, "status") ?? "queued",
            Str(root, "conclusion"),
            Str(root, "head_sha") ?? string.Empty,
            Str(root, "head_branch"),
            Str(root, "html_url"));
    }

    /// <summary>
    /// <c>POST /actions/runs/{id}/cancel</c>: 202 when GitHub accepted it. 409 means the run already completed and 404 that
    /// it is gone, both "nothing to cancel". A 403 is the missing <c>actions: write</c> (O17).
    /// </summary>
    public async Task<bool> CancelWorkflowRunAsync(Repository repository, long runId, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(runId);
        using var reply = await SendAsync(repository, HttpMethod.Post,
            $"{RepoPath(repository)}/actions/runs/{runId.ToString(CultureInfo.InvariantCulture)}/cancel", null, DefaultApiVersion, cancellationToken);
        if (reply.Status is HttpStatusCode.Conflict or HttpStatusCode.NotFound) return false;
        EnsureSuccess(reply);
        return true;
    }

    /// <summary>
    /// <c>GET /actions/workflows/{file}/runs?branch=…</c>, the first page only (newest first, which is all the stray-run
    /// sweep needs: the queue dispatches a handful of runs per entry at most). A workflow GitHub does not know is no runs.
    /// </summary>
    public async Task<IReadOnlyList<ForgeWorkflowRun>> ListWorkflowRunsAsync(Repository repository, string workflowFile, string branch, CancellationToken cancellationToken = default)
    {
        if (!WorkflowFileName().IsMatch(workflowFile))
            throw new ArgumentException($"'{workflowFile}' is not a workflow file name.", nameof(workflowFile));
        ArgumentException.ThrowIfNullOrEmpty(branch);

        using var reply = await SendAsync(repository, HttpMethod.Get,
            $"{RepoPath(repository)}/actions/workflows/{Uri.EscapeDataString(workflowFile)}/runs?branch={Uri.EscapeDataString(branch)}&per_page={RunsPageSize.ToString(CultureInfo.InvariantCulture)}",
            null, DefaultApiVersion, cancellationToken);
        if (reply.Status == HttpStatusCode.NotFound) return [];
        EnsureSuccess(reply);

        var root = reply.Json?.RootElement ?? default;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("workflow_runs", out var runs) || runs.ValueKind != JsonValueKind.Array)
            throw new ForgeRequestException($"GitHub's answer to the runs of {workflowFile} is not a listing.", (int)reply.Status);
        return [.. runs.EnumerateArray()
            .Where(run => Num(run, "id") is not null)
            .Select(run => new ForgeWorkflowRun(Num(run, "id")!.Value, Str(run, "status") ?? "queued", Str(run, "conclusion"),
                Str(run, "head_sha") ?? string.Empty, Str(run, "head_branch"), Str(run, "html_url")))];
    }

    /// <summary>The runs read by <see cref="ListWorkflowRunsAsync"/>.</summary>
    private const int RunsPageSize = 20;

    /// <remarks>
    /// <para>
    /// Three caps, because none of the sizes is ours to trust: the listing's <c>size_in_bytes</c>, the
    /// downloaded archive (read with a hard stop at <paramref name="maxBytes"/>), and the file inside it,
    /// whose declared length is checked and whose inflated bytes are counted again while reading (a zip
    /// header can lie about its uncompressed size).
    /// </para>
    /// <para>
    /// The archive URL answers a redirect to signed blob storage. A handler that follows redirects drops the
    /// <c>Authorization</c> header itself; one that does not gets the redirect followed here, without it.
    /// </para>
    /// </remarks>
    public async Task<ForgeArtifactDownload> DownloadArtifactAsync(
        Repository repository, long runId, string artifactName, long maxBytes, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(artifactName);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);

        var artifacts = await GetPagedAsync(repository,
            $"{RepoPath(repository)}/actions/runs/{runId.ToString(CultureInfo.InvariantCulture)}/artifacts?name={Uri.EscapeDataString(artifactName)}",
            "artifacts", cancellationToken);
        var matches = artifacts.Where(a => Str(a, "name") == artifactName).ToList();
        if (matches.Count == 0)
            return new ForgeArtifactDownload(EForgeArtifactOutcome.NotFound, null, null, $"The run has no artifact named '{artifactName}'.");

        var artifact = matches.FirstOrDefault(a => !Flag(a, "expired"));
        if (artifact.ValueKind == JsonValueKind.Undefined)
            return new ForgeArtifactDownload(EForgeArtifactOutcome.Expired, null, null, $"The artifact '{artifactName}' has expired.");
        if (Num(artifact, "size_in_bytes") is { } declared && declared > maxBytes)
            return TooLarge($"The artifact is {declared} bytes; the limit is {maxBytes}.");

        var artifactId = Num(artifact, "id") ?? 0;
        var archive = await DownloadArchiveAsync(repository, artifactId, maxBytes, cancellationToken);
        if (archive is null)
            return TooLarge($"The archive exceeds {maxBytes} bytes.");

        return ReadSingleFile(archive, maxBytes);
    }

    private static ForgeArtifactDownload TooLarge(string detail) => new(EForgeArtifactOutcome.TooLarge, null, null, detail);

    /// <summary>The archive's bytes, or null beyond <paramref name="maxBytes"/>.</summary>
    private async Task<byte[]?> DownloadArchiveAsync(Repository repository, long artifactId, long maxBytes, CancellationToken cancellationToken)
    {
        var token = await TokenAsync(repository, cancellationToken);
        var http = httpClientFactory.CreateClient(nameof(GitHubForgeMergeQueue));

        using var request = NewRequest(HttpMethod.Get,
            new Uri(ApiBase, $"{RepoPath(repository)}/actions/artifacts/{artifactId.ToString(CultureInfo.InvariantCulture)}/zip"), token, DefaultApiVersion);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
        {
            // Signed blob storage: no credential goes there.
            using var blobRequest = new HttpRequestMessage(HttpMethod.Get, location.IsAbsoluteUri ? location : new Uri(ApiBase, location));
            using var blob = await http.SendAsync(blobRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            return await ReadCappedAsync(blob, maxBytes, cancellationToken);
        }
        return await ReadCappedAsync(response, maxBytes, cancellationToken);
    }

    private static async Task<byte[]?> ReadCappedAsync(HttpResponseMessage response, long maxBytes, CancellationToken cancellationToken)
    {
        if (response.StatusCode == HttpStatusCode.Forbidden)
            throw new ForgeAccessDeniedException("GitHub refused the artifact download.", AcceptedPermissions(response));
        if (!response.IsSuccessStatusCode)
            throw new ForgeRequestException($"GitHub answered {(int)response.StatusCode} for the artifact download.", (int)response.StatusCode);
        if (response.Content.Headers.ContentLength is { } length && length > maxBytes)
            return null;

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await ReadAtMostAsync(stream, maxBytes, cancellationToken);
    }

    /// <summary>Up to <paramref name="maxBytes"/> bytes of <paramref name="stream"/>, or null when it has more.</summary>
    private static async Task<byte[]?> ReadAtMostAsync(Stream stream, long maxBytes, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > maxBytes) return null;
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    /// <summary>The archive's one file. Directories are ignored; any other count of files is invalid.</summary>
    internal static ForgeArtifactDownload ReadSingleFile(byte[] archive, long maxBytes)
    {
        try
        {
            using var zip = new ZipArchive(new MemoryStream(archive), ZipArchiveMode.Read);
            var files = zip.Entries.Where(entry => !entry.FullName.EndsWith('/')).ToList();
            if (files.Count != 1)
                return new ForgeArtifactDownload(EForgeArtifactOutcome.Invalid, null, null, $"The artifact holds {files.Count} files; exactly one was expected.");

            var file = files[0];
            if (file.Length > maxBytes)
                return TooLarge($"The file is {file.Length} bytes uncompressed; the limit is {maxBytes}.");

            using var entry = file.Open();
            using var content = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = entry.Read(chunk, 0, chunk.Length)) > 0)
            {
                // Counted again: the header's length is the archive's claim, not a fact.
                if (content.Length + read > maxBytes)
                    return TooLarge($"The file inflates past {maxBytes} bytes.");
                content.Write(chunk, 0, read);
            }
            return new ForgeArtifactDownload(EForgeArtifactOutcome.Ok, file.Name, content.ToArray(), null);
        }
        catch (InvalidDataException ex)
        {
            return new ForgeArtifactDownload(EForgeArtifactOutcome.Invalid, null, null, $"The artifact is not a readable zip: {ex.Message}");
        }
    }

    public async Task<string?> GetFileBlobShaAsync(Repository repository, string gitRef, string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(gitRef);
        var segments = path.Split('/');
        if (path.Length == 0 || segments.Any(segment => segment is "" or "." or ".."))
            throw new ArgumentException($"'{path}' is not a repository-relative file path.", nameof(path));

        using var reply = await SendAsync(repository, HttpMethod.Get,
            $"{RepoPath(repository)}/contents/{string.Join('/', segments.Select(Uri.EscapeDataString))}?ref={Uri.EscapeDataString(gitRef)}",
            null, DefaultApiVersion, cancellationToken);
        if (reply.Status == HttpStatusCode.NotFound) return null;
        EnsureSuccess(reply);

        // A directory answers an array; a symlink or submodule another type. Only a file has the blob we compare.
        var root = reply.Json!.RootElement;
        return root.ValueKind == JsonValueKind.Object && Str(root, "type") == "file" ? Str(root, "sha") : null;
    }

    /// <summary>
    /// <c>GET /contents/.github/workflows?ref=</c> for the listing (404: no folder, no workflows), then each workflow's
    /// blob by its sha (<c>GET /git/blobs/{sha}</c>, which unlike the contents API serves files past 1 MB too). Workflow
    /// names are matched case-insensitively, which is never fewer files than GitHub reads.
    /// </summary>
    public async Task<IReadOnlyList<ForgeWorkflowFile>> GetWorkflowFilesAsync(Repository repository, string gitRef, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(gitRef);
        const string folder = ".github/workflows";
        List<(string Path, string? Type, long? Size, string? Sha)> entries;
        using (var listing = await SendAsync(repository, HttpMethod.Get,
            $"{RepoPath(repository)}/contents/.github/workflows?ref={Uri.EscapeDataString(gitRef)}", null, DefaultApiVersion, cancellationToken))
        {
            if (listing.Status == HttpStatusCode.NotFound) return [];
            EnsureSuccess(listing);
            var root = listing.Json?.RootElement ?? default;
            if (root.ValueKind != JsonValueKind.Array)
                return [new ForgeWorkflowFile(folder, null, $"`{folder}` is a {Str(root, "type") ?? "thing"} here, not a folder")];
            entries = [.. root.EnumerateArray()
                .Select(e => (Path: Str(e, "path") ?? $"{folder}/{Str(e, "name")}", Type: Str(e, "type"), Size: Num(e, "size"), Sha: Str(e, "sha")))
                .Where(e => e.Path.EndsWith(".yml", StringComparison.OrdinalIgnoreCase) || e.Path.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase))
                .OrderBy(e => e.Path, StringComparer.Ordinal)];
        }

        var files = new List<ForgeWorkflowFile>();
        foreach (var (path, type, size, sha) in entries)
        {
            if (files.Count >= ForgeWorkflowFile.MaxFiles)
                files.Add(new ForgeWorkflowFile(path, null, $"past the first {ForgeWorkflowFile.MaxFiles} workflow files"));
            else if (type != "file" || sha is null || !CommitSha().IsMatch(sha))
                files.Add(new ForgeWorkflowFile(path, null, $"a {type ?? "thing"}, not a file"));
            else if (size is not { } bytes || bytes > ForgeWorkflowFile.MaxBytes)
                files.Add(new ForgeWorkflowFile(path, null, $"over {ForgeWorkflowFile.MaxBytes} bytes"));
            else
                files.Add(await ReadWorkflowAsync(repository, path, sha, cancellationToken));
        }
        return files;
    }

    private async Task<ForgeWorkflowFile> ReadWorkflowAsync(Repository repository, string path, string sha, CancellationToken cancellationToken)
    {
        using var blob = await SendAsync(repository, HttpMethod.Get, $"{RepoPath(repository)}/git/blobs/{sha}", null, DefaultApiVersion, cancellationToken);
        EnsureSuccess(blob);
        var root = blob.Json?.RootElement ?? default;
        if (Str(root, "encoding") != "base64" || Str(root, "content") is not { } content)
            return new ForgeWorkflowFile(path, null, "GitHub did not hand over its content");
        try
        {
            var bytes = Convert.FromBase64String(content.Replace("\n", "", StringComparison.Ordinal));
            if (bytes.Length > ForgeWorkflowFile.MaxBytes)
                return new ForgeWorkflowFile(path, null, $"over {ForgeWorkflowFile.MaxBytes} bytes");
            var text = StrictUtf8.GetString(bytes);
            return new ForgeWorkflowFile(path, text.StartsWith('﻿') ? text[1..] : text);
        }
        catch (Exception ex) when (ex is FormatException or DecoderFallbackException)
        {
            return new ForgeWorkflowFile(path, null, "not UTF-8 text");
        }
    }

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    // ── The merge ────────────────────────────────────────────────────────────────────────────────

    public async Task<ForgeMergeRequest> RequestMergeAsync(
        Repository repository, int number, string sha, EForgeMergeMethod method, string? commitTitle, string? commitMessage,
        CancellationToken cancellationToken = default)
    {
        RequireSha(sha);
        var body = new Dictionary<string, object?>
        {
            ["sha"] = sha,
            ["merge_method"] = method switch
            {
                EForgeMergeMethod.Squash => "squash",
                EForgeMergeMethod.Merge => "merge",
                EForgeMergeMethod.Rebase => "rebase",
                _ => throw new ArgumentOutOfRangeException(nameof(method), method, null),
            },
            // D13: with "default", a repository with GitHub's own queue on would enqueue the PR there.
            ["merge_action"] = "direct_merge",
            // T7: the bot is held to ruleset 2, always.
            ["bypass_rules"] = false,
        };
        if (commitTitle is not null) body["commit_title"] = commitTitle;
        if (commitMessage is not null) body["commit_message"] = commitMessage;

        using var reply = await SendAsync(repository, HttpMethod.Put,
            $"{RepoPath(repository)}/pulls/{number.ToString(CultureInfo.InvariantCulture)}/merge-async", body, MergeAsyncApiVersion, cancellationToken);
        return ReadMergeReply(reply);
    }

    public async Task<ForgeMergeRequest> GetMergeRequestStatusAsync(Repository repository, int number, string requestId, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(requestId, out var uuid))
            throw new ArgumentException($"'{requestId}' is not a merge request id.", nameof(requestId));

        // Undocumented, measured live (spikes_app.md h1): 200 with the same envelope as the PUT.
        using var reply = await SendAsync(repository, HttpMethod.Get,
            $"{RepoPath(repository)}/pulls/{number.ToString(CultureInfo.InvariantCulture)}/merge-async/{uuid:D}", null, MergeAsyncApiVersion, cancellationToken);
        if (reply.Status == HttpStatusCode.NotFound)
            return new ForgeMergeRequest(EForgeMergeState.Failed, null, null, "GitHub does not know this merge request.");
        var result = ReadMergeReply(reply);
        return result.RequestId is null && result.State == EForgeMergeState.Pending ? result with { RequestId = uuid.ToString("D") } : result;
    }

    /// <summary>
    /// The <c>merge-async</c> envelope, <c>{ status, details: { message, uuid?, sha? } }</c>, by status code
    /// (D13, D17): 202 pending, 200 merged (a repeat of a merged request, so retries are safe), 400 failed
    /// synchronously ("head branch was modified" is its own state), 409 a request already pending (its uuid
    /// is in the body).
    /// </summary>
    private static ForgeMergeRequest ReadMergeReply(Reply reply)
    {
        if (reply.Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests
            || (int)reply.Status >= 500)
            EnsureSuccess(reply);

        var root = reply.Json?.RootElement ?? default;
        var details = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("details", out var d) ? d : default;
        var message = Str(details, "message") ?? reply.Message;
        var uuid = Str(details, "uuid");
        var status = Str(root, "status");

        if (reply.Status == HttpStatusCode.Conflict)
            return new ForgeMergeRequest(EForgeMergeState.Pending, uuid, null, message);

        if (reply.Status == HttpStatusCode.BadRequest && message is not null
            && message.Contains("head branch was modified", StringComparison.OrdinalIgnoreCase))
            return new ForgeMergeRequest(EForgeMergeState.HeadModified, null, null, message);

        return status switch
        {
            "merged" => new ForgeMergeRequest(EForgeMergeState.Merged, null, Str(details, "sha"), message),
            "pending" or "queued" or "enqueued" => new ForgeMergeRequest(EForgeMergeState.Pending, uuid, null, message),
            "failed" => new ForgeMergeRequest(EForgeMergeState.Failed, null, null, message),
            // No envelope, or a word this code does not know: never read as a merge.
            _ => reply.IsSuccess && status is null && reply.Status == HttpStatusCode.Accepted
                ? new ForgeMergeRequest(EForgeMergeState.Pending, uuid, null, message)
                : new ForgeMergeRequest(EForgeMergeState.Failed, null, null,
                    message ?? $"GitHub answered {(int)reply.Status} with status '{status}'."),
        };
    }

    public async Task<IReadOnlyList<ForgeReview>> GetReviewsAsync(Repository repository, int number, CancellationToken cancellationToken = default)
    {
        var reviews = await GetPagedAsync(repository,
            $"{RepoPath(repository)}/pulls/{number.ToString(CultureInfo.InvariantCulture)}/reviews", null, cancellationToken);

        return [.. reviews.Select(review => new ForgeReview(
            Num(review, "id") ?? 0,
            review.TryGetProperty("user", out var user) ? Str(user, "login") : null,
            Str(review, "state") switch
            {
                "APPROVED" => EForgeReviewState.Approved,
                "CHANGES_REQUESTED" => EForgeReviewState.ChangesRequested,
                "COMMENTED" => EForgeReviewState.Commented,
                "DISMISSED" => EForgeReviewState.Dismissed,
                "PENDING" => EForgeReviewState.Pending,
                _ => EForgeReviewState.Other,
            },
            Str(review, "commit_id"),
            Str(review, "submitted_at") is { } submitted
                && DateTimeOffset.TryParse(submitted, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? at : null))];
    }

    /// <summary>
    /// <c>GET /pulls/{n}/commits</c>, every page (GitHub stops at 250 commits). <c>author</c> is the GitHub account the
    /// commit is attributed to, null when its email is linked to none; <c>commit.author</c> is what git recorded.
    /// </summary>
    public async Task<IReadOnlyList<ForgePullRequestCommit>> GetPullRequestCommitsAsync(Repository repository, int number, CancellationToken cancellationToken = default)
    {
        var commits = await GetPagedAsync(repository,
            $"{RepoPath(repository)}/pulls/{number.ToString(CultureInfo.InvariantCulture)}/commits", null, cancellationToken);

        return [.. commits.Select(item =>
        {
            var commit = item.TryGetProperty("commit", out var c) ? c : default;
            var gitAuthor = commit.ValueKind == JsonValueKind.Object && commit.TryGetProperty("author", out var a) ? a : default;
            var account = item.TryGetProperty("author", out var u) && u.ValueKind == JsonValueKind.Object ? u : default;
            return new ForgePullRequestCommit(
                Str(item, "sha") ?? string.Empty,
                Str(gitAuthor, "name"),
                Str(gitAuthor, "email"),
                Str(account, "login"),
                Str(account, "type") == "Bot",
                Str(commit, "message") ?? string.Empty);
        })];
    }

    // ── Feedback ─────────────────────────────────────────────────────────────────────────────────

    /// <remarks>
    /// ⚠️ GitHub does not reopen a completed check run reliably; a new attempt on the same commit should create
    /// a new run (pass no <paramref name="existingId"/>) rather than move a completed one back to in progress.
    /// </remarks>
    public async Task<long> PublishQueueCheckAsync(Repository repository, string sha, ForgeQueueCheck check, long? existingId, CancellationToken cancellationToken = default)
    {
        RequireSha(sha);
        ArgumentNullException.ThrowIfNull(check);
        if (check.DetailsUrl is { } detailsUrl
            && !(Uri.TryCreate(detailsUrl, UriKind.Absolute, out var parsed) && parsed.Scheme == Uri.UriSchemeHttps))
            throw new ArgumentException("A details URL must be absolute https.", nameof(check));

        var (status, conclusion) = check.State switch
        {
            EForgeQueueCheckState.Queued => (CheckStatus.Queued, (CheckConclusion?)null),
            EForgeQueueCheckState.InProgress => (CheckStatus.InProgress, (CheckConclusion?)null),
            EForgeQueueCheckState.Success => (CheckStatus.Completed, (CheckConclusion?)CheckConclusion.Success),
            EForgeQueueCheckState.Failure => (CheckStatus.Completed, (CheckConclusion?)CheckConclusion.Failure),
            EForgeQueueCheckState.Neutral => (CheckStatus.Completed, (CheckConclusion?)CheckConclusion.Neutral),
            EForgeQueueCheckState.Cancelled => (CheckStatus.Completed, (CheckConclusion?)CheckConclusion.Cancelled),
            _ => throw new ArgumentOutOfRangeException(nameof(check), check.State, "Unmapped queue state."),
        };
        var summary = check.Summary.Length > MaxSummaryLength ? check.Summary[..(MaxSummaryLength - 1)] + "…" : check.Summary;
        var output = new NewCheckRunOutput(check.Title, summary);

        var client = await ClientAsync(repository, cancellationToken);
        try
        {
            if (existingId is { } id)
            {
                await client.Check.Run.Update(repository.OwnerLogin, repository.Name, id, new CheckRunUpdate
                {
                    Status = status,
                    Conclusion = conclusion,
                    Output = output,
                    DetailsUrl = check.DetailsUrl,
                });
                return id;
            }

            var created = await client.Check.Run.Create(repository.OwnerLogin, repository.Name, new NewCheckRun(CheckGate.QueueCheckName, sha)
            {
                Status = status,
                Conclusion = conclusion,
                Output = output,
                DetailsUrl = check.DetailsUrl,
            });
            return created.Id;
        }
        catch (ApiException ex) when (ex.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new ForgeAccessDeniedException(ex.Message, "checks=write");
        }
    }

    public async Task AddLabelAsync(Repository repository, int number, string label, CancellationToken cancellationToken = default)
    {
        RequireLabel(label);
        var client = await ClientAsync(repository, cancellationToken);
        try
        {
            await client.Issue.Labels.AddToIssue(repository.OwnerLogin, repository.Name, number, [label]);
        }
        catch (ApiException ex) when (ex.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new ForgeAccessDeniedException(ex.Message, "pull_requests=write");
        }
    }

    public async Task<bool> RemoveLabelAsync(Repository repository, int number, string label, CancellationToken cancellationToken = default)
    {
        RequireLabel(label);
        var client = await ClientAsync(repository, cancellationToken);
        try
        {
            await client.Issue.Labels.RemoveFromIssue(repository.OwnerLogin, repository.Name, number, label);
            return true;
        }
        catch (NotFoundException)
        {
            // "Label does not exist" on the issue: the state we wanted.
            return false;
        }
        catch (ApiException ex) when (ex.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new ForgeAccessDeniedException(ex.Message, "pull_requests=write");
        }
    }

    public async Task<long> PostCommentAsync(Repository repository, int number, string body, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(body);
        var client = await ClientAsync(repository, cancellationToken);
        try
        {
            // A pull request is an issue for this API, as in GitHubPullRequestCommentGateway.
            var created = await client.Issue.Comment.Create(repository.OwnerLogin, repository.Name, number, body);
            return created.Id;
        }
        catch (ApiException ex) when (ex.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new ForgeAccessDeniedException(ex.Message, "pull_requests=write");
        }
    }

    // ── The writer's credential (T9) ─────────────────────────────────────────────────────────────

    /// <remarks>
    /// <para>
    /// <b>One token per job (T9, §0 D59).</b> <c>POST /app/installations/{id}/access_tokens</c> as the App, with
    /// <c>repository_ids</c> holding this repository alone and <c>permissions</c> holding <c>contents: write</c>, plus
    /// <c>workflows: write</c> only when the job's push may carry workflow files. It is never cached: the shared
    /// installation token (every repository of the installation, every permission it was granted, cached for its hour)
    /// is never handed to the writer. GitHub fixes the lifetime at one hour; <see cref="RevokeWriterAccessAsync"/> ends it
    /// sooner.
    /// </para>
    /// <para>
    /// The token is scoped to the <b>base</b> repository for a fork's push too: GitHub's "Allow edits from maintainers"
    /// lets those with push access to the base repository push to the fork's head branch, and the App has no token for a
    /// fork it is not installed on. ⚠️ S6 never ran: whether GitHub counts an App's token as such a maintainer is
    /// unverified. If it does not, the writer's push answers <c>push-rejected</c>, and the queue says fork pull requests
    /// cannot be queued yet rather than a generic failure. GitHub documents the checkbox for user-owned forks only; an
    /// organization-owned fork reports <c>maintainer_can_modify: false</c> and is refused here.
    /// </para>
    /// </remarks>
    public async Task<ForgePushAccess> GetWriterAccessAsync(Repository repository, ForgePullRequest? pullRequest, bool pushesWorkflowChanges, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repository);

        string target;
        if (pullRequest is null || !pullRequest.IsFromFork)
            target = repository.FullName;
        else if (pullRequest.HeadRepositoryFullName is not { } fork)
            return ForgePushAccess.Refused("The pull request's fork no longer exists, so there is no branch to push to.");
        else if (!pullRequest.MaintainerCanModify)
            return ForgePushAccess.Refused(
                $"The merge queue pushes the rebased commit to {fork}, which needs \"Allow edits by maintainers\" ticked on the pull request. "
                + "GitHub offers that box only for forks owned by a user, not by an organization.");
        else
            target = fork;

        if (!GitHubForgeClient.GitHubFullName().IsMatch(repository.FullName) || !GitHubForgeClient.GitHubFullName().IsMatch(target))
            return ForgePushAccess.Refused($"'{target}' is not a GitHub repository name.");

        var installationId = await ResolveInstallationAsync(repository, cancellationToken);
        if (installationId is null)
            return ForgePushAccess.Refused("No GitHub App installation for this repository.");

        var token = await MintJobTokenAsync(repository, installationId.Value, pushesWorkflowChanges, cancellationToken);
        return new ForgePushAccess(new ForgeWriterAccess($"https://github.com/{repository.FullName}.git", $"https://github.com/{target}.git", "x-access-token", token), null);
    }

    /// <summary>
    /// The job's own token (T9, §0 D59). A 401, 403, 404 or 422 is the installation refusing what was asked, typically
    /// <c>workflows: write</c> not accepted yet (GitHub: "cannot be granted permissions that the app was not granted"), so it
    /// is a <see cref="ForgeAccessDeniedException"/> naming the permission, for O17's link.
    /// </summary>
    private async Task<string> MintJobTokenAsync(Repository repository, long installationId, bool workflows, CancellationToken cancellationToken)
    {
        var permissions = new SortedDictionary<string, string>(StringComparer.Ordinal) { ["contents"] = "write" };
        if (workflows) permissions["workflows"] = "write";
        var body = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["repository_ids"] = new[] { repository.GitHubId },
            ["permissions"] = permissions,
        });
        var asked = string.Join(", ", permissions.Select(p => $"{p.Key}={p.Value}"));

        var app = await installationService.CreateAppClientAsync();
        try
        {
            var response = await app.Connection.Post<AccessToken>(
                new Uri($"app/installations/{installationId.ToString(CultureInfo.InvariantCulture)}/access_tokens", UriKind.Relative),
                body, "application/vnd.github+json", "application/json", parameters: null, cancellationToken);
            return response.Body?.Token is { Length: > 0 } token
                ? token
                : throw new ForgeRequestException($"GitHub minted no token for {repository.FullName}.", (int)response.HttpResponse.StatusCode);
        }
        catch (ApiException ex) when (ex is RateLimitExceededException or SecondaryRateLimitExceededException)
        {
            throw new ForgeRequestException($"GitHub rate limit reached minting a token for {repository.FullName}.", 429);
        }
        catch (ApiException ex) when (ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound or HttpStatusCode.UnprocessableEntity)
        {
            throw new ForgeAccessDeniedException($"GitHub refused a token for {repository.FullName} with {asked}: {ex.Message}", workflows ? "workflows=write" : "contents=write");
        }
        catch (ApiException ex)
        {
            throw new ForgeRequestException($"GitHub answered {(int)ex.StatusCode} minting a token for {repository.FullName}: {ex.Message}", (int)ex.StatusCode);
        }
    }

    /// <summary>The bound on a revocation: it runs after every writer job, and a slow GitHub must not hold the step.</summary>
    private static readonly TimeSpan RevokeTimeout = TimeSpan.FromSeconds(10);

    /// <summary><c>DELETE /installation/token</c>, authenticated with the job's token itself: 204 means it is dead now.</summary>
    public async Task<bool> RevokeWriterAccessAsync(Repository repository, ForgeWriterAccess access, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(access);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RevokeTimeout);
        try
        {
            var http = httpClientFactory.CreateClient(nameof(GitHubForgeMergeQueue));
            using var request = NewRequest(HttpMethod.Delete, new Uri(ApiBase, "installation/token"), access.Password, DefaultApiVersion);
            using var response = await http.SendAsync(request, timeout.Token);
            if (response.StatusCode == HttpStatusCode.NoContent) return true;
            logger.LogWarning("Revoking a merge-queue writer token for {FullName} answered {Status}; it expires within the hour.", repository.FullName, (int)response.StatusCode);
            return false;
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            logger.LogWarning(ex, "Could not revoke a merge-queue writer token for {FullName}; it expires within the hour.", repository.FullName);
            return false;
        }
    }

    // ── Plumbing ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>One response: its status, its JSON body when it had one, and the permission GitHub says it wanted.</summary>
    private sealed class Reply(HttpStatusCode status, JsonDocument? json, string? acceptedPermissions, string method, string path) : IDisposable
    {
        public HttpStatusCode Status { get; } = status;
        public JsonDocument? Json { get; } = json;
        public string? AcceptedPermissions { get; } = acceptedPermissions;
        public string Method { get; } = method;
        public string Path { get; } = path;
        public bool IsSuccess => (int)Status is >= 200 and < 300;

        /// <summary>GitHub's <c>message</c>, when the body is an object that has one.</summary>
        public string? Message => Json?.RootElement is { ValueKind: JsonValueKind.Object } root ? Str(root, "message") : null;

        public void Dispose() => Json?.Dispose();
    }

    private async Task<Reply> SendAsync(Repository repository, HttpMethod method, string path, object? body, string apiVersion, CancellationToken cancellationToken)
    {
        var token = await TokenAsync(repository, cancellationToken);
        var http = httpClientFactory.CreateClient(nameof(GitHubForgeMergeQueue));

        using var request = NewRequest(method, new Uri(ApiBase, path), token, apiVersion);
        if (body is not null)
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        using var response = await http.SendAsync(request, cancellationToken);
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);

        JsonDocument? json = null;
        if (bytes.Length > 0)
        {
            try { json = JsonDocument.Parse(bytes); }
            catch (JsonException) { /* not JSON: an HTML error page from a proxy; the status decides */ }
        }

        if (response.StatusCode == HttpStatusCode.Forbidden && IsRateLimited(response))
        {
            json?.Dispose();
            throw new ForgeRequestException($"GitHub rate limit reached on {method} {path}.", 429);
        }
        return new Reply(response.StatusCode, json, AcceptedPermissions(response), method.Method, path);
    }

    private static HttpRequestMessage NewRequest(HttpMethod method, Uri uri, string token, string apiVersion)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", apiVersion);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("CodeCoverage", "1.0"));
        return request;
    }

    private static bool IsRateLimited(HttpResponseMessage response)
        => response.Headers.TryGetValues("x-ratelimit-remaining", out var values) && values.FirstOrDefault() == "0";

    private static string? AcceptedPermissions(HttpResponseMessage response)
        => response.Headers.TryGetValues("X-Accepted-GitHub-Permissions", out var values) ? values.FirstOrDefault() : null;

    /// <summary>Throws the neutral exception for anything that is not a 2xx.</summary>
    private static void EnsureSuccess(Reply reply)
    {
        if (reply.IsSuccess) return;

        var what = $"GitHub answered {(int)reply.Status} to {reply.Method} {reply.Path}: {reply.Message ?? "no message"}";
        throw reply.Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
            ? new ForgeAccessDeniedException(what, reply.AcceptedPermissions)
            : new ForgeRequestException(what, (int)reply.Status);
    }

    /// <summary>
    /// Every item of a listing, following <c>page</c> until a short page. <paramref name="arrayProperty"/> names
    /// the array in an object envelope (<c>check_runs</c>); null for a listing that is a bare array.
    /// </summary>
    private async Task<IReadOnlyList<JsonElement>> GetPagedAsync(Repository repository, string path, string? arrayProperty, CancellationToken cancellationToken)
    {
        var items = new List<JsonElement>();
        var separator = path.Contains('?') ? '&' : '?';
        for (var page = 1; page <= MaxPages; page++)
        {
            using var reply = await SendAsync(repository, HttpMethod.Get,
                $"{path}{separator}per_page={PageSize}&page={page.ToString(CultureInfo.InvariantCulture)}", null, DefaultApiVersion, cancellationToken);
            EnsureSuccess(reply);

            var root = reply.Json?.RootElement ?? default;
            var array = arrayProperty is null ? root : root.ValueKind == JsonValueKind.Object && root.TryGetProperty(arrayProperty, out var a) ? a : default;
            if (array.ValueKind != JsonValueKind.Array)
                throw new ForgeRequestException($"GitHub's answer to GET {path} is not a listing.", (int)reply.Status);

            var count = 0;
            foreach (var item in array.EnumerateArray())
            {
                items.Add(item.Clone());
                count++;
            }
            if (count < PageSize) return items;
        }

        logger.LogWarning("GET {Path} on {FullName} has more than {Max} items; the rest are ignored.", path, repository.FullName, MaxPages * PageSize);
        return items;
    }

    private async Task<string> TokenAsync(Repository repository, CancellationToken cancellationToken)
    {
        var installationId = await ResolveInstallationAsync(repository, cancellationToken)
            ?? throw new ForgeAccessDeniedException($"No GitHub App installation for {repository.FullName}.");
        return await installationService.GetInstallationTokenAsync(installationId, cancellationToken);
    }

    private async Task<IGitHubClient> ClientAsync(Repository repository, CancellationToken cancellationToken)
    {
        var installationId = await ResolveInstallationAsync(repository, cancellationToken)
            ?? throw new ForgeAccessDeniedException($"No GitHub App installation for {repository.FullName}.");
        return await installationService.CreateInstallationClientAsync(installationId);
    }

    private async Task<long?> ResolveInstallationAsync(Repository repository, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(repository);
        return repository.Account is null
            ? null
            : (await session.LoadAsync<Account>(repository.Account, cancellationToken))?.InstallationId;
    }

    /// <summary><c>repos/{owner}/{name}</c>, from the stored names, escaped.</summary>
    private static string RepoPath(Repository repository)
        => $"repos/{Uri.EscapeDataString(repository.OwnerLogin)}/{Uri.EscapeDataString(repository.Name)}";

    /// <summary>A branch name in a path: each segment escaped, the slashes kept (GitHub routes <c>a/b</c> as one name).</summary>
    private static string BranchPath(string branch)
    {
        ArgumentException.ThrowIfNullOrEmpty(branch);
        var segments = branch.Split('/');
        if (segments.Any(segment => segment is "" or "." or ".."))
            throw new ArgumentException($"'{branch}' is not a branch name.", nameof(branch));
        return string.Join('/', segments.Select(Uri.EscapeDataString));
    }

    private static void RequireSha(string sha)
    {
        if (string.IsNullOrEmpty(sha) || !CommitSha().IsMatch(sha))
            throw new ArgumentException($"'{sha}' is not a commit sha.", nameof(sha));
    }

    private static void RequireLabel(string label)
    {
        if (string.IsNullOrWhiteSpace(label) || label.Length > 50)
            throw new ArgumentException("A label is 1 to 50 characters.", nameof(label));
    }

    private static string? Str(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long? Num(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out var number)
            ? number
            : null;

    private static bool Flag(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static IEnumerable<JsonElement> Items(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray()
            : [];

    [GeneratedRegex("^[A-Za-z0-9](?:[A-Za-z0-9-]{0,38})$")]
    private static partial Regex GitHubLogin();

    [GeneratedRegex("^[0-9a-fA-F]{40}([0-9a-fA-F]{24})?$")]
    private static partial Regex CommitSha();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,99}\.ya?ml$")]
    private static partial Regex WorkflowFileName();
}
