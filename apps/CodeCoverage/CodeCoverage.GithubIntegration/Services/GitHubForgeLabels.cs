using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CodeCoverage.Forge;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Webhooks.GitHub.Services;
using Raven.Client.Documents.Session;
using Account = CodeCoverage.Entities.Account;
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
/// <b>GraphQL for the files</b> (S2): REST pages carry every file's patch, which a size count never reads.
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
    /// <summary>GitHub lists at most 3 000 files of a pull request: 30 pages of 100.</summary>
    private const int MaxFilePages = 30;

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

        var files = new List<ForgePullRequestFile>();
        string? cursor = null;
        for (var page = 0; page < MaxFilePages; page++)
        {
            var body = await connection.Run(FilesRequest(repository, number, cursor), cancellationToken);
            var (nodes, next) = ParseFiles(body, repository, number);
            files.AddRange(nodes);
            if (next is null) return files;
            cursor = next;
        }

        return files;
    }

    /// <summary>One page of <c>pullRequest.files</c>: path and counts only, every value a variable.</summary>
    internal static string FilesRequest(Repository repository, int number, string? cursor)
    {
        const string query = "query($owner: String!, $name: String!, $number: Int!, $after: String) { repository(owner: $owner, name: $name) "
            + "{ pullRequest(number: $number) { files(first: 100, after: $after) { nodes { path additions deletions } pageInfo { hasNextPage endCursor } } } } }";
        var variables = new Dictionary<string, object?>
        {
            ["owner"] = repository.OwnerLogin,
            ["name"] = repository.Name,
            ["number"] = number,
            ["after"] = cursor,
        };
        return JsonSerializer.Serialize(new { query, variables });
    }

    /// <summary>A page's files and the cursor of the next page, null on the last.</summary>
    internal static (List<ForgePullRequestFile> Files, string? Next) ParseFiles(string body, Repository repository, int number)
    {
        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        if (root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0)
        {
            var what = $"listing the files of {repository.FullName}#{number}";
            if (GitHubRateLimits.FromGraphQLErrors(errors.EnumerateArray().Select(e => (Str(e, "type"), Str(e, "message"))), _ => null, DateTimeOffset.UtcNow, what) is { } limited)
                throw new ForgeRequestException($"GitHub rate limit reached {what}.", 429, limited);
            var first = errors[0];
            var message = Str(first, "message") ?? "GraphQL error";
            if (Str(first, "type") is "FORBIDDEN")
                throw new ForgeAccessDeniedException(message, "pull_requests=read");
            throw new ForgeRequestException($"GitHub refused {what}: {message}", null);
        }

        var connection = root.GetProperty("data").GetProperty("repository").GetProperty("pullRequest").GetProperty("files");
        var files = connection.GetProperty("nodes").EnumerateArray()
            .Select(n => new ForgePullRequestFile(Str(n, "path") ?? "", n.GetProperty("additions").GetInt32(), n.GetProperty("deletions").GetInt32()))
            .ToList();
        var pageInfo = connection.GetProperty("pageInfo");
        var next = pageInfo.GetProperty("hasNextPage").GetBoolean() ? Str(pageInfo, "endCursor") : null;
        return (files, next);
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

        public void EnsureSuccess()
        {
            if ((int)Status is >= 200 and < 300) return;
            var message = Json?.RootElement is { ValueKind: JsonValueKind.Object } root ? Str(root, "message") : null;
            if (Status == HttpStatusCode.Forbidden)
                throw new ForgeAccessDeniedException(message ?? $"{method} {path} was forbidden.", Permission);
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
