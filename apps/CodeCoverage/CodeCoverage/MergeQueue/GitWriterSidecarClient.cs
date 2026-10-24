using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeCoverage.CommitGraph;
using CodeCoverage.Entities;
using Microsoft.Extensions.Options;

namespace CodeCoverage.MergeQueue;

/// <summary>The <c>MergeQueue</c> configuration section: the git writer's URL and request bound, and the saga's settings
/// (docs/code-coverage/merge-queue.md §7.3, hosting.md §4).</summary>
public sealed class MergeQueueOptions
{
    public const string Section = "MergeQueue";

    /// <summary>The git writer's base URL, <c>http://coverage-git-writer:8080</c> in production. Unset means the merge
    /// queue's git steps are off (<see cref="IGitWriterSidecar.IsConfigured"/>).</summary>
    public string? GitWriterUrl { get; set; }

    /// <summary>The bound on one job request: the writer's own job timeout (180 s) plus a slot wait (30 s) and a margin.
    /// Under the messaging ClaimTtl of 5 minutes (§5.5).</summary>
    public int JobRequestTimeoutSeconds { get; set; } = 240;

    // ── The saga (M5) ────────────────────────────────────────────────────────────────────────────────

    /// <summary>The committer of every commit the queue writes.</summary>
    public string CommitterName { get; set; } = "CodeCoverage merge queue";

    public string CommitterEmail { get; set; } = "merge-queue@noreply.coverage.mintplayer.com";

    /// <summary>
    /// The author email of the rebased commit, from the pull request author's login (<c>{login}</c>). Cosmetic: the
    /// forge's squash merge sets the merged commit's author itself; this only shows on the pull request's branch.
    /// </summary>
    public string AuthorEmailFormat { get; set; } = "{login}@users.noreply.github.com";

    /// <summary>The artifact the regenerate workflow uploads (<c>.github/actions/spark-regenerate</c>, D35).</summary>
    public string PatchArtifactName { get; set; } = "merge-queue-patch";

    /// <summary>The largest regenerate patch accepted, the writer's own <c>MaxPatchBytes</c> (D30).</summary>
    public long MaxPatchBytes { get; set; } = 8 * 1024 * 1024;
}

/// <summary>
/// The app's side of the merge queue's git writer, <c>coverage-git-writer</c> (docs/coverage_merge_queue_PRD.md D2, M3).
/// </summary>
/// <remarks>
/// <para><b>No authentication, by design.</b> The writer is reachable only on the internal two-host network
/// <c>coverage-git-writer</c>; the network is the boundary. The app mints a short-lived, single-repository installation
/// token per request and sends it in the body (<see cref="SidecarCredential"/>); the writer never holds the App key.</para>
/// <para>Every job answers 200 with an outcome (<see cref="GitWriterOutcomes"/>); only a transport failure or a refusal
/// throws. A refusal (a 400 naming the field and its rule) is <see cref="GitWriterRefusedException"/>: the same request is
/// refused every time, so it is never retried (§0 D57).</para>
/// </remarks>
public interface IGitWriterSidecar
{
    /// <summary>False when <c>MergeQueue:GitWriterUrl</c> is unset.</summary>
    bool IsConfigured { get; }

    /// <summary>§5.1 step [1]: squash and rebase the pinned head onto the base and answer the commit. Pushes nothing (§0 D65).</summary>
    Task<GitWriterPrepareResult> PrepareAsync(GitWriterPrepareRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// §0 D65: rebuild the prepared commit and push it to the queue branch under a lease. Only right before a dispatch: a
    /// push to a branch of the base repository starts its <c>push</c> workflows on the pushed code (D14, D19).
    /// </summary>
    Task<GitWriterQueueBranchResult> PushQueueBranchAsync(GitWriterQueueBranchRequest request, CancellationToken cancellationToken = default);

    /// <summary>§5.1 step [3]: rebuild the prepared commit, validate and apply the regenerate patch and replace the pull
    /// request's branch under a lease.</summary>
    Task<GitWriterPublishResult> PublishAsync(GitWriterPublishRequest request, CancellationToken cancellationToken = default);

    /// <summary>Removes the queue branch after its entry ended (D5). Idempotent.</summary>
    Task<GitWriterDeleteResult> DeleteQueueBranchAsync(GitWriterDeleteQueueBranchRequest request, CancellationToken cancellationToken = default);

    // ── The dependency updater (dependency-updates PRD R4, R14, R16) ───────────────────────────────────

    /// <summary>R14: <c>mintplayer/dependencies-work</c> = <c>expected_sha</c> + the plan commit, or + the validated apply patch.</summary>
    Task<GitWriterDependencyResult> DependencyWorkRefAsync(GitWriterDependencyRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("This git writer has no dependency jobs: DependencyWorkRefAsync.");

    /// <summary>The <c>dependencies</c> patch profile on an apply patch; nothing is pushed.</summary>
    Task<GitWriterDependencyResult> DependencyValidateAsync(GitWriterDependencyRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("This git writer has no dependency jobs: DependencyValidateAsync.");

    /// <summary>R4: rebuild <c>mintplayer/dependencies</c> (bot commit, migrations, the humans' commits replayed) and push it
    /// under a lease, or nothing when the tree is the branch's.</summary>
    Task<GitWriterDependencyResult> DependencyRollingAsync(GitWriterDependencyRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("This git writer has no dependency jobs: DependencyRollingAsync.");

    /// <summary>R16: the onboarding commit on <c>mintplayer/dependencies-onboarding</c>.</summary>
    Task<GitWriterDependencyResult> DependencyFilesAsync(GitWriterDependencyRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("This git writer has no dependency jobs: DependencyFilesAsync.");

    /// <summary>R14: deletes the work ref while it is at <see cref="GitWriterDependencyRequest.ExpectedSha"/>. Idempotent.</summary>
    Task<GitWriterDependencyResult> DependencyDeleteWorkRefAsync(GitWriterDependencyRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("This git writer has no dependency jobs: DependencyDeleteWorkRefAsync.");

    /// <summary>#153 M6: one server-host versioning run, planned and tagged by the engine inside the writer.</summary>
    Task<PackageVersioning.GitWriterVersioningResult> VersioningRunAsync(PackageVersioning.GitWriterVersioningRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("This git writer has no versioning job.");

    /// <summary>#153 M10: apply a merged pull request to release branches, or report the conflicts with nothing pushed.</summary>
    Task<PackageVersioning.GitWriterBackportResult> VersioningBackportAsync(PackageVersioning.GitWriterBackportRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("This git writer has no backport job.");

    /// <summary>#153 M11: the release plan for a pushed commit, without a build and without writing anything.</summary>
    Task<PackageVersioning.GitWriterVersioningPlanResult> VersioningPlanAsync(PackageVersioning.GitWriterVersioningPlanRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("This git writer has no versioning plan job.");

    /// <summary>#153 D17: the versioning onboarding commit on <c>mintplayer/versioning-onboarding</c>.</summary>
    Task<GitWriterDependencyResult> VersioningOnboardingAsync(GitWriterDependencyRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("This git writer has no versioning onboarding job.");

    /// <summary>#153 D17: version tags pushed outside a run (the onboarding's seed tags), never with force.</summary>
    Task<PackageVersioning.GitWriterTagsResult> TagsAsync(PackageVersioning.GitWriterTagsRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("This git writer has no tags job.");
}

/// <summary>A typed <see cref="HttpClient"/> over the writer's contract.</summary>
public sealed class GitWriterSidecarClient(HttpClient http, IOptions<MergeQueueOptions> options) : IGitWriterSidecar
{
    private static readonly JsonSerializerOptions Json = JsonSerializerOptions.Web;

    public bool IsConfigured => http.BaseAddress is not null;

    public Task<GitWriterPrepareResult> PrepareAsync(GitWriterPrepareRequest request, CancellationToken cancellationToken = default)
        => SendAsync<GitWriterPrepareResult>(HttpMethod.Post, "jobs/prepare", request, cancellationToken);

    public Task<GitWriterQueueBranchResult> PushQueueBranchAsync(GitWriterQueueBranchRequest request, CancellationToken cancellationToken = default)
        => SendAsync<GitWriterQueueBranchResult>(HttpMethod.Post, "jobs/queue-branch", request, cancellationToken);

    public Task<GitWriterPublishResult> PublishAsync(GitWriterPublishRequest request, CancellationToken cancellationToken = default)
        => SendAsync<GitWriterPublishResult>(HttpMethod.Post, "jobs/publish", request, cancellationToken);

    public Task<GitWriterDeleteResult> DeleteQueueBranchAsync(GitWriterDeleteQueueBranchRequest request, CancellationToken cancellationToken = default)
        => SendAsync<GitWriterDeleteResult>(HttpMethod.Delete, "jobs/queue-branch", request, cancellationToken);

    public Task<GitWriterDependencyResult> DependencyWorkRefAsync(GitWriterDependencyRequest request, CancellationToken cancellationToken = default)
        => SendAsync<GitWriterDependencyResult>(HttpMethod.Post, "jobs/dependencies/work-ref", request, cancellationToken);

    public Task<GitWriterDependencyResult> DependencyValidateAsync(GitWriterDependencyRequest request, CancellationToken cancellationToken = default)
        => SendAsync<GitWriterDependencyResult>(HttpMethod.Post, "jobs/dependencies/validate", request, cancellationToken);

    public Task<GitWriterDependencyResult> DependencyRollingAsync(GitWriterDependencyRequest request, CancellationToken cancellationToken = default)
        => SendAsync<GitWriterDependencyResult>(HttpMethod.Post, "jobs/dependencies/rolling", request, cancellationToken);

    public Task<GitWriterDependencyResult> DependencyFilesAsync(GitWriterDependencyRequest request, CancellationToken cancellationToken = default)
        => SendAsync<GitWriterDependencyResult>(HttpMethod.Post, "jobs/dependencies/files", request, cancellationToken);

    public Task<GitWriterDependencyResult> DependencyDeleteWorkRefAsync(GitWriterDependencyRequest request, CancellationToken cancellationToken = default)
        => SendAsync<GitWriterDependencyResult>(HttpMethod.Delete, "jobs/dependencies/work-ref", request, cancellationToken);

    public Task<GitWriterDependencyResult> VersioningOnboardingAsync(GitWriterDependencyRequest request, CancellationToken cancellationToken = default)
        => SendAsync<GitWriterDependencyResult>(HttpMethod.Post, "jobs/versioning/onboarding", request, cancellationToken);

    public Task<PackageVersioning.GitWriterTagsResult> TagsAsync(PackageVersioning.GitWriterTagsRequest request, CancellationToken cancellationToken = default)
        => SendAsync<PackageVersioning.GitWriterTagsResult>(HttpMethod.Post, "jobs/tags", request, cancellationToken);

    public Task<PackageVersioning.GitWriterVersioningResult> VersioningRunAsync(PackageVersioning.GitWriterVersioningRequest request, CancellationToken cancellationToken = default)
        => SendAsync<PackageVersioning.GitWriterVersioningResult>(HttpMethod.Post, "jobs/versioning/run", request, cancellationToken);

    public Task<PackageVersioning.GitWriterBackportResult> VersioningBackportAsync(PackageVersioning.GitWriterBackportRequest request, CancellationToken cancellationToken = default)
        => SendAsync<PackageVersioning.GitWriterBackportResult>(HttpMethod.Post, "jobs/versioning/backport", request, cancellationToken);

    public Task<PackageVersioning.GitWriterVersioningPlanResult> VersioningPlanAsync(PackageVersioning.GitWriterVersioningPlanRequest request, CancellationToken cancellationToken = default)
        => SendAsync<PackageVersioning.GitWriterVersioningPlanResult>(HttpMethod.Post, "jobs/versioning/plan", request, cancellationToken);

    /// <summary>
    /// The request as the writer reads it: one flat object. The queue-branch push and the publish carry their prepare's
    /// fields (<see cref="GitWriterQueueBranchRequest.Prepare"/>) at the top level, beside their own, as the writer's
    /// request types extend the prepare's (§0 D65).
    /// </summary>
    internal static JsonObject Body(object request)
    {
        var body = JsonSerializer.SerializeToNode(request, request.GetType(), Json)!.AsObject();
        if (body["prepare"] is JsonObject prepare)
        {
            body.Remove("prepare");
            foreach (var (key, value) in prepare.ToList())
            {
                prepare.Remove(key);
                body[key] = value;
            }
        }
        return body;
    }

    private async Task<T> SendAsync<T>(HttpMethod method, string path, object request, CancellationToken cancellationToken)
    {
        if (!IsConfigured)
            throw new InvalidOperationException("MergeQueue:GitWriterUrl is not configured.");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(options.Value.JobRequestTimeoutSeconds));
        using var message = new HttpRequestMessage(method, path) { Content = JsonContent.Create(Body(request), options: Json) };
        using var response = await http.SendAsync(message, cts.Token);
        if (response.StatusCode == HttpStatusCode.BadRequest)
            throw await RefusedAsync(path, response, cts.Token);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(Json, cts.Token)
            ?? throw new InvalidOperationException($"The git writer answered {path} with an empty body.");
    }

    /// <summary>The writer's refusal, read from its body; a writer older than §0 D57 sent none.</summary>
    private static async Task<GitWriterRefusedException> RefusedAsync(string path, HttpResponseMessage response, CancellationToken cancellationToken)
    {
        GitWriterRefusal? refusal = null;
        try
        {
            refusal = await response.Content.ReadFromJsonAsync<GitWriterRefusal>(Json, cancellationToken);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
        }
        var job = path.StartsWith("jobs/", StringComparison.Ordinal) ? path["jobs/".Length..] : path;
        return new GitWriterRefusedException(job, refusal?.Field ?? "request", refusal?.Detail ?? "valid (the writer gave no reason)");
    }

    private sealed record GitWriterRefusal(string? Outcome, string? Field, string? Detail);
}

/// <summary>
/// The git writer refused a request: a value broke one of its rules (<c>MergeQueueWireRules</c>). Sending it again changes
/// nothing, so the saga ends the entry with the writer's reason instead of retrying (§0 D57).
/// </summary>
public sealed class GitWriterRefusedException(string job, string field, string rule)
    : Exception($"The git writer refused the {job} request: {field} must be {rule}.")
{
    /// <summary><c>prepare</c>, <c>queue-branch</c> (its push or its deletion) or <c>publish</c>.</summary>
    public string Job { get; } = job;

    /// <summary>The first field that broke its rule, as the wire names it (<c>regeneratedPaths</c>, <c>prBranch</c>, …).</summary>
    public string Field { get; } = field;

    public string Rule { get; } = rule;
}

/// <summary>A commit identity. The writer requires <paramref name="Date"/> (§0 D65): the saga pins it per run, so every
/// job of the run builds the same commit from the same request.</summary>
public sealed record GitWriterIdentity(string Name, string Email, DateTimeOffset Date);

/// <summary>One auto-resolve entry: a path glob and its strategy (§10.3; null is take-base). For take-base, the top-level
/// JSON keys whose lines may conflict (O2), or <paramref name="AnyLine"/> for a wholly generated file whose every line may
/// (O19). <paramref name="Validate"/> and <paramref name="Regenerate"/> as in <c>coverage.yml</c>.</summary>
public sealed record GitWriterAutoResolve(string Path, IReadOnlyList<string>? Keys = null, bool AnyLine = false, string? Strategy = null,
    IReadOnlyList<string>? Validate = null, bool? Regenerate = null)
{
    /// <summary>
    /// A stored rule as the writer reads it (§10.3). A take-base rule without keys is any line (O19). The strategy is sent
    /// by name only when it is not take-base, so an older writer still reads a take-base-only configuration; the
    /// regenerate choice is the effective one the parser stored (O36).
    /// </summary>
    public static GitWriterAutoResolve From(MergeQueueAutoResolveRule rule)
    {
        var validate = rule.Validate.Count == 0 ? null : rule.Validate;
        return rule.Strategy == EMergeQueueResolveStrategy.TakeBase
            ? new GitWriterAutoResolve(rule.Path, rule.Keys.Count == 0 ? null : rule.Keys, AnyLine: rule.Keys.Count == 0, Validate: validate)
            : new GitWriterAutoResolve(rule.Path, Strategy: MergeQueueAutoResolveRule.WireName(rule.Strategy), Validate: validate, Regenerate: rule.Regenerate);
    }

    /// <summary>O19's default: <c>modelHashes.json</c> and <c>securityPosture.txt</c>, any line, take-base.</summary>
    public static IReadOnlyList<GitWriterAutoResolve> SparkGeneratedFiles { get; } =
        [new("**/App_Data/modelHashes.json", AnyLine: true), new("**/App_Data/securityPosture.txt", AnyLine: true)];
}

/// <summary>
/// The body of <c>POST /jobs/prepare</c>, and the fields the queue-branch push and the publish repeat so the writer rebuilds
/// the same commit (§0 D65). <paramref name="BaseSha"/>: for the prepare, the base branch must still be there; for the
/// others, the base the prepared commit was built on. <paramref name="HeadSha"/> is the publish's lease too.
/// </summary>
public sealed record GitWriterPrepareRequest(
    string Provider, string RepoId, string CloneUrl, SidecarCredential Credential, string BaseBranch, string? BaseSha,
    string HeadRef, string HeadSha, IReadOnlyList<GitWriterAutoResolve> AutoResolve,
    GitWriterIdentity Author, GitWriterIdentity Committer, string Message);

/// <summary>The body of <c>POST /jobs/queue-branch</c> (§0 D65): the prepare's fields (sent at the top level), the commit
/// the prepare answered, and the lease; <paramref name="ExpectedQueueSha"/> null means the queue branch must not exist.</summary>
/// <param name="HeadFromFork">The pull request's head lives in a fork. The writer then refuses a commit whose
/// <c>.github</c> differs from its base's (<c>fork-touches-workflows</c>), whatever else the request says (O25, §0 D67).
/// Required on the wire.</param>
public sealed record GitWriterQueueBranchRequest(GitWriterPrepareRequest Prepare, string PreparedSha, string QueueBranch, string? ExpectedQueueSha, bool HeadFromFork);

/// <summary>The body of <c>POST /jobs/publish</c>: the prepare's fields (sent at the top level) and the commit it
/// answered. <paramref name="Patch"/> is the regenerate artifact's bytes, base64, or null when nothing was regenerated;
/// <paramref name="PushUrl"/> is the fork's clone URL for a fork's pull request (O3).</summary>
public sealed record GitWriterPublishRequest(
    GitWriterPrepareRequest Prepare, string PreparedSha, string? PushUrl, string PrBranch, string? Patch, IReadOnlyList<string> RegeneratedPaths)
{
    /// <summary>O36: one patch per regenerate workflow run, in the order the writer applies them (workflow name, ordinal).
    /// Null sends the single <see cref="Patch"/> an older writer reads.</summary>
    public IReadOnlyList<GitWriterPatch>? Patches { get; init; }
}

/// <summary>One regenerate run's patch (O36): the workflow it came from and its <c>git diff</c>, base64 (empty: no
/// change).</summary>
public sealed record GitWriterPatch(string Workflow, string Patch);

/// <summary>How the writer resolved one conflicted path (§10.6); <paramref name="Rule"/> indexes the request's
/// <c>autoResolve</c>.</summary>
public sealed record GitWriterResolution(string Path, string Strategy, bool Regenerate, int Rule);

/// <summary>The body of <c>DELETE /jobs/queue-branch</c>.</summary>
public sealed record GitWriterDeleteQueueBranchRequest(string Provider, string RepoId, string CloneUrl, SidecarCredential Credential, string QueueBranch, string? ExpectedSha);

/// <param name="Paths">Why, for <c>evicted</c>. For <c>prepared</c>: the <c>.github/**</c> paths the commit changes
/// against its base.</param>
public sealed record GitWriterPrepareResult(
    string Outcome, string? Reason, string? BaseSha, string? HeadSha, string? PreparedSha,
    IReadOnlyList<string> ResolvedPaths, IReadOnlyList<string> Paths, string? Detail, long DurationMs)
{
    /// <summary>The commit changes <c>.github/**</c> against its base (D12, §0 D65); the saga decides what that stops
    /// (<see cref="MergeQueueWorkflowGuard"/>).</summary>
    public bool TouchesWorkflows { get; init; }

    /// <summary>The commit's <c>.github/workflows</c> differs from the pinned head's (§0 D59, D67): the base changed
    /// workflow files since, which a fork's branch lacks and no App may write there (§0 D69, D71).</summary>
    public bool BaseChangedWorkflows { get; init; }

    /// <summary>How each resolved path was resolved (§10.6). Null from a writer older than resolution v2: the saga then
    /// treats every resolved path as needing regeneration, as before.</summary>
    public IReadOnlyList<GitWriterResolution>? Resolutions { get; init; }

    /// <summary>The writer's resolver version and git version (O35 #4); null from an older writer.</summary>
    public string? ResolverVersion { get; init; }
}

/// <param name="PreparedSha">For <c>pushed</c>: the commit the queue branch holds now.</param>
/// <param name="Paths">Why, for <c>evicted</c> and <c>fork-touches-workflows</c>.</param>
public sealed record GitWriterQueueBranchResult(string Outcome, string? Reason, string? PreparedSha, IReadOnlyList<string> Paths, string? Detail, long DurationMs)
{
    /// <summary>With <c>prepared-differs</c>: the writer's resolver version now (O35 #4).</summary>
    public string? ResolverVersion { get; init; }
}

public sealed record GitWriterPublishResult(string Outcome, string? Reason, string? FinalSha, IReadOnlyList<string> Paths, string? Detail, long DurationMs)
{
    /// <summary>With <c>prepared-differs</c>: the writer's resolver version now (O35 #4).</summary>
    public string? ResolverVersion { get; init; }
}

public sealed record GitWriterDeleteResult(string Outcome, string? Reason, string? Detail, long DurationMs);

/// <summary>
/// The body of every dependency job (the writer's <c>Dependency*Wire</c>s, one flat object). Each job reads the fields it
/// takes; the rest go as null.
/// </summary>
public sealed record GitWriterDependencyRequest(string Provider, string RepoId, string CloneUrl, SidecarCredential Credential, string BaseBranch, string? ExpectedSha)
{
    public GitWriterIdentity? Author { get; init; }
    public GitWriterIdentity? Committer { get; init; }
    public string? Message { get; init; }

    /// <summary>The plan as committed (plan.schema.json text), for the jobs that judge a patch against it.</summary>
    public string? Plan { get; init; }

    /// <summary>The apply patch, base64.</summary>
    public string? Patch { get; init; }

    public IReadOnlyList<string>? ClaimedGroups { get; init; }
    public IReadOnlyDictionary<string, string>? TargetActions { get; init; }
    public IReadOnlyList<string>? AllowedHosts { get; init; }

    /// <summary>Work ref: the plan file's content for the plan commit (R14).</summary>
    public string? PlanJson { get; init; }

    public string? ExpectedWorkSha { get; init; }

    public IReadOnlyList<GitWriterMigratePatch>? Migrations { get; init; }
    public string? ExpectedRollingSha { get; init; }
    public string? LastBotSha { get; init; }

    public IReadOnlyList<GitWriterFile>? Files { get; init; }
}

public sealed record GitWriterMigratePatch(string Group, string Patch);

/// <summary>One onboarding file: <paramref name="Content"/>, or <paramref name="Delete"/>.</summary>
public sealed record GitWriterFile(string Path, string? Content, bool Delete = false);

/// <summary>A dependency job's answer (CodeCoverage.GitWriter's <c>DependencyJobResponse</c>).</summary>
public sealed record GitWriterDependencyResult(string Outcome, string? Reason, string? Sha, string? BotSha, IReadOnlyList<string> Paths, string? Detail, long DurationMs)
{
    public string? Verdict { get; init; }
    public IReadOnlyList<string> AppliedGroups { get; init; } = [];
    public bool RequiresWorkflowsPermission { get; init; }
    public IReadOnlyList<string> NewPackages { get; init; } = [];
    public IReadOnlyList<GitWriterHumanValue> HumanValues { get; init; } = [];
    public int Replayed { get; init; }
}

public sealed record GitWriterHumanValue(string DepName, string PackageFile, string HeldValue, string? ProposedValue, string Commit, string? Author);

/// <summary>The dependency jobs' own outcome words (CodeCoverage.GitWriter's <c>DependencyJobOutcomes</c>).</summary>
public static class GitWriterDependencyOutcomes
{
    public const string Validated = "validated";
    public const string Unchanged = "unchanged";
    public const string ReplayConflict = "replay-conflict";
    public const string RelockNeeded = "relock-needed";
    public const string RollingRewritten = "rolling-rewritten";
    public const string PlanRejected = "plan-rejected";
    public const string BranchMoved = "branch-moved";
    public const string Accepted = "accepted";
    public const string Rejected = "rejected";
    public const string NeedsHuman = "needs-human";
}

/// <summary>The writer's <c>outcome</c> values (CodeCoverage.GitWriter's <c>JobOutcomes</c>).</summary>
public static class GitWriterOutcomes
{
    public const string Prepared = "prepared";
    public const string Evicted = "evicted";
    public const string HeadMismatch = "head-mismatch";
    public const string BaseMoved = "base-moved";
    public const string Pushed = "pushed";

    /// <summary>The queue-branch push: the head is a fork's and the commit changes <c>.github</c> against its base, so
    /// nothing was pushed (O25, §0 D67).</summary>
    public const string ForkTouchesWorkflows = "fork-touches-workflows";

    public const string PreparedDiffers = "prepared-differs";
    public const string QueueBranchMoved = "queue-branch-moved";
    public const string PatchRejected = "patch-rejected";
    public const string BranchMoved = "branch-moved";
    public const string Published = "published";
    public const string PushRejected = "push-rejected";
    public const string Deleted = "deleted";
    public const string Busy = "busy";
    public const string Failed = "failed";
}

/// <summary>The writer's <c>reason</c> values the saga acts on (CodeCoverage.GitWriter's <c>JobReasons</c>).</summary>
public static class GitWriterReasons
{
    /// <summary><c>pushed</c>/<c>published</c> with the commit an earlier attempt of the same job pushed (§0 D52).</summary>
    public const string AlreadyPushed = "already-pushed";

    /// <summary><c>patch-rejected</c>: two regenerate workflows' patches change one path differently (O36).</summary>
    public const string Overlap = "overlap";

    /// <summary><c>patch-rejected</c>: a patched path fails a validator of its <c>autoResolve</c> rule (§10.4).</summary>
    public const string InvalidContent = "invalid-content";
}
