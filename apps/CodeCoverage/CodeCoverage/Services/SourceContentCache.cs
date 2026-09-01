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
/// </summary>
public interface ISourceContentCache
{
    bool TryGet(string key, out string? content);
    void Set(string key, string content, TimeSpan duration);
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

    public bool TryGet(string key, out string? content) => cache.TryGetValue(key, out content);

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
