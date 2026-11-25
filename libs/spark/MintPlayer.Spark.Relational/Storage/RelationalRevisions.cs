using Microsoft.EntityFrameworkCore;
using MintPlayer.Spark.Abstractions.Storage;
using MintPlayer.Spark.Relational.Model;
using MintPlayer.Spark.Storage;

namespace MintPlayer.Spark.Relational.Storage;

/// <summary>
/// Writes revisions (#536 S8, PRD O12) into <c>spark.Revisions</c>, in the writing transaction, for every write path:
/// the unit of work (<see cref="RelationalSparkSession.SaveChangesAsync"/>, <c>PatchAppend</c> included), the set-based
/// <c>UpdateWhereAsync</c> and <c>DeleteWhereAsync</c>, and the expiry sweep. Framework SQL never writes an entity table
/// any other way (the PRD's trap).
/// </summary>
internal static class RelationalRevisions
{
    /// <summary>A revision of <paramref name="entity"/> at <paramref name="version"/>: the whole aggregate as JSON.</summary>
    public static SparkRevisionRow Of(SparkTable table, string id, long version, object entity, DateTimeOffset at) => new()
    {
        Id = Guid.NewGuid(),
        Collection = table.Name,
        DocumentId = id,
        Version = version,
        At = at,
        ClrType = entity.GetType().AssemblyQualifiedName,
        Json = SparkDocumentJson.Serialize(entity),
    };

    /// <summary>The revision a delete leaves: no content, at the deleted version + 1.</summary>
    public static SparkRevisionRow Deleted(SparkTable table, string id, long deletedVersion, DateTimeOffset at) => new()
    {
        Id = Guid.NewGuid(),
        Collection = table.Name,
        DocumentId = id,
        Version = deletedVersion + 1,
        At = at,
    };

    /// <summary>What a write did to one document of a collection that keeps revisions, for <see cref="ApplyAsync"/>.</summary>
    public sealed record Touched(string Collection, string DocumentId, SparkRevisionSettings Settings, bool Deleted);

    /// <summary>
    /// After the write, in its transaction: purges the revisions of documents deleted from a purge-on-delete collection,
    /// and prunes the others by their collection's retention.
    /// </summary>
    public static async Task ApplyAsync(DbContext db, IReadOnlyCollection<Touched> touched, DateTimeOffset now, CancellationToken cancellationToken)
    {
        foreach (var item in touched)
        {
            var revisions = db.Set<SparkRevisionRow>().Where(r => r.Collection == item.Collection && r.DocumentId == item.DocumentId);
            if (item.Deleted && item.Settings.PurgeOnDelete)
            {
                await revisions.ExecuteDeleteAsync(cancellationToken);
                continue;
            }
            if (item.Settings.MinimumRevisionsToKeep is null && item.Settings.MinimumRevisionAgeToKeep is null)
                continue;

            var newestFirst = await revisions.OrderByDescending(r => r.At).ThenByDescending(r => r.Version)
                .Select(r => new { r.Id, r.At })
                .ToListAsync(cancellationToken);
            var expired = SparkRevisionRetention.Expired([.. newestFirst.Select(r => r.At)], item.Settings, now)
                .Select(i => newestFirst[i].Id)
                .ToList();
            foreach (var chunk in expired.Chunk(500))
                await db.Set<SparkRevisionRow>().Where(r => chunk.Contains(r.Id)).ExecuteDeleteAsync(cancellationToken);
        }
    }

    /// <summary>A revision row read back as its document, or <see langword="null"/> for a delete revision or an unknown type.</summary>
    public static object? Read(SparkRevisionRow row)
        => row.Json is null || row.ClrType is null || Type.GetType(row.ClrType) is not { } type ? null : SparkDocumentJson.Deserialize(row.Json, type);
}

/// <summary>The relational providers' <see cref="ISparkRevisions"/> (#536 S8): the <c>spark.Revisions</c> table.</summary>
/// <remarks>
/// A revision's version is the document's version (D3). Versions restart at 1 when a deleted document is stored again, so
/// a version can name a revision of each of its lives; <see cref="GetAsync"/> answers with the newest.
/// </remarks>
internal sealed class RelationalSparkRevisions(RelationalSparkStore store) : ISparkRevisions
{
    public Task<IReadOnlyList<string>> ConfigureAsync(IReadOnlyList<SparkRevisionCollection> collections, CancellationToken cancellationToken = default)
    {
        var changed = new List<string>();
        foreach (var collection in collections)
        {
            var table = store.Model.Tables.FirstOrDefault(t => string.Equals(t.Name, collection.Collection, StringComparison.OrdinalIgnoreCase));
            var key = table?.Name ?? collection.Collection;
            if (store.Setup.Revisions.TryGetValue(key, out var current) && current == collection.Settings)
                continue;
            store.Setup.Revisions[key] = collection.Settings;
            changed.Add(collection.Label);
        }
        return Task.FromResult<IReadOnlyList<string>>(changed);
    }

    public Task<SparkRevisionLimits?> GetLimitsAsync(CancellationToken cancellationToken = default) => Task.FromResult<SparkRevisionLimits?>(null);

    public async Task<IReadOnlyList<SparkRevisionInfo>> ListAsync(Type entityType, string id, int skip, int take, CancellationToken cancellationToken = default)
    {
        var collection = store.GetCollectionName(entityType);
        var lowered = id.ToLowerInvariant();
        await using var db = store.NewContext();
        var rows = await db.Set<SparkRevisionRow>().AsNoTracking()
            .Where(r => r.Collection == collection && r.DocumentId == lowered)
            .OrderByDescending(r => r.At).ThenByDescending(r => r.Version)
            .Skip(Math.Max(0, skip)).Take(Math.Max(0, take))
            .Select(r => new { r.Version, r.At, IsDelete = r.Json == null })
            .ToListAsync(cancellationToken);
        return [.. rows.Select(r => new SparkRevisionInfo(RelationalSparkSession.FormatVersion(r.Version), r.At, r.IsDelete))];
    }

    public async Task<IReadOnlyDictionary<string, object>> GetAsync(Type entityType, string id, IReadOnlyCollection<string> versions, CancellationToken cancellationToken = default)
    {
        var numbers = versions.Select(v => long.TryParse(v, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : (long?)null)
            .OfType<long>().Distinct().ToList();
        if (numbers.Count == 0)
            return new Dictionary<string, object>();

        var collection = store.GetCollectionName(entityType);
        var lowered = id.ToLowerInvariant();
        await using var db = store.NewContext();
        var rows = await db.Set<SparkRevisionRow>().AsNoTracking()
            .Where(r => r.Collection == collection && r.DocumentId == lowered && r.Json != null && numbers.Contains(r.Version))
            .OrderByDescending(r => r.At)
            .ToListAsync(cancellationToken);

        var result = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            var version = RelationalSparkSession.FormatVersion(row.Version);
            if (result.ContainsKey(version) || RelationalRevisions.Read(row) is not { } document || !entityType.IsInstanceOfType(document))
                continue;
            result[version] = document;
        }
        return result;
    }

    public Task EnsureCanDeleteAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public async Task<long> DeleteAsync(Type entityType, string id, CancellationToken cancellationToken = default)
    {
        var collection = store.GetCollectionName(entityType);
        var lowered = id.ToLowerInvariant();
        await using var db = store.NewContext();
        return await db.Set<SparkRevisionRow>().Where(r => r.Collection == collection && r.DocumentId == lowered).ExecuteDeleteAsync(cancellationToken);
    }
}
