using System.Globalization;
using System.Linq.Expressions;
using System.Text.Json;

namespace MintPlayer.Spark.Abstractions.Storage;

/// <summary>One row a cursor provider read: the document, its version and its commit stamp.</summary>
public readonly record struct SparkStampedDocument<T>(string Id, T Document, string? Version, ulong Stamp);

/// <summary>
/// <see cref="ISparkSubscriptions"/> for a store with a commit-ordered stamp (#536 D39, D6): InMemory and the relational
/// providers. A subscription is a cursor, "every stamp up to here is handled", kept in <see cref="ISparkAtomicValues"/>
/// under <c>spark/subscriptions/{name}</c>; one runner at a time holds the <see cref="ISparkStoreLock"/> lease
/// <c>spark/subscriptions/{name}/runner</c>.
/// </summary>
/// <remarks>
/// <para>
/// A poll reads the safe horizon first (every stamp below it has committed, D6), then the matching rows with a stamp
/// in <c>(cursor, horizon)</c> by stamp. A batch never splits one stamp: PostgreSQL stamps a whole transaction with one
/// value, so a batch that would cut through it is shortened to the stamps before, or, when one transaction alone
/// fills it, grown to the whole transaction. The cursor moves only after the handler returned, by compare-and-swap:
/// a runner whose lease was taken over cannot move it.
/// </para>
/// <para>
/// When nothing matched, the cursor moves to the horizon all the same, so a filter that skips most rows does not
/// re-read them on every poll.
/// </para>
/// </remarks>
public abstract class SparkCursorSubscriptions : ISparkSubscriptions
{
    /// <summary>Where the cursors and the registry live.</summary>
    protected abstract ISparkAtomicValues Values { get; }

    /// <summary>The runners' leases.</summary>
    protected abstract ISparkStoreLock Locks { get; }

    /// <summary>The atomic value that lists the subscription names.</summary>
    public const string RegistryKey = "spark/subscriptions";

    /// <summary>How long a runner's lease lives without renewal; a dead runner's subscription waits this long at most.</summary>
    public static readonly TimeSpan LeaseTimeToLive = TimeSpan.FromSeconds(30);

    /// <summary>How often a poll runs when nothing woke it.</summary>
    protected virtual TimeSpan PollInterval => TimeSpan.FromSeconds(1);

    /// <summary>How often a waiting runner tries the lease.</summary>
    protected virtual TimeSpan StandbyInterval => TimeSpan.FromSeconds(1);

    /// <summary>The stamp below which every write has committed.</summary>
    protected abstract Task<ulong> ReadHorizonAsync(CancellationToken cancellationToken);

    /// <summary>
    /// The rows of <typeparamref name="T"/> matching <paramref name="filter"/> with a stamp in (<paramref name="after"/>,
    /// <paramref name="before"/>), by stamp, at most <paramref name="take"/> (0 = all).
    /// </summary>
    protected abstract Task<IReadOnlyList<SparkStampedDocument<T>>> ReadAsync<T>(Expression<Func<T, bool>>? filter,
        ulong after, ulong before, int take, CancellationToken cancellationToken) where T : class;

    /// <summary>Calls <paramref name="wake"/> after a commit of this process, to poll early; null when the store cannot tell.</summary>
    protected virtual IDisposable? OnCommitted(Action wake) => null;

    private static string CursorKey(string name) => $"{RegistryKey}/{name}";

    private static string RunnerKey(string name) => $"{RegistryKey}/{name}/runner";

    public async Task EnsureAsync<T>(SparkSubscriptionDefinition<T> definition, CancellationToken cancellationToken = default) where T : class
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentException.ThrowIfNullOrEmpty(definition.Name);
        await ChangeRegistryAsync(names => names.Add(definition.Name), cancellationToken);
    }

    public async Task<IReadOnlyList<string>> GetNamesAsync(CancellationToken cancellationToken = default)
        => [.. Names(await Values.GetAsync(RegistryKey, cancellationToken)).Order(StringComparer.Ordinal)];

    public async Task DeleteAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        await ChangeRegistryAsync(names => names.Remove(name), cancellationToken);
        if (await Values.GetAsync(CursorKey(name), cancellationToken) is { } cursor)
            await Values.TryDeleteAsync(cursor.Key, cursor.Version, cancellationToken);
    }

    private async Task ChangeRegistryAsync(Func<HashSet<string>, bool> change, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            var current = await Values.GetAsync(RegistryKey, cancellationToken);
            var names = Names(current);
            if (!change(names))
                return;
            if (await Values.TryPutAsync(RegistryKey, JsonSerializer.Serialize(names.Order(StringComparer.Ordinal)), current?.Version ?? 0, cancellationToken) is not null)
                return;
            if (attempt >= 20)
                throw new SparkSubscriptionException("The subscription registry kept changing under the write.");
        }
    }

    private static HashSet<string> Names(SparkAtomicValue? value)
        => value is null ? new(StringComparer.Ordinal) : new(JsonSerializer.Deserialize<string[]>(value.Value) ?? [], StringComparer.Ordinal);

    public async Task RunAsync<T>(SparkSubscriptionDefinition<T> definition, SparkSubscriptionRunOptions options,
        Func<SparkSubscriptionBatch<T>, CancellationToken, Task> handler, CancellationToken cancellationToken) where T : class
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(handler);
        await EnsureAsync(definition, cancellationToken);

        while (!cancellationToken.IsCancellationRequested)
        {
            ISparkLease? lease;
            try
            {
                lease = await Locks.TryAcquireAsync(RunnerKey(definition.Name), LeaseTimeToLive, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            if (lease is null)
            {
                await DelayAsync(StandbyInterval, null, cancellationToken);
                continue;
            }

            await using (lease)
            {
                using var held = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lease.Lost);
                try
                {
                    await RunHeldAsync(definition, options, handler, held.Token);
                }
                catch (OperationCanceledException) when (held.IsCancellationRequested)
                {
                    // Stopped, or the lease was lost: the loop ends or waits for the lease again.
                }
            }
        }
    }

    private async Task RunHeldAsync<T>(SparkSubscriptionDefinition<T> definition, SparkSubscriptionRunOptions options,
        Func<SparkSubscriptionBatch<T>, CancellationToken, Task> handler, CancellationToken cancellationToken) where T : class
    {
        var take = Math.Max(1, options.MaxDocsPerBatch);
        var woken = new SemaphoreSlim(0, 1);
        using var wake = OnCommitted(() =>
        {
            try { if (woken.CurrentCount == 0) woken.Release(); }
            catch (SemaphoreFullException) { }
        });

        DateTimeOffset? failingSince = null;
        while (!cancellationToken.IsCancellationRequested)
        {
            int delivered;
            try
            {
                delivered = await PollOnceAsync(definition, take, handler, cancellationToken);
                failingSince = null;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is not SparkSubscriptionException)
            {
                // At least once: nothing moved, the same rows come again after the delay.
                options.OnRetry?.Invoke(ex);
                failingSince ??= DateTimeOffset.UtcNow;
                if (DateTimeOffset.UtcNow - failingSince > options.MaxDownTime)
                    throw new SparkSubscriptionException($"Subscription '{definition.Name}' kept failing for longer than {options.MaxDownTime}.", ex);
                await DelayAsync(options.RetryDelay, null, cancellationToken);
                continue;
            }

            if (delivered == LostCursor)
                return;

            // A full batch: there may be more right now. Otherwise wait for a commit or the next poll.
            if (delivered < take)
                await DelayAsync(PollInterval, woken, cancellationToken);
        }
    }

    private const int LostCursor = -1;

    /// <summary>One poll: one batch handled and the cursor moved. The number of documents delivered, or <see cref="LostCursor"/>.</summary>
    private async Task<int> PollOnceAsync<T>(SparkSubscriptionDefinition<T> definition, int take,
        Func<SparkSubscriptionBatch<T>, CancellationToken, Task> handler, CancellationToken cancellationToken) where T : class
    {
        var stored = await Values.GetAsync(CursorKey(definition.Name), cancellationToken);
        var cursor = stored is null ? 0UL : ulong.Parse(stored.Value, CultureInfo.InvariantCulture);
        var horizon = await ReadHorizonAsync(cancellationToken);
        if (horizon <= cursor + 1)
            return 0;

        var rows = await ReadAsync(definition.Filter, cursor, horizon, take + 1, cancellationToken);
        ulong next;
        IReadOnlyList<SparkStampedDocument<T>> batch;
        if (rows.Count <= take)
        {
            batch = rows;
            next = horizon - 1;
        }
        else
        {
            // Never split a stamp: stop before the last one, or take all of it when it fills the batch alone.
            var last = rows[take - 1].Stamp;
            batch = [.. rows.Take(take).Where(r => r.Stamp < last)];
            if (batch.Count > 0)
                next = batch[^1].Stamp;
            else
            {
                batch = await ReadAsync(definition.Filter, last - 1, last + 1, 0, cancellationToken);
                next = last;
            }
        }

        if (batch.Count > 0)
            await handler(new SparkSubscriptionBatch<T>([.. batch.Select(r => new SparkSubscriptionItem<T>(r.Id, r.Document, r.Version))]), cancellationToken);

        // Another runner moved the cursor: this one no longer holds the subscription.
        if (next > cursor
            && await Values.TryPutAsync(CursorKey(definition.Name), next.ToString(CultureInfo.InvariantCulture), stored?.Version ?? 0, cancellationToken) is null)
            return LostCursor;
        return batch.Count;
    }

    private static async Task DelayAsync(TimeSpan delay, SemaphoreSlim? woken, CancellationToken cancellationToken)
    {
        try
        {
            if (woken is null)
                await Task.Delay(delay, cancellationToken);
            else
                await woken.WaitAsync(delay, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }
}
