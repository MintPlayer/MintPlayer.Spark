using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using MintPlayer.Spark.Abstractions.Reflection;
using MintPlayer.Spark.Relational.Model;

namespace MintPlayer.Spark.Relational.Storage;

/// <summary>
/// Deletes leave a tombstone in the deleting transaction (#536 D6), so the change feed reads them below the same
/// horizon as the tables' stamps.
/// </summary>
internal static class RelationalTombstones
{
    /// <summary>Ids per set-based delete statement: each is one <c>IN</c> list.</summary>
    private const int Chunk = 500;

    public static SparkTombstoneRow For(SparkTable table, string id, DateTimeOffset deletedAt)
        => new() { Id = Guid.NewGuid(), Collection = table.Name, DocumentId = id, DeletedAt = deletedAt };

    /// <summary>
    /// <c>DELETE</c> of the rows <paramref name="filter"/> selects, with a tombstone each, in one transaction on
    /// <paramref name="db"/>, a context of its own: its <c>SaveChanges</c> writes the tombstones and nothing else. The
    /// ids are read first; each delete is <c>filter AND key IN (…)</c>, so a row that stopped matching in between stays,
    /// and only the rows the delete removed get a tombstone.
    /// </summary>
    public static async Task<int> DeleteAsync<T>(SparkDbContext db, Func<IQueryable<T>, IQueryable<T>> filter, DateTimeOffset now, CancellationToken cancellationToken)
        where T : class
    {
        var table = db.SparkModel.RequireTable(typeof(T));
        var revisions = db.Setup.RevisionsOf(table.Name);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var rows = await filter(db.Set<T>().AsNoTracking()).Select(KeyAndVersion<T>(table)).ToListAsync(cancellationToken);

        var deleted = 0;
        var touched = new List<RelationalRevisions.Touched>();
        foreach (var chunk in rows.Chunk(Chunk))
        {
            object[] keys = [.. chunk.Select(r => r.Key)];
            var count = await RelationalSparkSession.WhereKeyIn(filter(db.Set<T>()), table, keys).ExecuteDeleteAsync(cancellationToken);
            if (count == 0)
                continue;
            deleted += count;
            var gone = count == chunk.Length ? chunk : await DeletedAsync<T>(db, table, chunk, keys, cancellationToken);
            foreach (var row in gone)
            {
                var id = SparkIdValues.ToText(row.Key)!;
                db.Add(For(table, id, now));
                // S8: a delete revision (or the purge), like the unit of work's delete.
                if (revisions is null)
                    continue;
                if (!revisions.PurgeOnDelete)
                    db.Add(RelationalRevisions.Deleted(table, id, row.Version, now));
                touched.Add(new(table.Name, id, revisions, Deleted: true));
            }
        }
        if (deleted > 0)
            await db.SaveChangesAsync(cancellationToken);
        await RelationalRevisions.ApplyAsync(db, touched, now, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return deleted;
    }

    /// <summary>
    /// The rows of <paramref name="chunk"/> the delete removed, when it removed fewer than were read: a row that stopped
    /// matching in between is still there, and must get no tombstone and no delete revision. Inside the deleting
    /// transaction, the chunk's rows still present are exactly the ones it left.
    /// </summary>
    private static async Task<KeyVersion[]> DeletedAsync<T>(SparkDbContext db, SparkTable table, KeyVersion[] chunk, object[] keys, CancellationToken cancellationToken)
        where T : class
    {
        var left = await RelationalSparkSession.WhereKeyIn(db.Set<T>().AsNoTracking(), table, keys).Select(KeyAndVersion<T>(table)).ToListAsync(cancellationToken);
        var kept = left.Select(r => r.Key).ToHashSet();
        return [.. chunk.Where(r => !kept.Contains(r.Key))];
    }

    /// <summary><c>x =&gt; new KeyVersion { Key = (object)x.Id, Version = EF.Property&lt;long&gt;(x, "Version") }</c>.</summary>
    public static Expression<Func<T, KeyVersion>> KeyAndVersion<T>(SparkTable table)
    {
        var x = Expression.Parameter(typeof(T), "x");
        return Expression.Lambda<Func<T, KeyVersion>>(Expression.MemberInit(Expression.New(typeof(KeyVersion)),
            Expression.Bind(typeof(KeyVersion).GetProperty(nameof(KeyVersion.Key))!,
                Expression.Convert(Expression.Property(Expression.Convert(x, table.Root), table.IdProperty), typeof(object))),
            Expression.Bind(typeof(KeyVersion).GetProperty(nameof(KeyVersion.Version))!,
                Expression.Call(typeof(EF), nameof(EF.Property), [typeof(long)], x, Expression.Constant(SparkDbContext.VersionProperty)))), x);
    }
}

/// <summary>A row's key and version, read before a set-based write (S8).</summary>
internal sealed class KeyVersion
{
    public object Key { get; set; } = default!;
    public long Version { get; set; }
}
