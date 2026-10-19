using Raven.Client.Documents;
using Raven.Client.Documents.BulkInsert;
using Raven.Client.Documents.Operations;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.ReferenceData.Releases;

/// <summary>What <see cref="ReleaseApplier"/> does with a document the database already holds under a different hash.</summary>
public enum ReleaseOverwrite
{
    /// <summary>A consumer's read-only copy: the release always wins.</summary>
    Always,

    /// <summary>
    /// The instance's seed (RD9): only a document the seed itself wrote (its <c>Release.Changed</c> is the seed's
    /// release) is refreshed; a document a full import wrote since is left alone.
    /// </summary>
    OnlySeedOwned,
}

/// <summary>
/// Writes a release into a database (§4.7): a document whose <c>Hash</c> already matches is skipped, so re-applying a
/// release writes <b>0 documents</b> (no re-indexing, no subscription wake-up, no change-vector churn). A first load into
/// an empty collection goes through BulkInsert with explicit ids and <c>SkipOverwriteIfUnchanged</c> (RD-DEF 12).
/// Documents are never deleted; <c>refdata/state</c> is written last, and only when something changed.
/// </summary>
public static class ReleaseApplier
{
    private const int Chunk = 256;

    public static async Task<ReleaseApplyResult> ApplyAsync(IDocumentStore store, ReleaseContent release, ReleaseOverwrite overwrite, CancellationToken cancellationToken)
    {
        int added = 0, updated = 0, skipped = 0;
        var counts = await CollectionCountsAsync(store, cancellationToken);
        foreach (var group in release.Documents.Values.GroupBy(d => d.GetType()))
        {
            var collection = ReleaseFormat.CollectionOf(group.Key).Name;
            if (counts.GetValueOrDefault(collection) == 0)
            {
                // The first load (§4.5 step 7, §4.7): nothing to compare against, so stream it in.
                await using var bulk = store.BulkInsert(new BulkInsertOptions { SkipOverwriteIfUnchanged = true }, cancellationToken);
                foreach (var document in group)
                {
                    await bulk.StoreAsync(document, ReleaseWriter.IdOf(document));
                    added++;
                }
                continue;
            }

            foreach (var chunk in group.Chunk(Chunk))
            {
                using var session = store.OpenAsyncSession();
                session.Advanced.MaxNumberOfRequestsPerSession = int.MaxValue;
                var ids = chunk.Select(ReleaseWriter.IdOf).ToList();
                var existing = await LoadAsync(session, group.Key, ids, cancellationToken);
                var dirty = false;
                foreach (var document in chunk)
                {
                    var id = ReleaseWriter.IdOf(document);
                    var hash = ReleaseWriter.HashOf(document);
                    if (existing.TryGetValue(id, out var stored) && stored is not null)
                    {
                        if (string.Equals(HashOf(stored), hash, StringComparison.Ordinal))
                        {
                            skipped++;
                            continue;
                        }
                        if (overwrite == ReleaseOverwrite.OnlySeedOwned && ReleaseChangedOf(stored) != release.Manifest.Release)
                        {
                            skipped++;
                            continue;
                        }
                        session.Advanced.Evict(stored);
                        updated++;
                    }
                    else
                    {
                        added++;
                    }
                    await session.StoreAsync(document, id, cancellationToken);
                    dirty = true;
                }
                if (dirty)
                    await session.SaveChangesAsync(cancellationToken);
            }
        }

        // refdata/state last (§4.7): the release and, per collection, the digest of what the database now holds.
        var digests = await LocalDigestsAsync(store, release.Manifest.Files.Select(f => f.Collection).Distinct(StringComparer.Ordinal), cancellationToken);
        using (var session = store.OpenAsyncSession())
        {
            var state = await session.LoadAsync<RefDataState>(RefDataState.DocumentId, cancellationToken);
            var unchanged = state is not null
                && state.Release == release.Manifest.Release
                && state.Digests.Count == digests.Count
                && digests.All(d => state.Digests.TryGetValue(d.Key, out var v) && v == d.Value);
            if (!unchanged || added + updated > 0)
            {
                state ??= new RefDataState();
                state.Release = release.Manifest.Release;
                state.Digests = digests;
                state.AppliedAtUtc = TimeProvider.System.GetUtcNow();
                await session.StoreAsync(state, RefDataState.DocumentId, cancellationToken);
                await session.SaveChangesAsync(cancellationToken);
            }
        }

        return new ReleaseApplyResult(added, updated, skipped);
    }

    /// <summary>Per collection: the digest over (id, Hash) of every document the database holds (§4.7).</summary>
    public static async Task<Dictionary<string, string>> LocalDigestsAsync(IDocumentStore store, IEnumerable<string> collections, CancellationToken cancellationToken)
    {
        var digests = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var collection in collections)
            digests[collection] = ReleaseCanonical.Digest((await StampsAsync(store, collection, cancellationToken)).Select(s => (s.Id, s.Hash ?? string.Empty)));
        return digests;
    }

    /// <summary>
    /// Per release file: the digest over (id, Hash) of the documents the database holds for that file's country and
    /// collection. The periodic checksum (§4.7) compares it with the manifest; a mismatch re-imports the file in full.
    /// </summary>
    public static async Task<Dictionary<string, string>> LocalFileDigestsAsync(IDocumentStore store, IEnumerable<ReleaseFile> files, CancellationToken cancellationToken)
    {
        var digests = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var collection in files.GroupBy(f => f.Collection, StringComparer.Ordinal))
        {
            var stamps = await StampsAsync(store, collection.Key, cancellationToken);
            foreach (var file in collection)
                digests[file.Path] = ReleaseCanonical.Digest(stamps
                    .Where(s => string.Equals(RefIds.CountryOf(s.Id) ?? (s.Country is { Length: > 0 } c ? c.ToLowerInvariant() : "_"), file.Country, StringComparison.Ordinal))
                    .Select(s => (s.Id, s.Hash ?? string.Empty)));
        }
        return digests;
    }

    /// <summary>The number of documents per collection (one maintenance call).</summary>
    public static async Task<IReadOnlyDictionary<string, long>> CollectionCountsAsync(IDocumentStore store, CancellationToken cancellationToken)
    {
        var statistics = await store.Maintenance.SendAsync(new GetCollectionStatisticsOperation(), cancellationToken);
        return statistics.Collections.ToDictionary(c => c.Key, c => c.Value, StringComparer.OrdinalIgnoreCase);
    }

    private static async Task<List<Stamp>> StampsAsync(IDocumentStore store, string collection, CancellationToken cancellationToken)
    {
        using var session = store.OpenAsyncSession();
        return await session.Advanced.AsyncRawQuery<Stamp>($"from {CollectionIdentifier(collection)} select id() as Id, Hash, Country")
            .Take(int.MaxValue).ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Only the release format's own collection names reach RQL (repo rule: identifiers are validated, never spliced
    /// raw). This package has no ASP.NET Core, so it checks against <see cref="ReleaseFormat.Collections"/> itself.
    /// </summary>
    private static string CollectionIdentifier(string collection)
        => ReleaseFormat.CollectionNamed(collection) is not null && collection.All(char.IsAsciiLetter)
            ? collection
            : throw new ArgumentException($"'{collection}' is not a release collection.", nameof(collection));

    private static async Task<IReadOnlyDictionary<string, object?>> LoadAsync(IAsyncDocumentSession session, Type type, List<string> ids, CancellationToken cancellationToken)
    {
        // A typed load per collection: the stored documents come back as the same CLR types the release holds.
        var method = typeof(ReleaseApplier).GetMethod(nameof(LoadTypedAsync), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .MakeGenericMethod(type);
        return await (Task<IReadOnlyDictionary<string, object?>>)method.Invoke(null, [session, ids, cancellationToken])!;
    }

    private static async Task<IReadOnlyDictionary<string, object?>> LoadTypedAsync<T>(IAsyncDocumentSession session, List<string> ids, CancellationToken cancellationToken)
    {
        var loaded = await session.LoadAsync<T>(ids, cancellationToken);
        return loaded.ToDictionary(p => p.Key, p => (object?)p.Value, StringComparer.OrdinalIgnoreCase);
    }

    private static string? HashOf(object document) => document.GetType().GetProperty("Hash")?.GetValue(document) as string;

    private static string? ReleaseChangedOf(object document)
        => (document.GetType().GetProperty("Release")?.GetValue(document) as ReleaseStamp)?.Changed;

    private sealed class Stamp
    {
        public string Id { get; set; } = string.Empty;
        public string? Hash { get; set; }
        public string? Country { get; set; }
    }
}

/// <param name="Added">Documents the database did not hold.</param>
/// <param name="Updated">Documents whose hash changed.</param>
/// <param name="Skipped">Documents already identical (or, for the seed, owned by a full import).</param>
public sealed record ReleaseApplyResult(int Added, int Updated, int Skipped)
{
    public int Written => Added + Updated;
}
