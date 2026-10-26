using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeCoverage.Forge;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Webhooks.GitHub.Services;
using Raven.Client.Documents.Session;
using Account = CodeCoverage.Entities.Account;
using Repository = CodeCoverage.Entities.Repository;

namespace CodeCoverage.Services;

/// <summary>
/// GitHub's <see cref="IForgeVersioning"/> (docs/coverage_package_versioning_PRD.md D16), as the App installation of the
/// repository's account. REST over raw JSON, like <see cref="GitHubForgeLabels"/>, so a 404 or a 422 comes back as an
/// outcome rather than an exception.
/// </summary>
[Register(typeof(IForgeVersioning), ServiceLifetime.Scoped)]
public partial class GitHubForgeVersioning : IForgeVersioning
{
    [Inject] private readonly IGitHubInstallationService installationService;
    [Inject] private readonly IHttpClientFactory httpClientFactory;
    [Inject] private readonly IAsyncDocumentSession session;

    private static readonly Uri ApiBase = new("https://api.github.com/");
    private const string ApiVersion = "2022-11-28";

    public EForgeProvider Provider => EForgeProvider.GitHub;

    public async Task<ForgePullRequestText?> GetPullRequestTextAsync(Repository repository, int number, CancellationToken cancellationToken = default)
    {
        using var reply = await SendAsync(repository, HttpMethod.Get, $"{RepoPath(repository)}/pulls/{number}", null, cancellationToken);
        if (reply.Status == HttpStatusCode.NotFound) return null;
        reply.EnsureSuccess("pull_requests=read");
        return ReadPullRequest(reply.Json!.RootElement);
    }

    internal static ForgePullRequestText ReadPullRequest(JsonElement pull)
    {
        var user = pull.TryGetProperty("user", out var u) && u.ValueKind == JsonValueKind.Object ? u : default;
        var login = user.ValueKind == JsonValueKind.Object ? Str(user, "login") : null;
        var labels = pull.TryGetProperty("labels", out var l) && l.ValueKind == JsonValueKind.Array
            ? l.EnumerateArray().Select(x => Str(x, "name")).OfType<string>().ToList()
            : [];
        return new ForgePullRequestText(
            Number: pull.GetProperty("number").GetInt32(),
            Title: Str(pull, "title") ?? "",
            Body: Str(pull, "body"),
            HeadRef: Str(pull.GetProperty("head"), "ref") ?? "",
            HeadSha: Str(pull.GetProperty("head"), "sha") ?? "",
            BaseRef: Str(pull.GetProperty("base"), "ref") ?? "",
            IsOpen: Str(pull, "state") == "open",
            Merged: pull.TryGetProperty("merged", out var merged) && merged.ValueKind == JsonValueKind.True,
            AuthorIsBot: (user.ValueKind == JsonValueKind.Object && Str(user, "type") == "Bot") || login?.EndsWith("[bot]", StringComparison.Ordinal) == true,
            MergeCommitSha: Str(pull, "merge_commit_sha") is { } sha && merged.ValueKind == JsonValueKind.True ? sha : null,
            Labels: labels);
    }

    public async Task<ForgeIssue?> GetIssueAsync(Repository repository, int number, CancellationToken cancellationToken = default)
    {
        using var reply = await SendAsync(repository, HttpMethod.Get, $"{RepoPath(repository)}/issues/{number}", null, cancellationToken);
        // 410: the issue was deleted; 301: transferred to another repository, which is not "this repository" any more.
        if (reply.Status is HttpStatusCode.NotFound or HttpStatusCode.Gone or HttpStatusCode.MovedPermanently) return null;
        reply.EnsureSuccess("issues=read");
        var issue = reply.Json!.RootElement;
        if (issue.TryGetProperty("pull_request", out _)) return null;
        var labels = issue.TryGetProperty("labels", out var l) && l.ValueKind == JsonValueKind.Array
            ? l.EnumerateArray().Select(x => Str(x, "name")).OfType<string>().ToList()
            : [];
        return new ForgeIssue(number, labels);
    }

    public async Task UpdatePullRequestTitleAsync(Repository repository, int number, string title, CancellationToken cancellationToken = default)
    {
        using var reply = await SendAsync(repository, HttpMethod.Patch, $"{RepoPath(repository)}/pulls/{number}", new JsonObject { ["title"] = title }, cancellationToken);
        reply.EnsureSuccess("pull_requests=write");
    }

    public async Task<long> PublishCheckAsync(Repository repository, string sha, ForgeVersioningCheck check, long? existingId, CancellationToken cancellationToken = default)
    {
        var id = existingId;
        if (id is null)
        {
            var find = $"{RepoPath(repository)}/commits/{Uri.EscapeDataString(sha)}/check-runs?check_name={Uri.EscapeDataString(check.Name)}&filter=latest&per_page=1";
            using var found = await SendAsync(repository, HttpMethod.Get, find, null, cancellationToken);
            found.EnsureSuccess("checks=read");
            id = GitHubForgeLabels.ReadCheckRunId(found.Json!.RootElement);
        }

        var body = CheckBody(sha, check);
        using var reply = id is { } existing
            ? await SendAsync(repository, HttpMethod.Patch, $"{RepoPath(repository)}/check-runs/{existing}", body, cancellationToken)
            : await SendAsync(repository, HttpMethod.Post, $"{RepoPath(repository)}/check-runs", body, cancellationToken);
        reply.EnsureSuccess("checks=write");
        return reply.Json!.RootElement.GetProperty("id").GetInt64();
    }

    /// <summary>The check run's body; no <c>text</c> property when there is none (GitHub 422s an explicit null).</summary>
    internal static JsonObject CheckBody(string sha, ForgeVersioningCheck check)
    {
        var output = new JsonObject { ["title"] = check.Title, ["summary"] = check.Summary };
        if (check.Text is { } text) output["text"] = text;
        var body = new JsonObject
        {
            ["name"] = check.Name,
            ["head_sha"] = sha,
            ["status"] = check.Completed ? "completed" : "in_progress",
        };
        if (check.Completed) body["conclusion"] = check.Conclusion;
        body["output"] = output;
        return body;
    }

    public async Task<ForgeWriteOutcome> CreateReleaseAsync(Repository repository, ForgeRelease release, CancellationToken cancellationToken = default)
    {
        var body = new JsonObject
        {
            ["tag_name"] = release.TagName,
            ["name"] = release.Name,
            ["body"] = release.Body,
            ["prerelease"] = release.Prerelease,
            // Never "latest" by GitHub's own guess: with one Release per package the newest is not the repository's latest.
            ["make_latest"] = "false",
        };
        using var reply = await SendAsync(repository, HttpMethod.Post, $"{RepoPath(repository)}/releases", body, cancellationToken);
        if (reply.Status == HttpStatusCode.UnprocessableEntity && reply.HasErrorCode("already_exists")) return ForgeWriteOutcome.Exists;
        reply.EnsureSuccess("contents=write");
        return ForgeWriteOutcome.Created;
    }

    public async Task<ForgeWriteOutcome> CreateBranchAsync(Repository repository, string branch, string sha, CancellationToken cancellationToken = default)
    {
        var body = new JsonObject { ["ref"] = $"refs/heads/{branch}", ["sha"] = sha };
        using var reply = await SendAsync(repository, HttpMethod.Post, $"{RepoPath(repository)}/git/refs", body, cancellationToken);
        // "Reference already exists" is a plain 422 without an error code.
        if (reply.Status == HttpStatusCode.UnprocessableEntity
            && reply.Json?.RootElement is { ValueKind: JsonValueKind.Object } root
            && Str(root, "message")?.Contains("already exists", StringComparison.OrdinalIgnoreCase) == true)
            return ForgeWriteOutcome.Exists;
        reply.EnsureSuccess("contents=write");
        return ForgeWriteOutcome.Created;
    }

    public async Task<int> OpenPullRequestAsync(Repository repository, string head, string @base, string title, string body, CancellationToken cancellationToken = default)
    {
        var request = new JsonObject { ["head"] = head, ["base"] = @base, ["title"] = title, ["body"] = body };
        using var reply = await SendAsync(repository, HttpMethod.Post, $"{RepoPath(repository)}/pulls", request, cancellationToken);
        if (reply.Status == HttpStatusCode.UnprocessableEntity)
        {
            // An open pull request with this head and base: a redelivery. Find it rather than failing.
            var path = $"{RepoPath(repository)}/pulls?state=open&head={Uri.EscapeDataString($"{repository.OwnerLogin}:{head}")}&base={Uri.EscapeDataString(@base)}&per_page=1";
            using var existing = await SendAsync(repository, HttpMethod.Get, path, null, cancellationToken);
            existing.EnsureSuccess("pull_requests=read");
            if (existing.Json!.RootElement is { ValueKind: JsonValueKind.Array } list && list.GetArrayLength() > 0)
                return list[0].GetProperty("number").GetInt32();
        }
        reply.EnsureSuccess("pull_requests=write");
        return reply.Json!.RootElement.GetProperty("number").GetInt32();
    }

    public async Task<IReadOnlyList<int>> MergedPullRequestsAsync(Repository repository, string sha, string baseBranch, CancellationToken cancellationToken = default)
    {
        using var reply = await SendAsync(repository, HttpMethod.Get, $"{RepoPath(repository)}/commits/{Uri.EscapeDataString(sha)}/pulls?per_page=10", null, cancellationToken);
        if (reply.Status is HttpStatusCode.NotFound or HttpStatusCode.UnprocessableEntity) return [];
        reply.EnsureSuccess("pull_requests=read");
        if (reply.Json!.RootElement is not { ValueKind: JsonValueKind.Array } list) return [];
        return [.. list.EnumerateArray()
            .Where(p => p.TryGetProperty("merged_at", out var merged) && merged.ValueKind == JsonValueKind.String
                && p.TryGetProperty("base", out var @base) && Str(@base, "ref") == baseBranch)
            .Select(p => p.GetProperty("number").GetInt32())];
    }

    public async Task CommentAsync(Repository repository, int number, string body, CancellationToken cancellationToken = default)
    {
        using var reply = await SendAsync(repository, HttpMethod.Post, $"{RepoPath(repository)}/issues/{number}/comments", new JsonObject { ["body"] = body }, cancellationToken);
        reply.EnsureSuccess("issues=write or pull_requests=write");
    }

    public async Task DispatchWorkflowAsync(Repository repository, string workflowFile, string branch, CancellationToken cancellationToken = default)
    {
        var path = $"{RepoPath(repository)}/actions/workflows/{Uri.EscapeDataString(workflowFile)}/dispatches";
        using var reply = await SendAsync(repository, HttpMethod.Post, path, new JsonObject { ["ref"] = branch }, cancellationToken);
        reply.EnsureSuccess("actions=write");
    }

    // ── Plumbing, as GitHubForgeLabels' ──────────────────────────────────────────────────────────

    private sealed class Reply(HttpStatusCode status, JsonDocument? json, string method, string path) : IDisposable
    {
        public HttpStatusCode Status { get; } = status;
        public JsonDocument? Json { get; } = json;

        public bool HasErrorCode(string code)
            => Json?.RootElement is { ValueKind: JsonValueKind.Object } root
               && root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array
               && errors.EnumerateArray().Any(e => e.ValueKind == JsonValueKind.Object && Str(e, "code") == code);

        public void EnsureSuccess(string permission)
        {
            if ((int)Status is >= 200 and < 300) return;
            var message = Json?.RootElement is { ValueKind: JsonValueKind.Object } root ? Str(root, "message") : null;
            if (Status == HttpStatusCode.Forbidden)
                throw new ForgeAccessDeniedException(message ?? $"{method} {path} was forbidden.", permission);
            throw new ForgeRequestException($"{method} {path} answered {(int)Status}: {message}", (int)Status);
        }

        public void Dispose() => Json?.Dispose();
    }

    private async Task<Reply> SendAsync(Repository repository, HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(repository);
        var installationId = (repository.Account is null ? null : (await session.LoadAsync<Account>(repository.Account, cancellationToken))?.InstallationId)
            ?? throw new ForgeAccessDeniedException($"No GitHub App installation for {repository.FullName}.");
        var token = await installationService.GetInstallationTokenAsync(installationId, cancellationToken);
        var http = httpClientFactory.CreateClient(nameof(GitHubForgeVersioning));

        using var request = new HttpRequestMessage(method, new Uri(ApiBase, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", ApiVersion);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("CodeCoverage", "1.0"));
        if (body is not null)
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        using var response = await http.SendAsync(request, cancellationToken);
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        JsonDocument? json = null;
        if (bytes.Length > 0)
        {
            try { json = JsonDocument.Parse(bytes); }
            catch (JsonException) { /* an HTML error page from a proxy; the status decides */ }
        }

        var message = json?.RootElement is { ValueKind: JsonValueKind.Object } errorRoot ? Str(errorRoot, "message") : null;
        if (GitHubRateLimits.Classify(response, message, DateTimeOffset.UtcNow, $"{method} {path}") is { } limited)
        {
            json?.Dispose();
            throw new ForgeRequestException($"GitHub rate limit reached on {method} {path}.", 429, limited);
        }
        return new Reply(response.StatusCode, json, method.Method, path);
    }

    private static string RepoPath(Repository repository)
        => $"repos/{Uri.EscapeDataString(repository.OwnerLogin)}/{Uri.EscapeDataString(repository.Name)}";

    private static string? Str(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
