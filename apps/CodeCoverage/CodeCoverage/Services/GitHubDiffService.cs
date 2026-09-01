using System.Text.Json;
using CodeCoverage.Entities;
using Microsoft.Extensions.Caching.Memory;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Webhooks.GitHub.Services;

namespace CodeCoverage.Services;

/// <summary>
/// Compares through the App installation token where one exists; public repos
/// fall back to unauthenticated REST (60 requests/hour/IP — the cache below is
/// what makes that budget survivable under status polling). A comparison is
/// cached briefly rather than permanently because <paramref name="baseRef"/>
/// may be a branch name whose tip moves.
/// </summary>
[Register(typeof(IGitHubDiffService), ServiceLifetime.Scoped)]
public partial class GitHubDiffService : IGitHubDiffService
{
    [Inject] private readonly IGitHubInstallationService installationService;
    [Inject] private readonly IHttpClientFactory httpClientFactory;
    [Inject] private readonly ILogger<GitHubDiffService> logger;

    /// <summary>GitHub returns at most this many files per comparison.</summary>
    private const int GitHubFileCap = 300;

    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);

    // Static because the service is scoped and the data is process-wide; sized
    // in entries — comparisons are small (line numbers, not patches).
    private static readonly MemoryCache Cache = new(new MemoryCacheOptions { SizeLimit = 256 });

    public async Task<CommitComparison?> CompareAsync(Repository repository, long? installationId, string baseRef, string headSha, CancellationToken cancellationToken = default)
    {
        var cacheKey = $"{repository.GitHubId}/{baseRef}...{headSha}";
        if (Cache.TryGetValue(cacheKey, out CommitComparison? cached))
            return cached;

        CommitComparison? comparison = null;

        if (installationId is not null)
        {
            try
            {
                var client = await installationService.CreateInstallationClientAsync(installationId.Value);
                var result = await client.Repository.Commit.Compare(repository.OwnerLogin, repository.Name, baseRef, headSha);
                comparison = new CommitComparison(
                    result.MergeBaseCommit?.Sha,
                    [.. result.Files.Select(f => new DiffFile(f.Filename, f.Status, f.PreviousFileName, AddedLines(f.Patch)))],
                    result.Files.Count >= GitHubFileCap);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Installation compare failed for {Repo} {Base}...{Head}", repository.FullName, baseRef, headSha);
            }
        }

        if (comparison is null && !repository.IsPrivate)
            comparison = await CompareAnonymouslyAsync(repository, baseRef, headSha, cancellationToken);

        if (comparison is not null)
            Cache.Set(cacheKey, comparison, new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = CacheDuration });

        return comparison;
    }

    private async Task<CommitComparison?> CompareAnonymouslyAsync(Repository repository, string baseRef, string headSha, CancellationToken cancellationToken)
    {
        try
        {
            var http = httpClientFactory.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"https://api.github.com/repos/{repository.FullName}/compare/{Uri.EscapeDataString(baseRef)}...{Uri.EscapeDataString(headSha)}");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            request.Headers.UserAgent.Add(new System.Net.Http.Headers.ProductInfoHeaderValue("Coverage", "1.0"));

            var response = await http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Anonymous compare for {Repo} {Base}...{Head} returned {Status}", repository.FullName, baseRef, headSha, (int)response.StatusCode);
                return null;
            }

            using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
            var root = json.RootElement;

            var mergeBase = root.TryGetProperty("merge_base_commit", out var mb) && mb.TryGetProperty("sha", out var mbSha)
                ? mbSha.GetString()
                : null;

            var files = new List<DiffFile>();
            if (root.TryGetProperty("files", out var filesElement))
            {
                foreach (var file in filesElement.EnumerateArray())
                {
                    files.Add(new DiffFile(
                        file.GetProperty("filename").GetString() ?? "",
                        file.GetProperty("status").GetString() ?? "modified",
                        file.TryGetProperty("previous_filename", out var prev) ? prev.GetString() : null,
                        AddedLines(file.TryGetProperty("patch", out var patch) ? patch.GetString() : null)));
                }
            }

            return new CommitComparison(mergeBase, files, files.Count >= GitHubFileCap);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Anonymous compare failed for {Repo} {Base}...{Head}", repository.FullName, baseRef, headSha);
            return null;
        }
    }

    /// <summary>
    /// New-file line numbers of the '+' lines in a unified-diff patch. Each
    /// hunk header <c>@@ -a,b +c,d @@</c> restarts the counter at c; context
    /// lines advance it, deletions don't. Additions-only is deliberate — a
    /// modified line is an addition in new-file space, and only lines that
    /// exist at head can be covered at head (Codecov's model).
    /// </summary>
    public static int[] AddedLines(string? patch)
    {
        if (string.IsNullOrEmpty(patch))
            return [];

        var added = new List<int>();
        var newLine = 0;
        foreach (var line in patch.Split('\n'))
        {
            if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                // "@@ -a,b +c,d @@ …" — take c.
                var plus = line.IndexOf('+');
                if (plus < 0) continue;
                var end = plus + 1;
                while (end < line.Length && char.IsAsciiDigit(line[end])) end++;
                if (end > plus + 1 && int.TryParse(line.AsSpan(plus + 1, end - plus - 1), out var start))
                    newLine = start;
            }
            else if (line.StartsWith("+++", StringComparison.Ordinal) || line.StartsWith("---", StringComparison.Ordinal))
            {
                // File headers — GitHub's compare `patch` omits them, but a
                // full unified diff has them and they are not content lines.
            }
            else if (line.StartsWith('+'))
            {
                added.Add(newLine);
                newLine++;
            }
            else if (!line.StartsWith('-') && !line.StartsWith('\\'))
            {
                // Context line ("\ No newline at end of file" advances nothing).
                newLine++;
            }
        }
        return [.. added];
    }
}
