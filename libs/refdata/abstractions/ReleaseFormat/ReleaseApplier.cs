using Raven.Client.Documents;
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
/// release writes <b>0 documents</b> (no re-indexing, no subscription wake-up, no change-vector churn). Documents are
/// never deleted; <c>refdata/state</c> is written last, and only when something changed.
/// </summary>
public static class ReleaseApplier
{
    private const int Chunk = 256;

    public static async Task<ReleaseApplyResult> ApplyAsync(IDocumentStore store, ReleaseContent release, ReleaseOverwrite overwrite, CancellationToken cancellationToken)
    {
        int added = 0, updated = 0, skipped = 0;
        var byType = release.Documents.Values.GroupBy(d => d.GetType());
        foreach (var group in byType)
        {
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

        var digests = release.Manifest.Files
            .GroupBy(f => f.Collection)
            .ToDictionary(g => g.Key, g => ReleaseCanonical.Digest(g.SelectMany(f => release.Documents.Values
                .Where(d => ReleaseFormat.CollectionOf(d.GetType()).Name == f.Collection
                            && string.Equals(RefIds.CountryOf(ReleaseWriter.IdOf(d)) ?? f.Country, f.Country, StringComparison.OrdinalIgnoreCase))
                .Select(d => (ReleaseWriter.IdOf(d), ReleaseWriter.HashOf(d))))), StringComparer.Ordinal);

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
}

/// <param name="Added">Documents the database did not hold.</param>
/// <param name="Updated">Documents whose hash changed.</param>
/// <param name="Skipped">Documents already identical (or, for the seed, owned by a full import).</param>
public sealed record ReleaseApplyResult(int Added, int Updated, int Skipped)
{
    public int Written => Added + Updated;
}
