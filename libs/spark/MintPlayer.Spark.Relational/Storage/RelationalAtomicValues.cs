using Microsoft.EntityFrameworkCore;
using MintPlayer.Spark.Abstractions.Storage;
using MintPlayer.Spark.Relational.Model;

namespace MintPlayer.Spark.Relational.Storage;

/// <summary>
/// <see cref="ISparkAtomicValues"/> on the relational providers (#536 S15): table <c>SparkAtomicValues</c>, each call one
/// statement. A put at version <c>0</c> is an <c>INSERT</c> (a duplicate key means another writer was first); any other
/// put or delete carries <c>WHERE Version = @expected</c>.
/// </summary>
public sealed class RelationalAtomicValues : ISparkAtomicValues
{
    private readonly RelationalSparkStore store;

    public RelationalAtomicValues(RelationalSparkStore store) => this.store = store;

    public async Task<SparkAtomicValue?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        await using var db = store.NewContext();
        var row = await db.Set<SparkAtomicValueRow>().AsNoTracking().FirstOrDefaultAsync(r => r.Key == key, cancellationToken);
        return row is null ? null : new SparkAtomicValue(row.Key, row.Value, row.Version);
    }

    public async Task<SparkAtomicValue?> TryPutAsync(string key, string value, long expectedVersion, CancellationToken cancellationToken = default)
    {
        await using var db = store.NewContext();
        if (expectedVersion == 0)
        {
            db.Add(new SparkAtomicValueRow { Key = key, Value = value, Version = 1 });
            try
            {
                await db.SaveChangesAsync(cancellationToken);
                return new SparkAtomicValue(key, value, 1);
            }
            catch (DbUpdateException ex) when (ex.InnerException is { } inner && db.Setup.Dialect.Classify(inner) is { Kind: SparkConstraintKind.Unique })
            {
                // The key exists: another writer put it first (or it was never deleted). Any other failure is no other
                // writer's, and throws: a caller that retries on null (the HiLo reservation) would retry it forever.
                return null;
            }
        }

        var updated = await db.Set<SparkAtomicValueRow>()
            .Where(r => r.Key == key && r.Version == expectedVersion)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.Value, value).SetProperty(r => r.Version, r => r.Version + 1), cancellationToken);
        return updated == 1 ? new SparkAtomicValue(key, value, expectedVersion + 1) : null;
    }

    public async Task<bool> TryDeleteAsync(string key, long expectedVersion, CancellationToken cancellationToken = default)
    {
        await using var db = store.NewContext();
        return await db.Set<SparkAtomicValueRow>()
            .Where(r => r.Key == key && r.Version == expectedVersion)
            .ExecuteDeleteAsync(cancellationToken) == 1;
    }
}
