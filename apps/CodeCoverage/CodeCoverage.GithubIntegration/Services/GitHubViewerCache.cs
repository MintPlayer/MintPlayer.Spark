using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Primitives;

namespace CodeCoverage.Services;

/// <summary>
/// One invalidation scope per signed-in user for every per-viewer answer this forge caches beyond the owner set itself: the
/// organization-owner checks (<see cref="GitHubAccessService.ManagesOwnerAsync"/>) and the repository-admin sets
/// (<see cref="GitHubRepositoryAdministration.GetAdministeredRepositoriesAsync"/>), dependency-updates PRD D-O12.
/// </summary>
/// <remarks>
/// <para>
/// Those answers are keyed per (user, org) and per (user, account), so the manual resync cannot remove them by key the way
/// it removes <c>github-owners/{userId}</c>. Each entry instead carries this scope's change token, and
/// <see cref="Invalidate"/> (called from <see cref="GitHubAccessService.InvalidateAsync"/>) cancels it: every entry of
/// that user expires at once, and nobody else's.
/// </para>
/// <para>
/// The scope itself slides an hour, far beyond the five minutes any entry lives, and is never evicted for memory pressure:
/// a scope lost while its entries were alive would leave them out of reach of the next resync.
/// </para>
/// </remarks>
internal static class GitHubViewerCache
{
    private static readonly TimeSpan ScopeSliding = TimeSpan.FromHours(1);

    private static string Key(string userId) => $"github-viewer-scope/{userId}";

    /// <summary>A change token that expires when <paramref name="userId"/>'s cached answers are invalidated.</summary>
    public static IChangeToken Scope(IMemoryCache cache, string userId)
    {
        var source = cache.GetOrCreate(Key(userId), entry =>
        {
            entry.SlidingExpiration = ScopeSliding;
            entry.Priority = CacheItemPriority.NeverRemove;
            return new CancellationTokenSource();
        })!;
        return new CancellationChangeToken(source.Token);
    }

    /// <summary>Caches <paramref name="value"/> for <paramref name="lifetime"/>, in <paramref name="userId"/>'s scope.</summary>
    public static void Set<T>(IMemoryCache cache, string userId, string key, T value, TimeSpan lifetime)
    {
        var options = new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = lifetime };
        options.AddExpirationToken(Scope(cache, userId));
        cache.Set(key, value, options);
    }

    /// <summary>Expires every answer cached in <paramref name="userId"/>'s scope.</summary>
    public static void Invalidate(IMemoryCache cache, string userId)
    {
        if (cache.Get<CancellationTokenSource>(Key(userId)) is not { } source)
            return;
        cache.Remove(Key(userId));
        source.Cancel();
    }
}
