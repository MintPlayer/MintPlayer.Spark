using System.Globalization;
using System.Linq.Expressions;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Storage;

namespace MintPlayer.Spark.Storage.InMemory;

/// <summary>
/// <see cref="ISparkSubscriptions"/> over the database's commit order (#536 D39): a version <c>A:{n}</c> is the stamp,
/// handed out under the commit lock, so every version up to <see cref="InMemoryDatabase.LastEtag"/> has committed.
/// A commit wakes the runners at once.
/// </summary>
[Register(typeof(ISparkSubscriptions), ServiceLifetime.Singleton)]
internal sealed partial class InMemorySparkSubscriptions : SparkCursorSubscriptions
{
    [Inject] private readonly InMemoryDatabase database;
    [Inject] private readonly ISparkAtomicValues values;
    [Inject] private readonly ISparkStoreLock locks;

    protected override ISparkAtomicValues Values => values;

    protected override ISparkStoreLock Locks => locks;

    protected override TimeSpan PollInterval => TimeSpan.FromMilliseconds(250);

    protected override TimeSpan StandbyInterval => TimeSpan.FromMilliseconds(250);

    protected override Task<ulong> ReadHorizonAsync(CancellationToken cancellationToken)
        => Task.FromResult((ulong)database.LastEtag + 1);

    protected override async Task<IReadOnlyList<SparkStampedDocument<T>>> ReadAsync<T>(Expression<Func<T, bool>>? filter,
        ulong after, ulong before, int take, CancellationToken cancellationToken)
    {
        using var session = new InMemorySparkSession(database);
        var query = filter is null ? session.Query<T>() : session.Query<T>().Where(filter);
        var rows = new List<SparkStampedDocument<T>>();
        await foreach (var row in session.StreamAsync(query, cancellationToken))
        {
            if (row.Id is null || row.Document is null || Stamp(row.Version) is not { } stamp || stamp <= after || stamp >= before)
                continue;
            rows.Add(new SparkStampedDocument<T>(row.Id, row.Document, row.Version, stamp));
        }
        var ordered = rows.OrderBy(r => r.Stamp);
        return take > 0 ? [.. ordered.Take(take)] : [.. ordered];
    }

    protected override IDisposable? OnCommitted(Action wake)
    {
        void Handler(IReadOnlyList<SparkStoreChange> _) => wake();
        database.Committed += Handler;
        return new Unsubscribe(() => database.Committed -= Handler);
    }

    private static ulong? Stamp(string? version)
        => version is { Length: > 2 } && version.StartsWith("A:", StringComparison.Ordinal)
            && ulong.TryParse(version.AsSpan(2), NumberStyles.None, CultureInfo.InvariantCulture, out var stamp)
                ? stamp
                : null;

    private sealed class Unsubscribe(Action action) : IDisposable
    {
        private Action? action = action;

        public void Dispose() => Interlocked.Exchange(ref action, null)?.Invoke();
    }
}
