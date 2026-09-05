using CodeCoverage.Entities;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Webhooks.GitHub.Services;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;

namespace CodeCoverage.Services;

/// <inheritdoc cref="IRepositoryResolver"/>
[Register(typeof(IRepositoryResolver), ServiceLifetime.Scoped)]
public partial class RepositoryResolver : IRepositoryResolver
{
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly IGitHubInstallationService installations;
    [Inject] private readonly IMemoryCache cache;
    [Inject] private readonly ILogger<RepositoryResolver> logger;

    /// <summary>
    /// How long a GitHub name lookup is remembered, hits and misses alike. The badge endpoint is
    /// anonymous and rate-limited, so an unknown name must cost one API call per window rather than
    /// one per request — caching only the hits would leave the miss path, which is the one an
    /// attacker or a crawler exercises, uncached.
    /// </summary>
    private static readonly TimeSpan LookupCacheDuration = TimeSpan.FromMinutes(10);

    public async Task<RepositoryResolution> ResolveAsync(string owner, string name, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(name))
            return RepositoryResolution.None;

        var fullName = $"{owner}/{name}";

        // 1. The live name. Always first, and this is what makes a name takeover safe: once a new
        //    repository occupies an old name, it is found here and the alias below is never
        //    consulted for it.
        var live = await session.Query<Repository, Indexes.Repositories_Overview>()
            .Where(r => r.FullName == fullName)
            .FirstOrDefaultAsync(cancellationToken);
        if (live is not null)
            return new RepositoryResolution(live, Redirect: false);

        // 2. A name we remember this repository leaving behind.
        var aliased = await session.Query<Repository, Indexes.Repositories_Overview>()
            .Where(r => r.PreviousFullNames.Any(previous => previous == fullName))
            .Take(2)
            .ToListAsync(cancellationToken);
        if (aliased.Count == 1)
            return new RepositoryResolution(aliased[0], Redirect: true);
        if (aliased.Count > 1)
        {
            // Two repositories both once answered to this name. Which one the caller meant is not
            // ours to guess, so fall through to GitHub, which knows.
            logger.LogInformation("Ambiguous alias {FullName}: {Count} repositories claim it", fullName, aliased.Count);
        }

        // 3. Ask GitHub, which follows its own rename and transfer redirects and answers with the
        //    numeric id — the one thing about a repository that never changes.
        var gitHubId = await LookupGitHubIdAsync(owner, name, cancellationToken);
        if (gitHubId is null)
            return RepositoryResolution.None;

        var resolved = await session.LoadAsync<Repository>(Repository.DocumentId(gitHubId.Value), cancellationToken);
        return resolved is null
            ? RepositoryResolution.None
            : new RepositoryResolution(resolved, Redirect: true);
    }

    /// <summary>
    /// Asks GitHub what numeric id an <c>owner/name</c> resolves to today, following the redirect
    /// GitHub itself keeps for renamed and transferred repositories. Returns null when the name is
    /// unknown, when the App has no credentials configured, or when GitHub is unreachable — in
    /// every case the caller degrades to "not found" rather than failing.
    /// </summary>
    private async Task<long?> LookupGitHubIdAsync(string owner, string name, CancellationToken cancellationToken)
    {
        var cacheKey = $"repo-id/{owner}/{name}".ToLowerInvariant();
        if (cache.TryGetValue<long?>(cacheKey, out var cached))
            return cached;

        long? gitHubId = null;
        try
        {
            var client = await installations.CreateAppClientAsync();
            var repository = await client.Repository.Get(owner, name);
            gitHubId = repository.Id;
        }
        catch (Octokit.NotFoundException)
        {
            // A name that resolves to nothing. Cached as a miss below, deliberately.
        }
        catch (Exception ex)
        {
            // No private key configured, GitHub down, rate limited. Not knowing is not the same as
            // knowing there is nothing, so this is not cached — the next request may do better.
            logger.LogDebug(ex, "GitHub name lookup for {Owner}/{Name} could not be completed", owner, name);
            return null;
        }

        cache.Set(cacheKey, gitHubId, LookupCacheDuration);
        return gitHubId;
    }
}
