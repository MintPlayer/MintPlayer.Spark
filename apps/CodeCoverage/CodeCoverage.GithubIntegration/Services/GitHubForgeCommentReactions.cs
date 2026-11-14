using System.Globalization;
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
/// GitHub's <see cref="IForgeCommentReactions"/> (#660 M2), as the App installation of the repository's account. Pull-request
/// conversation comments are issue comments, so <c>issues/comments/{id}/reactions</c> serves <c>/release</c> on an issue and
/// every pull-request command alike (spike S1).
/// </summary>
[Register(typeof(IForgeCommentReactions), ServiceLifetime.Scoped)]
public partial class GitHubForgeCommentReactions : IForgeCommentReactions
{
    [Inject] private readonly IGitHubInstallationService installationService;
    [Inject] private readonly IHttpClientFactory httpClientFactory;
    [Inject] private readonly IAsyncDocumentSession session;

    private static readonly Uri ApiBase = new("https://api.github.com/");
    private const string ApiVersion = "2022-11-28";
    /// <summary>Either one lets the App react (PRD §1a); the App holds both.</summary>
    private const string Permission = "issues=write";

    public EForgeProvider Provider => EForgeProvider.GitHub;

    public async Task<long> AddAsync(Repository repository, long commentId, EForgeReaction reaction, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(commentId);
        // docs: POST /repos/{owner}/{repo}/issues/comments/{comment_id}/reactions → 201 new, 200 the App's existing one (S1).
        var (status, json) = await SendAsync(repository, HttpMethod.Post, ReactionsPath(repository, commentId),
            new Dictionary<string, object?> { ["content"] = Content(reaction) }, cancellationToken);
        using (json)
        {
            EnsureSuccess(status, json, "POST", ReactionsPath(repository, commentId));
            return json?.RootElement is { ValueKind: JsonValueKind.Object } root && root.TryGetProperty("id", out var id) && id.TryGetInt64(out var value)
                ? value
                : throw new ForgeRequestException($"GitHub answered a reaction without an id on comment {commentId} of {repository.FullName}.", (int)status);
        }
    }

    public async Task RemoveAsync(Repository repository, long commentId, long reactionId, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(commentId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(reactionId);
        // docs: DELETE /repos/{owner}/{repo}/issues/comments/{comment_id}/reactions/{reaction_id} → 204; 404 once gone (S1).
        var path = $"{ReactionsPath(repository, commentId)}/{reactionId.ToString(CultureInfo.InvariantCulture)}";
        var (status, json) = await SendAsync(repository, HttpMethod.Delete, path, null, cancellationToken);
        using (json)
        {
            if (status == HttpStatusCode.NotFound) return;
            EnsureSuccess(status, json, "DELETE", path);
        }
    }

    /// <summary>GitHub's reaction <c>content</c> values.</summary>
    internal static string Content(EForgeReaction reaction) => reaction switch
    {
        EForgeReaction.ThumbsUp => "+1",
        EForgeReaction.ThumbsDown => "-1",
        EForgeReaction.Eyes => "eyes",
        EForgeReaction.Rocket => "rocket",
        EForgeReaction.Hooray => "hooray",
        _ => throw new ArgumentOutOfRangeException(nameof(reaction), reaction, null),
    };

    // ── Plumbing ─────────────────────────────────────────────────────────────────────────────────

    private static string ReactionsPath(Repository repository, long commentId)
        => $"repos/{Uri.EscapeDataString(repository.OwnerLogin)}/{Uri.EscapeDataString(repository.Name)}/issues/comments/{commentId.ToString(CultureInfo.InvariantCulture)}/reactions";

    private static void EnsureSuccess(HttpStatusCode status, JsonDocument? json, string method, string path)
    {
        if ((int)status is >= 200 and < 300) return;
        var message = json?.RootElement is { ValueKind: JsonValueKind.Object } root && root.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String
            ? m.GetString()
            : null;
        if (status == HttpStatusCode.Forbidden)
            throw new ForgeAccessDeniedException(message ?? $"{method} {path} was forbidden.", Permission);
        throw new ForgeRequestException($"{method} {path} answered {(int)status}: {message}", (int)status);
    }

    private async Task<(HttpStatusCode Status, JsonDocument? Json)> SendAsync(Repository repository, HttpMethod method, string path, object? body,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(repository);
        var installationId = repository.Account is null
            ? null
            : (await session.LoadAsync<Account>(repository.Account, cancellationToken))?.InstallationId;
        if (installationId is not { } installation)
            throw new ForgeAccessDeniedException($"No GitHub App installation for {repository.FullName}.");
        var token = await installationService.GetInstallationTokenAsync(installation, cancellationToken);
        var http = httpClientFactory.CreateClient(nameof(GitHubForgeCommentReactions));

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

        // A rate limit (403 or 429) is retryable through the message bus, never a refusal.
        var message = json?.RootElement is { ValueKind: JsonValueKind.Object } errorRoot && errorRoot.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String
            ? m.GetString()
            : null;
        if (GitHubRateLimits.Classify(response, message, DateTimeOffset.UtcNow, $"{method} {path}") is { } limited)
        {
            json?.Dispose();
            throw new ForgeRequestException($"GitHub rate limit reached on {method} {path}.", 429, limited);
        }
        return (response.StatusCode, json);
    }
}
