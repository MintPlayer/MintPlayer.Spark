using CodeCoverage.Entities;
using Microsoft.Extensions.Caching.Memory;
using Raven.Client.Documents.Session;
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

        // 3. Ask GitHub — but only for an owner we already know.
        //
        // This step exists to map a *stale name of a repository we know* onto its current id, and a
        // stale name's owner is, by construction, an owner we knew. Calling GitHub for arbitrary
        // input would be two gifts to an anonymous caller: the badge endpoint is [AllowAnonymous],
        // so probing distinct names would burn the App's GitHub rate limit and degrade the
        // reconciler and the PR bot; and it would turn response time into an existence oracle,
        // because a name we know answers from RavenDB in milliseconds while one we do not costs a
        // round-trip. For a private repository, "we know it" means it exists and the App is
        // installed on it — precisely what the badge endpoint's never-404 rule refuses to reveal.
        //
        // Gating on the account keeps the useful case (the owner is known; only the repository name
        // is stale) and costs an indexed lookup instead of a network call for everything else.
        if (!await IsKnownAccountAsync(owner, cancellationToken))
            return RepositoryResolution.None;

        var gitHubId = await LookupGitHubIdAsync(owner, name, cancellationToken);
        if (gitHubId is null)
            return RepositoryResolution.None;

        var resolved = await session.LoadAsync<Repository>(Repository.DocumentId(gitHubId.Value), cancellationToken);
        return resolved is null
            ? RepositoryResolution.None
            : new RepositoryResolution(resolved, Redirect: true);
    }

    /// <summary>
    /// Whether we already hold an account with this login. Cached alongside the name lookups,
    /// because the miss path is the one a crawler exercises.
    /// </summary>
    private async Task<bool> IsKnownAccountAsync(string owner, CancellationToken cancellationToken)
    {
        var cacheKey = $"known-account/{owner}".ToLowerInvariant();
        if (cache.TryGetValue<bool>(cacheKey, out var known))
            return known;

        known = await session.Query<Account, Indexes.Accounts_Overview>()
            .AnyAsync(a => a.Login == owner, cancellationToken);

        cache.Set(cacheKey, known, LookupCacheDuration);
        return known;
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
