using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeCoverage.Forge;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Webhooks.GitHub.Services;
using Octokit.GraphQL;
using Raven.Client.Documents.Session;
using static Octokit.GraphQL.Variable;
using Account = CodeCoverage.Entities.Account;
using GraphQLQuery = Octokit.GraphQL.Query;
using IGraphQLConnection = Octokit.GraphQL.IConnection;
using Repository = CodeCoverage.Entities.Repository;

namespace CodeCoverage.Services;

/// <summary>
/// GitHub's <see cref="IForgeLabels"/> (docs/coverage_pr_size_labels_PRD.md M5), as the App installation of the repository's
/// account.
/// </summary>
/// <remarks>
/// <para>
/// <b>REST over raw JSON for the labels.</b> Octokit 14's <c>Label</c> has no <c>archived_at</c> (S1), and the reconciler
/// must leave an archived label alone, so the definitions are read as JSON. The writes go the same way, so a 404 and
/// a 422 <c>already_exists</c> come back as outcomes rather than exceptions.
/// </para>
/// <para>
/// <b>GraphQL for the files</b> (S2): REST pages carry every file's patch, which a size count never reads. A typed
/// Octokit.GraphQL document, read to its end with <c>AllPages</c> (#714, 2026-11-27).
/// </para>
/// </remarks>
[Register(typeof(IForgeLabels), ServiceLifetime.Scoped)]
public partial class GitHubForgeLabels : IForgeLabels
{
    [Inject] private readonly IGitHubInstallationService installationService;
    [Inject] private readonly IHttpClientFactory httpClientFactory;
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly ILogger<GitHubForgeLabels> logger;

    private static readonly Uri ApiBase = new("https://api.github.com/");
    private const string ApiVersion = "2022-11-28";
    private const string Permission = "issues=write or pull_requests=write";
    private const int PageSize = 100;
    /// <summary>A repository with more labels than this is not one the reconciler can reason about anyway.</summary>
    private const int MaxLabelPages = 20;

    public EForgeProvider Provider => EForgeProvider.GitHub;

    public async Task<IReadOnlyList<ForgeLabel>> ListRepositoryLabelsAsync(Repository repository, CancellationToken cancellationToken = default)
    {
        var labels = new List<ForgeLabel>();
        for (var page = 1; page <= MaxLabelPages; page++)
        {
            using var reply = await SendAsync(repository, HttpMethod.Get, $"{RepoPath(repository)}/labels?per_page={PageSize}&page={page}", null, cancellationToken);
            reply.EnsureSuccess();
            var items = reply.Json!.RootElement.EnumerateArray().ToList();
            labels.AddRange(items.Select(ReadLabel));
            if (items.Count < PageSize) return labels;
        }

        logger.LogWarning("{FullName} has more than {Max} labels; the rest are ignored.", repository.FullName, MaxLabelPages * PageSize);
        return labels;
    }

    public async Task<ForgeLabelWrite> CreateLabelAsync(Repository repository, ForgeLabelDefinition label, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(label);
        using var reply = await SendAsync(repository, HttpMethod.Post, $"{RepoPath(repository)}/labels",
            new { name = label.Name, color = label.Color, description = label.Description }, cancellationToken);
        if (reply.Status == HttpStatusCode.UnprocessableEntity && reply.HasErrorCode("already_exists"))
            return ForgeLabelWrite.Conflict;
        reply.EnsureSuccess();
        return ForgeLabelWrite.Done;
    }

    public async Task<ForgeLabelWrite> UpdateLabelAsync(Repository repository, string currentName, ForgeLabelDefinition label, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(currentName);
        ArgumentNullException.ThrowIfNull(label);
        using var reply = await SendAsync(repository, HttpMethod.Patch, $"{RepoPath(repository)}/labels/{Uri.EscapeDataString(currentName)}",
            new { new_name = label.Name, color = label.Color, description = label.Description }, cancellationToken);
        if (reply.Status == HttpStatusCode.NotFound)
            return ForgeLabelWrite.NotFound;
        if (reply.Status == HttpStatusCode.UnprocessableEntity && reply.HasErrorCode("already_exists"))
            return ForgeLabelWrite.Conflict;
        reply.EnsureSuccess();
        return ForgeLabelWrite.Done;
    }

    public async Task<ForgeLabelWrite> DeleteLabelAsync(Repository repository, string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        using var reply = await SendAsync(repository, HttpMethod.Delete, $"{RepoPath(repository)}/labels/{Uri.EscapeDataString(name)}", null, cancellationToken);
        if (reply.Status == HttpStatusCode.NotFound)
            return ForgeLabelWrite.NotFound;
        reply.EnsureSuccess();
        return ForgeLabelWrite.Done;
    }

    public async Task<IReadOnlyList<string>> ListPullRequestLabelsAsync(Repository repository, int number, CancellationToken cancellationToken = default)
    {
        // A pull request carries at most 100 labels (GitHub's limit per issue), so one page is all of them.
        using var reply = await SendAsync(repository, HttpMethod.Get, $"{RepoPath(repository)}/issues/{number}/labels?per_page={PageSize}", null, cancellationToken);
        reply.EnsureSuccess();
        return reply.Json!.RootElement.EnumerateArray().Select(l => Str(l, "name")).OfType<string>().ToList();
    }

    public async Task AddLabelAsync(Repository repository, int number, string label, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(label);
        using var reply = await SendAsync(repository, HttpMethod.Post, $"{RepoPath(repository)}/issues/{number}/labels", new { labels = new[] { label } }, cancellationToken);
        reply.EnsureSuccess();
    }

    public async Task<bool> RemoveLabelAsync(Repository repository, int number, string label, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(label);
        using var reply = await SendAsync(repository, HttpMethod.Delete, $"{RepoPath(repository)}/issues/{number}/labels/{Uri.EscapeDataString(label)}", null, cancellationToken);
        // "Label does not exist" on the pull request: the state we wanted.
        if (reply.Status == HttpStatusCode.NotFound)
            return false;
        reply.EnsureSuccess();
        return true;
    }

    public async Task<IReadOnlyList<ForgePullRequestFile>> ListPullRequestFilesAsync(Repository repository, int number, CancellationToken cancellationToken = default)
    {
        var installationId = await ResolveInstallationAsync(repository, cancellationToken);
        var connection = await installationService.CreateGraphQLConnectionAsync(installationId, EClientType.Installation);
        var variables = new Dictionary<string, object>
        {
            ["owner"] = repository.OwnerLogin,
            ["name"] = repository.Name,
            ["number"] = number,
        };

        var files = await new ErrorReadingConnection(connection, $"listing the files of {repository.FullName}#{number}")
            .Run(FilesQuery, variables, cancellationToken);
        return [.. files ?? []];
    }

    public async Task<long> PublishSizeCheckAsync(Repository repository, string sha, ForgeSizeCheck check, long? existingId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(sha);
        ArgumentNullException.ThrowIfNull(check);

        var id = existingId ?? await FindSizeCheckAsync(repository, sha, cancellationToken);
        var body = SizeCheckBody(sha, check);

        using var reply = id is { } existing
            ? await SendAsync(repository, HttpMethod.Patch, $"{RepoPath(repository)}/check-runs/{existing}", body, cancellationToken)
            : await SendAsync(repository, HttpMethod.Post, $"{RepoPath(repository)}/check-runs", body, cancellationToken);
        reply.EnsureSuccess("checks=write");
        return reply.Json!.RootElement.GetProperty("id").GetInt64();
    }

    /// <summary>
    /// The check run's body. GitHub answers 422 ("For 'properties/text', nil is not a string") to an explicit
    /// <c>"text": null</c>, so a check without a text leaves the property out.
    /// </summary>
    internal static JsonObject SizeCheckBody(string sha, ForgeSizeCheck check)
    {
        var output = new JsonObject { ["title"] = check.Title, ["summary"] = check.Summary };
        if (check.Text is { } text) output["text"] = text;

        var body = new JsonObject
        {
            ["name"] = IForgeLabels.SizeCheckName,
            ["head_sha"] = sha,
            ["status"] = check.Completed ? "completed" : "in_progress",
        };
        if (check.Completed) body["conclusion"] = "neutral";
        body["output"] = output;
        return body;
    }

    /// <summary>The newest "PR size" check on the commit, when there is one.</summary>
    private async Task<long?> FindSizeCheckAsync(Repository repository, string sha, CancellationToken cancellationToken)
    {
        var path = $"{RepoPath(repository)}/commits/{Uri.EscapeDataString(sha)}/check-runs?check_name={Uri.EscapeDataString(IForgeLabels.SizeCheckName)}&filter=latest&per_page=1";
        using var reply = await SendAsync(repository, HttpMethod.Get, path, null, cancellationToken);
        reply.EnsureSuccess("checks=read");
        return ReadCheckRunId(reply.Json!.RootElement);
    }

    internal static long? ReadCheckRunId(JsonElement root)
        => root.TryGetProperty("check_runs", out var runs) && runs.ValueKind == JsonValueKind.Array && runs.GetArrayLength() > 0
            ? runs[0].GetProperty("id").GetInt64()
            : null;

    /// <summary>
    /// Every file of one pull request, path and counts only: Octokit.GraphQL sends the first page and fetches the rest by
    /// cursor (<c>AllPages</c>). GitHub lists at most 3 000 files of a pull request. Every value is a variable.
    /// </summary>
    private static readonly ICompiledQuery<IEnumerable<ForgePullRequestFile>> FilesQuery = new GraphQLQuery()
        .Repository(owner: Var("owner"), name: Var("name"))
        .PullRequest(Var("number"))
        .Files()
        .AllPages()
        .Select(file => new ForgePullRequestFile(file.Path, file.Additions, file.Deletions))
        .Compile();

    /// <summary>
    /// Passes every page through, after reading GitHub's GraphQL <c>errors</c> itself: Octokit.GraphQL's deserializer throws
    /// them as a bare message, losing the <c>type</c> that tells a refusal from a rate limit (measured 2026-11-27, #714).
    /// </summary>
    private sealed class ErrorReadingConnection(IGraphQLConnection inner, string what) : IGraphQLConnection
    {
        public Uri Uri => inner.Uri;

        public async Task<string> Run(string query, CancellationToken cancellationToken = default)
        {
            var body = await inner.Run(query, cancellationToken);
            ThrowOnErrors(body, what);
            return body;
        }
    }

    /// <summary>
    /// A rate limit is a 429 <see cref="ForgeRequestException"/>, a <c>FORBIDDEN</c> names the permission, any other error
    /// is a <see cref="ForgeRequestException"/> with GitHub's message.
    /// </summary>
    internal static void ThrowOnErrors(string body, string what)
    {
        using var json = JsonDocument.Parse(body);
        if (!json.RootElement.TryGetProperty("errors", out var errors) || errors.ValueKind != JsonValueKind.Array || errors.GetArrayLength() == 0)
            return;

        if (GitHubRateLimits.FromGraphQLErrors(errors.EnumerateArray().Select(e => (Str(e, "type"), Str(e, "message"))), _ => null, DateTimeOffset.UtcNow, what) is { } limited)
            throw new ForgeRequestException($"GitHub rate limit reached {what}.", 429, limited);
        var first = errors[0];
        var message = Str(first, "message") ?? "GraphQL error";
        if (Str(first, "type") is "FORBIDDEN")
            throw new ForgeAccessDeniedException(message, "pull_requests=read");
        throw new ForgeRequestException($"GitHub refused {what}: {message}", null);
    }

    internal static ForgeLabel ReadLabel(JsonElement label)
        => new(
            Str(label, "name") ?? "",
            (Str(label, "color") ?? "").ToLowerInvariant(),
            Str(label, "description"),
            label.TryGetProperty("archived_at", out var archived) && archived.ValueKind == JsonValueKind.String);

    // ── Plumbing ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>One response: its status and its JSON body when it had one.</summary>
    private sealed class Reply(HttpStatusCode status, JsonDocument? json, string method, string path) : IDisposable
    {
        public HttpStatusCode Status { get; } = status;
        public JsonDocument? Json { get; } = json;

        /// <summary>A 422's <c>errors[].code</c> holds <paramref name="code"/>.</summary>
        public bool HasErrorCode(string code)
            => Json?.RootElement is { ValueKind: JsonValueKind.Object } root
               && root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array
               && errors.EnumerateArray().Any(e => e.ValueKind == JsonValueKind.Object && Str(e, "code") == code);

        public void EnsureSuccess(string permission = Permission)
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
        var installationId = await ResolveInstallationAsync(repository, cancellationToken);
        var token = await installationService.GetInstallationTokenAsync(installationId, cancellationToken);
        var http = httpClientFactory.CreateClient(nameof(GitHubForgeLabels));

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
            catch (JsonException) { /* not JSON: an HTML error page from a proxy; the status decides */ }
        }

        // A rate limit (primary or secondary: 403 or 429) is retryable through the message bus, never a refusal.
        var message = json?.RootElement is { ValueKind: JsonValueKind.Object } errorRoot ? Str(errorRoot, "message") : null;
        if (GitHubRateLimits.Classify(response, message, DateTimeOffset.UtcNow, $"{method} {path}") is { } limited)
        {
            json?.Dispose();
            throw new ForgeRequestException($"GitHub rate limit reached on {method} {path}.", 429, limited);
        }

        return new Reply(response.StatusCode, json, method.Method, path);
    }

    private async Task<long> ResolveInstallationAsync(Repository repository, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(repository);
        var installationId = repository.Account is null
            ? null
            : (await session.LoadAsync<Account>(repository.Account, cancellationToken))?.InstallationId;
        return installationId ?? throw new ForgeAccessDeniedException($"No GitHub App installation for {repository.FullName}.");
    }

    /// <summary><c>repos/{owner}/{name}</c>, from the stored names, escaped.</summary>
    private static string RepoPath(Repository repository)
        => $"repos/{Uri.EscapeDataString(repository.OwnerLogin)}/{Uri.EscapeDataString(repository.Name)}";

    private static string? Str(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
