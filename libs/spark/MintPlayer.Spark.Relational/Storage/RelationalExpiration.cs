using System.Collections.Concurrent;
using System.Data.Common;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Storage;
using MintPlayer.Spark.Relational.Model;

namespace MintPlayer.Spark.Relational.Storage;

/// <summary>
/// <see cref="ISparkStoreExpiration"/> on the relational providers (#551 D18, #536 S6): the stamper mirrors
/// <see cref="IExpiring.ExpiresAtUtc"/> into each row's <c>SparkExpiresAt</c> column at save, and the sweep deletes the
/// rows past it, table by table, every delete frequency.
/// </summary>
public sealed class RelationalExpiration : ISparkStoreExpiration, IDisposable
{
    private readonly RelationalSparkStore store;
    private readonly ILogger<RelationalExpiration>? logger;
    private Timer? sweep;

    public RelationalExpiration(RelationalSparkStore store, ILogger<RelationalExpiration>? logger = null)
    {
        this.store = store;
        this.logger = logger;
    }

    public IDisposable InstallStamper(Func<Type, TimeSpan?> defaultExpiresAfter, TimeProvider time)
    {
        var defaults = new ConcurrentDictionary<Type, TimeSpan?>();
        return store.AddBeforeSave((entity, isNew, setExpiry) =>
        {
            if (entity is not IExpiring expiring)
                return;
            if (expiring.ExpiresAtUtc is null && isNew && defaults.GetOrAdd(entity.GetType(), defaultExpiresAfter) is { } after)
                expiring.ExpiresAtUtc = time.GetUtcNow().UtcDateTime + after;
            if (expiring.ExpiresAtUtc is { } expires)
            {
                var utc = Spark.Expiration.SparkExpiration.ToUtc(expires);
                expiring.ExpiresAtUtc = utc;
                setExpiry(new DateTimeOffset(utc, TimeSpan.Zero));
            }
            else
                setExpiry(null);
        });
    }

    public void EnableSweep(TimeSpan deleteFrequency)
    {
        var next = new Timer(_ => _ = SweepAsync(TimeProvider.System, CancellationToken.None), null, deleteFrequency, deleteFrequency);
        Interlocked.Exchange(ref sweep, next)?.Dispose();
    }

    /// <summary>Deletes every row whose expiry has passed. A row another row still references stays, and is logged.</summary>
    public async Task<int> SweepAsync(TimeProvider time, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var deleted = 0;
        foreach (var table in store.Model.Tables)
        {
            try
            {
                await using var db = store.NewContext();
                deleted += await (Task<int>)SweepTableMethod.MakeGenericMethod(table.Root).Invoke(null, [db, now, cancellationToken])!;
            }
            catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException)
            {
                logger?.LogWarning(ex, "Expired rows of {Table} were not deleted.", table.Name);
            }
        }
        try
        {
            // The change feed's tombstones (D6) expire too, also in an app whose feed nobody watches.
            await RelationalSparkChangeFeed.TrimTombstonesAsync(store, now - RelationalSparkChangeFeed.TombstoneRetention, cancellationToken);
        }
        catch (DbException ex)
        {
            logger?.LogWarning(ex, "Expired tombstones were not deleted.");
        }
        return deleted;
    }

    private static readonly MethodInfo SweepTableMethod = typeof(RelationalExpiration).GetMethod(nameof(SweepTable), BindingFlags.NonPublic | BindingFlags.Static)!;

    /// <summary>Through <see cref="RelationalTombstones.DeleteAsync"/>, so an expired row leaves its tombstone (D6) and delete revision (S8).</summary>
    private static Task<int> SweepTable<T>(SparkDbContext db, DateTimeOffset now, CancellationToken cancellationToken) where T : class
        => RelationalTombstones.DeleteAsync<T>(db, q => q.Where(x => EF.Property<DateTimeOffset?>(x, SparkDbContext.ExpiresProperty) <= now), now, cancellationToken);

    public void Dispose() => Interlocked.Exchange(ref sweep, null)?.Dispose();
}
