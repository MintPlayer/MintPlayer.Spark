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
    /// ids are read first; each delete is <c>filter AND key IN (…)</c>, so a row that stopped matching in between stays.
    /// </summary>
    public static async Task<int> DeleteAsync<T>(SparkDbContext db, Func<IQueryable<T>, IQueryable<T>> filter, DateTimeOffset now, CancellationToken cancellationToken)
        where T : class
    {
        var table = db.SparkModel.RequireTable(typeof(T));
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var x = Expression.Parameter(typeof(T), "x");
        var key = Expression.Lambda<Func<T, object>>(Expression.Convert(Expression.Property(Expression.Convert(x, table.Root), table.IdProperty), typeof(object)), x);
        var keys = await filter(db.Set<T>()).Select(key).ToListAsync(cancellationToken);

        var deleted = 0;
        foreach (var chunk in keys.Chunk(Chunk))
        {
            var count = await RelationalSparkSession.WhereKeyIn(filter(db.Set<T>()), table, chunk).ExecuteDeleteAsync(cancellationToken);
            if (count == 0)
                continue;
            deleted += count;
            db.AddRange(chunk.Select(k => For(table, SparkIdValues.ToText(k)!, now)));
        }
        if (deleted > 0)
            await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return deleted;
    }
}
