using CodeCoverage.Entities;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Webhooks.GitHub.Services;

namespace CodeCoverage.Services;

public interface IGitHubContentService
{
    /// <summary>Source of one file at an exact commit, or null when unavailable.</summary>
    Task<string?> GetFileContentAsync(Repository repository, long? installationId, string sha, string path, CancellationToken cancellationToken = default);
}

/// <summary>
/// Fetches file content from GitHub at view time — source is never stored
/// here. Private repos read through the App installation token; public repos
/// fall back to raw.githubusercontent.com. Content at a full SHA is immutable,
/// so cache entries can live long.
/// </summary>
[Register(typeof(IGitHubContentService), ServiceLifetime.Scoped)]
public partial class GitHubContentService : IGitHubContentService
{
    [Inject] private readonly IGitHubInstallationService installationService;
    [Inject] private readonly IHttpClientFactory httpClientFactory;
    [Inject] private readonly ISourceContentCache sourceCache;
    [Inject] private readonly ILogger<GitHubContentService> logger;

    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(30);

    public async Task<string?> GetFileContentAsync(Repository repository, long? installationId, string sha, string path, CancellationToken cancellationToken = default)
    {
        // One GitHub fetch per uncached file however many requests ask for it at once; a miss
        // (null) is returned but not cached.
        var cacheKey = $"content/{repository.GitHubId}/{sha}/{path}";
        return await sourceCache.GetOrFetchAsync(cacheKey, () => FetchFileContentAsync(repository, installationId, sha, path), CacheDuration, cancellationToken);
    }

    /// <summary>
    /// Runs once per key for every caller waiting on it, so it does not take any one caller's
    /// cancellation token (see <see cref="ISourceContentCache.GetOrFetchAsync"/>).
    /// </summary>
    private async Task<string?> FetchFileContentAsync(Repository repository, long? installationId, string sha, string path)
    {
        string? content = null;

        if (installationId is not null)
        {
            try
            {
                var client = await installationService.CreateInstallationClientAsync(installationId.Value);
                var files = await client.Repository.Content.GetAllContentsByRef(repository.GitHubId, path, sha);
                content = files.FirstOrDefault()?.Content;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Installation content fetch failed for {Repo}@{Sha}:{Path}", repository.FullName, sha, path);
            }
        }

        if (content is null && !repository.IsPrivate)
        {
            try
            {
                var http = httpClientFactory.CreateClient();
                using var request = new HttpRequestMessage(HttpMethod.Get,
                    $"https://raw.githubusercontent.com/{repository.FullName}/{sha}/{Uri.EscapeDataString(path).Replace("%2F", "/")}");
                request.Headers.UserAgent.Add(new System.Net.Http.Headers.ProductInfoHeaderValue("Coverage", "1.0"));
                var response = await http.SendAsync(request);
                if (response.IsSuccessStatusCode)
                    content = await response.Content.ReadAsStringAsync();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Raw content fetch failed for {Repo}@{Sha}:{Path}", repository.FullName, sha, path);
            }
        }

        return content;
    }
}
