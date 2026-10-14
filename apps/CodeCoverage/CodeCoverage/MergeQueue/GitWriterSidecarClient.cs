using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CodeCoverage.CommitGraph;
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
    public long MaxPatchBytes { get; set; } = 2 * 1024 * 1024;
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

    /// <summary>§5.1 step [1]: squash and rebase the pinned head onto the base and push it to the queue branch.</summary>
    Task<GitWriterPrepareResult> PrepareAsync(GitWriterPrepareRequest request, CancellationToken cancellationToken = default);

    /// <summary>§5.1 step [3]: validate and apply the regenerate patch and replace the pull request's branch under a lease.</summary>
    Task<GitWriterPublishResult> PublishAsync(GitWriterPublishRequest request, CancellationToken cancellationToken = default);

    /// <summary>Removes the queue branch after its entry ended (D5). Idempotent.</summary>
    Task<GitWriterDeleteResult> DeleteQueueBranchAsync(GitWriterDeleteQueueBranchRequest request, CancellationToken cancellationToken = default);
}

/// <summary>A typed <see cref="HttpClient"/> over the writer's contract.</summary>
public sealed class GitWriterSidecarClient(HttpClient http, IOptions<MergeQueueOptions> options) : IGitWriterSidecar
{
    private static readonly JsonSerializerOptions Json = JsonSerializerOptions.Web;

    public bool IsConfigured => http.BaseAddress is not null;

    public Task<GitWriterPrepareResult> PrepareAsync(GitWriterPrepareRequest request, CancellationToken cancellationToken = default)
        => SendAsync<GitWriterPrepareResult>(HttpMethod.Post, "jobs/prepare", request, cancellationToken);

    public Task<GitWriterPublishResult> PublishAsync(GitWriterPublishRequest request, CancellationToken cancellationToken = default)
        => SendAsync<GitWriterPublishResult>(HttpMethod.Post, "jobs/publish", request, cancellationToken);

    public Task<GitWriterDeleteResult> DeleteQueueBranchAsync(GitWriterDeleteQueueBranchRequest request, CancellationToken cancellationToken = default)
        => SendAsync<GitWriterDeleteResult>(HttpMethod.Delete, "jobs/queue-branch", request, cancellationToken);

    private async Task<T> SendAsync<T>(HttpMethod method, string path, object body, CancellationToken cancellationToken)
    {
        if (!IsConfigured)
            throw new InvalidOperationException("MergeQueue:GitWriterUrl is not configured.");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(options.Value.JobRequestTimeoutSeconds));
        using var message = new HttpRequestMessage(method, path) { Content = JsonContent.Create(body, body.GetType(), options: Json) };
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
    /// <summary><c>prepare</c>, <c>publish</c> or <c>queue-branch</c>.</summary>
    public string Job { get; } = job;

    /// <summary>The first field that broke its rule, as the wire names it (<c>regeneratedPaths</c>, <c>prBranch</c>, …).</summary>
    public string Field { get; } = field;

    public string Rule { get; } = rule;
}

/// <summary>A commit identity; <paramref name="Date"/> null lets the writer use the current time.</summary>
public sealed record GitWriterIdentity(string Name, string Email, DateTimeOffset? Date = null);

/// <summary>One auto-resolve entry: a path glob and the top-level JSON keys whose lines may conflict (O2), or
/// <paramref name="AnyLine"/> for a wholly generated file whose every line may (O19).</summary>
public sealed record GitWriterAutoResolve(string Path, IReadOnlyList<string>? Keys = null, bool AnyLine = false)
{
    /// <summary>O19's default: <c>modelHashes.json</c> and <c>securityPosture.txt</c>, any line, take-base.</summary>
    public static IReadOnlyList<GitWriterAutoResolve> SparkGeneratedFiles { get; } =
        [new("**/App_Data/modelHashes.json", AnyLine: true), new("**/App_Data/securityPosture.txt", AnyLine: true)];
}

/// <summary>The body of <c>POST /jobs/prepare</c>. <paramref name="ExpectedQueueSha"/> null means the queue branch must not exist.</summary>
public sealed record GitWriterPrepareRequest(
    string Provider, string RepoId, string CloneUrl, SidecarCredential Credential, string BaseBranch, string? BaseSha,
    string HeadRef, string HeadSha, string QueueBranch, string? ExpectedQueueSha, IReadOnlyList<GitWriterAutoResolve> AutoResolve,
    GitWriterIdentity Author, GitWriterIdentity Committer, string Message);

/// <summary>The body of <c>POST /jobs/publish</c>. <paramref name="Patch"/> is the regenerate artifact's bytes, base64,
/// or null when nothing was regenerated; <paramref name="PushUrl"/> is the fork's clone URL for a fork's pull request (O3).</summary>
public sealed record GitWriterPublishRequest(
    string Provider, string RepoId, string CloneUrl, string? PushUrl, SidecarCredential Credential, string QueueBranch,
    string PreparedSha, string HeadRef, string PrBranch, string ExpectedPrHeadSha, string? Patch, IReadOnlyList<string> RegeneratedPaths,
    GitWriterIdentity Committer);

/// <summary>The body of <c>DELETE /jobs/queue-branch</c>.</summary>
public sealed record GitWriterDeleteQueueBranchRequest(string Provider, string RepoId, string CloneUrl, SidecarCredential Credential, string QueueBranch, string? ExpectedSha);

/// <param name="Paths">Why, for <c>evicted</c>. For <c>prepared</c>: the <c>.github/**</c> paths of the net change.</param>
public sealed record GitWriterPrepareResult(
    string Outcome, string? Reason, string? BaseSha, string? HeadSha, string? PreparedSha,
    IReadOnlyList<string> ResolvedPaths, IReadOnlyList<string> Paths, string? Detail, long DurationMs)
{
    /// <summary>The net change touches <c>.github/**</c> (D12); the saga decides what that stops (§0 D63).</summary>
    public bool TouchesWorkflows { get; init; }

    /// <summary>The base's <c>.github/workflows</c> differs from the pinned head's (§0 D59): a fork's publish needs the
    /// forge's workflow permission.</summary>
    public bool BaseChangedWorkflows { get; init; }
}

public sealed record GitWriterPublishResult(string Outcome, string? Reason, string? FinalSha, IReadOnlyList<string> Paths, string? Detail, long DurationMs);

public sealed record GitWriterDeleteResult(string Outcome, string? Reason, string? Detail, long DurationMs);

/// <summary>The writer's <c>outcome</c> values (CodeCoverage.GitWriter's <c>JobOutcomes</c>).</summary>
public static class GitWriterOutcomes
{
    public const string Prepared = "prepared";
    public const string Evicted = "evicted";
    public const string HeadMismatch = "head-mismatch";
    public const string BaseMoved = "base-moved";
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
    /// <summary><c>prepared</c>/<c>published</c> with the commit an earlier attempt of the same job pushed (§0 D52).</summary>
    public const string AlreadyPushed = "already-pushed";
}
