using System.Collections.Concurrent;
using Microsoft.Extensions.Caching.Memory;

namespace CodeCoverage.Services;

/// <summary>
/// A bounded cache for GitHub file source, separate from the app's shared
/// <see cref="IMemoryCache"/>.
///
/// Source bodies are the one thing cached here that an anonymous caller can
/// grow without limit: <c>/api/browse/…/file</c> fetches a file from GitHub per
/// uncached path, so walking a large repository's tree fills the process with
/// megabytes of source. The shared cache holds short owner lists and cannot.
///
/// It is a separate instance rather than a <c>SizeLimit</c> on the shared one
/// because a size limit is not a quiet ceiling: once set, every <c>Set</c> that
/// omits an explicit <c>Size</c> throws — including calls inside the framework,
/// which we neither own nor can audit. Bounding only what needs bounding keeps
/// the blast radius to this file.
///
/// Why not HybridCache: its in-memory tier is that same shared <see cref="IMemoryCache"/>, so
/// bounding it means the <c>SizeLimit</c> above. Its keys here are commit-addressed and never
/// change, and the app runs as one process, so its distributed tier and tag invalidation buy
/// nothing. The one thing it would add, one fetch per key under concurrency, is
/// <see cref="GetOrFetchAsync"/>.
///
/// Lives with its only consumer (<c>GitHubContentService</c>) rather than in CodeCoverage.Library,
/// which holds entities and contracts only and must not depend on ASP.NET Core (#388).
/// </summary>
public interface ISourceContentCache
{
    bool TryGet(string key, out string? content);
    void Set(string key, string content, TimeSpan duration);

    /// <summary>
    /// The cached content for <paramref name="key"/>, or the result of <paramref name="fetch"/>, run
    /// <b>once</b> however many callers ask for the same uncached key at the same time. A
    /// <c>null</c> result (not found, fetch failed) is returned but not cached.
    /// </summary>
    /// <remarks>
    /// The shared fetch does not run under any one caller's token, so one caller giving up cannot fail
    /// the others; each caller stops waiting on its own <paramref name="cancellationToken"/>.
    /// </remarks>
    Task<string?> GetOrFetchAsync(string key, Func<Task<string?>> fetch, TimeSpan duration, CancellationToken cancellationToken = default);
}

public sealed class SourceContentCache : ISourceContentCache, IDisposable
{
    /// <summary>
    /// Entries are sized in characters, so this is roughly 64M chars — a couple
    /// of hundred megabytes at worst, and thousands of source files. Eviction is
    /// LRU-ish within the limit; a miss costs one GitHub request, never an error.
    /// </summary>
    private const long SizeLimitInCharacters = 64L * 1024 * 1024;

    /// <summary>
    /// One file must not be able to evict everything else. Anything larger is
    /// served but not cached — a generated bundle is exactly the kind of file
    /// that is both huge and rarely read twice.
    /// </summary>
    private const int MaxEntryCharacters = 2 * 1024 * 1024;

    private readonly MemoryCache cache = new(new MemoryCacheOptions
    {
        SizeLimit = SizeLimitInCharacters,
        CompactionPercentage = 0.25,
    });

    /// <summary>The fetches in flight, one per key; removed when they finish, whatever the outcome.</summary>
    private readonly ConcurrentDictionary<string, Lazy<Task<string?>>> inFlight = new(StringComparer.Ordinal);

    public bool TryGet(string key, out string? content) => cache.TryGetValue(key, out content);

    public async Task<string?> GetOrFetchAsync(string key, Func<Task<string?>> fetch, TimeSpan duration, CancellationToken cancellationToken = default)
    {
        if (TryGet(key, out var cached))
            return cached;

        // Lazy, so a GetOrAdd race builds the wrapper twice but starts the fetch once.
        var shared = inFlight.GetOrAdd(key, k => new Lazy<Task<string?>>(() => FetchAndStoreAsync(k, fetch, duration)));
        return await shared.Value.WaitAsync(cancellationToken);
    }

    private async Task<string?> FetchAndStoreAsync(string key, Func<Task<string?>> fetch, TimeSpan duration)
    {
        try
        {
            var content = await fetch();
            if (content is not null)
                Set(key, content, duration);
            return content;
        }
        finally
        {
            inFlight.TryRemove(key, out _);
        }
    }

    public void Set(string key, string content, TimeSpan duration)
    {
        if (content.Length > MaxEntryCharacters)
            return;

        cache.Set(key, content, new MemoryCacheEntryOptions
        {
            Size = content.Length,
            AbsoluteExpirationRelativeToNow = duration,
        });
    }

    // Explicit, so it isn't part of the service's own surface — the DI
    // container still disposes the singleton at shutdown.
    void IDisposable.Dispose() => cache.Dispose();
}
