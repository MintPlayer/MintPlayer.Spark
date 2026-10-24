using System.Text.RegularExpressions;
using CodeCoverage.Entities;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Webhooks.GitHub.Services;

namespace CodeCoverage.Services;

public interface IGitHubContentService
{
    /// <summary>Source of one file at an exact commit, or null when it does not exist (a 404); any other failure throws (P16).</summary>
    Task<string?> GetFileContentAsync(Repository repository, long? installationId, string sha, string path, CancellationToken cancellationToken = default);
}

/// <summary>
/// Fetches file content from GitHub at view time — source is never stored
/// here. Private repos read through the App installation token; public repos
/// fall back to raw.githubusercontent.com. Content at a full SHA is immutable,
/// so cache entries can live long; a read by branch name is never cached.
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
        // Only a full SHA is immutable. Many callers pass a branch name (the default branch's coverage.yml), and a cached
        // branch read served the onboarding's pre-merge coverage.yml to the release run right after the merge: "coverage.yml
        // on master has no versioning section" (MintPlayer.AspNetCore.SpaServices, run 40132022816).
        if (!FullSha().IsMatch(sha))
            return await FetchFileContentAsync(repository, installationId, sha, path);

        // One GitHub fetch per uncached file however many requests ask for it at once; a miss
        // (null) is returned but not cached.
        var cacheKey = $"content/{repository.GitHubId}/{sha}/{path}";
        return await sourceCache.GetOrFetchAsync(cacheKey, () => FetchFileContentAsync(repository, installationId, sha, path), CacheDuration, cancellationToken);
    }

    [GeneratedRegex(@"\A(?:[0-9a-f]{40}|[0-9a-f]{64})\z", RegexOptions.CultureInvariant)]
    private static partial Regex FullSha();

    /// <summary>
    /// Runs once per key for every caller waiting on it, so it does not take any one caller's
    /// cancellation token (see <see cref="ISourceContentCache.GetOrFetchAsync"/>).
    /// </summary>
    private async Task<string?> FetchFileContentAsync(Repository repository, long? installationId, string sha, string path)
    {
        // P16 (dependency-updates PRD): only a 404 is an absence. Every other failure is thrown, so a rate-limited read is
        // retried instead of being read as "no coverage.yml" or "no dependabot file" (an onboarding built on that answer
        // opened a wrong pull request). Nothing failed is cached: the cache stores only a non-null answer.
        if (installationId is not null)
        {
            try
            {
                var client = await installationService.CreateInstallationClientAsync(installationId.Value);
                var files = await client.Repository.Content.GetAllContentsByRef(repository.GitHubId, path, sha);
                return files.FirstOrDefault()?.Content;
            }
            catch (Octokit.NotFoundException)
            {
                // No such file at that ref: an answer, not a failure. The merge queue asks every repository's default branch
                // for coverage.yml on each pull-request event (merge-queue PRD §0 O30, D88), and most repositories have none.
                logger.LogDebug("No {Path} in {Repo} at {Sha}", path, repository.FullName, sha);
                return null;
            }
            catch (Octokit.ApiException ex)
            {
                logger.LogWarning(ex, "Installation content fetch failed for {Repo}@{Sha}:{Path}", repository.FullName, sha, path);
                throw GitHubForgeClient.ForgeFailure(ex, $"{path} of {repository.FullName}@{sha}");
            }
        }

        if (repository.IsPrivate)
            return null;

        // A public repository with no installation: anonymously, from raw.githubusercontent.com.
        var http = httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"https://raw.githubusercontent.com/{repository.FullName}/{sha}/{Uri.EscapeDataString(path).Replace("%2F", "/")}");
        request.Headers.UserAgent.Add(new System.Net.Http.Headers.ProductInfoHeaderValue("Coverage", "1.0"));
        using var response = await http.SendAsync(request);
        if (response.IsSuccessStatusCode)
            return await response.Content.ReadAsStringAsync();
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;
        if (GitHubRateLimits.Classify(response, null, DateTimeOffset.UtcNow, $"{path} of {repository.FullName}@{sha}") is { } limited)
            throw new CodeCoverage.Forge.ForgeRequestException($"GitHub rate limit reached reading {path} of {repository.FullName}.", 429, limited);
        throw new CodeCoverage.Forge.ForgeRequestException(
            $"raw.githubusercontent.com answered {(int)response.StatusCode} for {path} of {repository.FullName}@{sha}.", (int)response.StatusCode);
    }
}
