using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MintPlayer.Spark.Abstractions.Reflection;
using MintPlayer.Spark.Abstractions.Storage;
using MintPlayer.Spark.Relational.Model;

namespace MintPlayer.Spark.Relational.Storage;

/// <summary>
/// <see cref="ISparkChangeFeed"/> on the relational providers (#536 M4e, D6).
/// <list type="bullet">
/// <item><see cref="OnCommitted"/>: what each of this process's sessions wrote, after its commit.</item>
/// <item>The watches poll. Each poll reads the safe horizon (the dialect's <see cref="SparkRelationalDialect.HorizonSql"/>):
/// when it has not moved, nothing committed and the poll ends there. Otherwise it reads the watched tables' rows and the
/// tombstones whose stamp is in <c>[cursor, horizon)</c>, and the horizon becomes the cursor. A stamp below the horizon
/// belongs to a committed write and a write still open gets one at or above it, so a transaction that commits late is
/// read by a later poll, never skipped (the failure that ruled out sequences).</item>
/// <item>A watch is confirmed once the feed has a cursor: every write that commits after that is read.</item>
/// <item>A poll that fails is retried at the next interval from the same cursor; the first one that succeeds again raises
/// <see cref="Reconnected"/>, as a lost connection does on RavenDB.</item>
/// </list>
/// ⚠️ One long transaction holds the horizon back, and every watch waits for it (D6's trap).
/// </summary>
public sealed class RelationalSparkChangeFeed : ISparkChangeFeed, IDisposable
{
    /// <summary>How often the watches poll, unless the constructor says otherwise.</summary>
    public static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(1);

    /// <summary>How long a tombstone is kept: a watch that has been failing for longer re-evaluates on <see cref="Reconnected"/> anyway.</summary>
    public static readonly TimeSpan TombstoneRetention = TimeSpan.FromDays(1);

    private static readonly TimeSpan TrimInterval = TimeSpan.FromHours(1);

    private readonly RelationalSparkStore store;
    private readonly TimeSpan pollInterval;
    private readonly ILogger<RelationalSparkChangeFeed>? logger;
    private readonly List<Watch> watches = [];
    private readonly SemaphoreSlim polling = new(1, 1);
    private readonly CancellationTokenSource stop = new();
    private Task? started;
    private ulong? cursor;
    private bool failing;
    private DateTimeOffset lastTrim = DateTimeOffset.MinValue;

    public RelationalSparkChangeFeed(RelationalSparkStore store, TimeSpan? pollInterval = null, ILogger<RelationalSparkChangeFeed>? logger = null)
    {
        this.store = store;
        this.pollInterval = pollInterval ?? DefaultPollInterval;
        this.logger = logger;
    }

    public IDisposable OnCommitted(Action<SparkStoreChange> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        Action<IReadOnlyList<SparkStoreChange>> forward = changes =>
        {
            foreach (var change in changes)
                Invoke(handler, change);
        };
        store.Committed += forward;
        return new Removal(() => store.Committed -= forward);
    }

    public SparkChangeWatch WatchDocument(string id, Action<SparkStoreChange> handler)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        var lower = id.ToLowerInvariant();
        var table = store.Model.Tables.FirstOrDefault(t => lower.StartsWith(t.Prefix, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"'{id}' is not an id of any table of this store.");
        return Add(new Watch(table, lower, handler));
    }

    public SparkChangeWatch WatchCollection(string collection, Action<SparkStoreChange> handler)
    {
        var table = store.Model.Tables.FirstOrDefault(t => string.Equals(t.Name, collection, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"'{collection}' is not a table of this store.");
        return Add(new Watch(table, null, handler));
    }

    public event Action? Reconnected;

    private SparkChangeWatch Add(Watch watch)
    {
        ArgumentNullException.ThrowIfNull(watch.Handler);
        lock (watches)
        {
            watches.Add(watch);
            started ??= StartAsync();
        }
        return new SparkChangeWatch(new Removal(() => { lock (watches) watches.Remove(watch); }), started);
    }

    private async Task StartAsync()
    {
        await PollAsync(stop.Token);
        _ = Task.Run(LoopAsync);
    }

    private async Task LoopAsync()
    {
        using var timer = new PeriodicTimer(pollInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stop.Token))
            {
                try
                {
                    await PollAsync(stop.Token);
                    if (failing)
                    {
                        failing = false;
                        Reconnected?.Invoke();
                    }
                    await TrimAsync(stop.Token);
                }
                catch (Exception ex) when (!stop.IsCancellationRequested)
                {
                    failing = true;
                    logger?.LogWarning(ex, "The change feed's poll failed; it retries from the same point.");
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
        }
    }

    /// <summary>One poll: reads what committed since the cursor and reports it to the watches.</summary>
    internal async Task PollAsync(CancellationToken cancellationToken)
    {
        await polling.WaitAsync(cancellationToken);
        try
        {
            await using var db = store.NewContext();
            var horizon = await ReadHorizonAsync(db, cancellationToken);
            if (cursor is not { } from || horizon <= from)
            {
                cursor ??= horizon;
                return;
            }

            Watch[] current;
            lock (watches)
                current = [.. watches];
            var changes = new List<SparkStoreChange>();
            foreach (var table in current.Select(w => w.Table).Distinct())
                changes.AddRange(await (Task<List<SparkStoreChange>>)ChangedRowsMethod.MakeGenericMethod(table.Root)
                    .Invoke(null, [db, table, from, horizon, cancellationToken])!);
            if (current.Length > 0)
            {
                var tombstones = await db.Set<SparkTombstoneRow>().AsNoTracking()
                    .Where(t => t.Stamp >= from && t.Stamp < horizon)
                    .OrderBy(t => t.Stamp)
                    .Select(t => new { t.DocumentId, t.Collection })
                    .ToListAsync(cancellationToken);
                changes.AddRange(tombstones.Select(t => new SparkStoreChange(t.DocumentId, t.Collection, null)));
            }
            cursor = horizon;

            foreach (var change in changes)
                foreach (var watch in current)
                    if (watch.Matches(change))
                        Invoke(watch.Handler, change);
        }
        finally
        {
            polling.Release();
        }
    }

    private async Task<ulong> ReadHorizonAsync(SparkDbContext db, CancellationToken cancellationToken)
    {
        await db.Database.OpenConnectionAsync(cancellationToken);
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = store.Setup.Dialect.HorizonSql;
        return Convert.ToUInt64(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static readonly MethodInfo ChangedRowsMethod = typeof(RelationalSparkChangeFeed).GetMethod(nameof(ChangedRowsAsync), BindingFlags.NonPublic | BindingFlags.Static)!;

    /// <summary>The rows of <paramref name="table"/> whose stamp is in <c>[from, to)</c>: id and version only.</summary>
    private static async Task<List<SparkStoreChange>> ChangedRowsAsync<T>(SparkDbContext db, SparkTable table, ulong from, ulong to, CancellationToken cancellationToken)
        where T : class
    {
        var x = Expression.Parameter(typeof(T), "x");
        var key = Expression.Convert(Expression.Property(x, table.IdProperty), typeof(object));
        var version = Expression.Call(typeof(EF), nameof(EF.Property), [typeof(long)], x, Expression.Constant(SparkDbContext.VersionProperty));
        var pair = Expression.New(typeof(KeyValuePair<object, long>).GetConstructor([typeof(object), typeof(long)])!, key, version);
        var rows = await db.Set<T>().AsNoTracking()
            .Where(e => EF.Property<ulong>(e, SparkDbContext.StampProperty) >= from && EF.Property<ulong>(e, SparkDbContext.StampProperty) < to)
            .Select(Expression.Lambda<Func<T, KeyValuePair<object, long>>>(pair, x))
            .ToListAsync(cancellationToken);
        return [.. rows.Select(r => new SparkStoreChange(SparkIdValues.ToText(r.Key), table.Name, RelationalSparkSession.FormatVersion(r.Value)))];
    }

    /// <summary>Deletes the tombstones past <see cref="TombstoneRetention"/>, at most once per <see cref="TrimInterval"/>.</summary>
    private async Task TrimAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        if (now - lastTrim < TrimInterval)
            return;
        lastTrim = now;
        await TrimTombstonesAsync(store, now - TombstoneRetention, cancellationToken);
    }

    /// <summary>Deletes the tombstones written before <paramref name="before"/>.</summary>
    internal static async Task<int> TrimTombstonesAsync(RelationalSparkStore store, DateTimeOffset before, CancellationToken cancellationToken)
    {
        await using var db = store.NewContext();
        return await db.Set<SparkTombstoneRow>().Where(t => t.DeletedAt < before).ExecuteDeleteAsync(cancellationToken);
    }

    private void Invoke(Action<SparkStoreChange> handler, SparkStoreChange change)
    {
        try
        {
            handler(change);
        }
        catch (Exception ex)
        {
            // A handler never throws into a writer or the poll (ISparkChangeFeed).
            logger?.LogWarning(ex, "A change feed handler threw for {DocumentId}.", change.DocumentId);
        }
    }

    public void Dispose()
    {
        stop.Cancel();
        lock (watches)
            watches.Clear();
    }

    private sealed record Watch(SparkTable Table, string? Id, Action<SparkStoreChange> Handler)
    {
        public bool Matches(SparkStoreChange change)
            => string.Equals(change.Collection, Table.Name, StringComparison.OrdinalIgnoreCase)
               && (Id is null || string.Equals(change.DocumentId, Id, StringComparison.OrdinalIgnoreCase));
    }

    private sealed class Removal(Action remove) : IDisposable
    {
        private Action? remove = remove;

        public void Dispose() => Interlocked.Exchange(ref remove, null)?.Invoke();
    }
}
