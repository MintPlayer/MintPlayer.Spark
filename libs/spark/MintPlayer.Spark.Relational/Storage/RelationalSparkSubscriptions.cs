using System.Globalization;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using MintPlayer.Spark.Abstractions.Storage;

namespace MintPlayer.Spark.Relational.Storage;

/// <summary>
/// <see cref="ISparkSubscriptions"/> on SQL Server and PostgreSQL (#536 D39, D6): the cursor runs over each table's
/// commit stamp (<c>rowversion</c> / <c>xid8</c>) below the dialect's safe horizon, so two interleaved transactions never
/// skip a row. This process's own commits wake the runners; other processes' are seen on the next poll.
/// </summary>
public sealed class RelationalSparkSubscriptions : SparkCursorSubscriptions
{
    private readonly RelationalSparkStore store;
    private readonly ISparkAtomicValues values;
    private readonly ISparkStoreLock locks;
    private readonly ISparkChangeFeed? changeFeed;
    private readonly TimeSpan pollInterval;

    public RelationalSparkSubscriptions(RelationalSparkStore store, ISparkAtomicValues values, ISparkStoreLock locks,
        ISparkChangeFeed? changeFeed = null, TimeSpan? pollInterval = null)
    {
        this.store = store;
        this.values = values;
        this.locks = locks;
        this.changeFeed = changeFeed;
        this.pollInterval = pollInterval ?? TimeSpan.FromSeconds(1);
    }

    protected override ISparkAtomicValues Values => values;

    protected override ISparkStoreLock Locks => locks;

    protected override TimeSpan PollInterval => pollInterval;

    protected override TimeSpan StandbyInterval => pollInterval;

    protected override async Task<ulong> ReadHorizonAsync(CancellationToken cancellationToken)
    {
        await using var db = store.NewContext();
        await db.Database.OpenConnectionAsync(cancellationToken);
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = store.Setup.Dialect.HorizonSql;
        return Convert.ToUInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    protected override async Task<IReadOnlyList<SparkStampedDocument<T>>> ReadAsync<T>(Expression<Func<T, bool>>? filter,
        ulong after, ulong before, int take, CancellationToken cancellationToken)
    {
        using var session = (RelationalSparkSession)store.OpenSession();
        return await session.ReadStampedAsync(filter, after, before, take, cancellationToken);
    }

    protected override IDisposable? OnCommitted(Action wake) => changeFeed?.OnCommitted(_ => wake());
}
