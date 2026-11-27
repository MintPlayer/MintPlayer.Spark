using System.Linq.Expressions;

namespace MintPlayer.Spark.Abstractions.Storage;

/// <summary>
/// Durable subscriptions (#536 D39, S7/S14): each document of <typeparamref name="T"/> that matches the filter, delivered
/// in commit order as its <b>current state</b>, the backlog included, at least once. It is the
/// <see cref="SparkStoreCapabilities.Subscriptions"/> capability.
/// </summary>
/// <typeparam name="T">The stored type (its collection).</typeparam>
/// <param name="Name">The subscription's name: its cursor and its exclusivity are per name.</param>
/// <param name="Filter">
/// Which documents are delivered; null for all. A pure predicate over the document's fields: never the clock
/// (<c>DateTime.UtcNow</c>), because RavenDB evaluates it once per change, not per read.
/// </param>
public sealed record SparkSubscriptionDefinition<T>(string Name, Expression<Func<T, bool>>? Filter = null) where T : class;

/// <summary>How <see cref="ISparkSubscriptions.RunAsync{T}"/> runs a subscription.</summary>
public sealed record SparkSubscriptionRunOptions
{
    /// <summary>At most this many documents per batch. Default 256.</summary>
    public int MaxDocsPerBatch { get; init; } = 256;

    /// <summary>How long a connection may fail before the run gives up with <see cref="SparkSubscriptionException"/>. Default 5 minutes.</summary>
    public TimeSpan MaxDownTime { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>The wait between reconnection attempts. Default 5 seconds.</summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Told of every failure the run retries (a handler that threw, a lost connection), for logging.</summary>
    public Action<Exception>? OnRetry { get; init; }
}

/// <summary>One document a subscription delivers.</summary>
/// <param name="Id">The document id.</param>
/// <param name="Document">The document as it was when the batch was read.</param>
/// <param name="Version">Its version then, for an optimistic write (<see cref="ISparkSession.StoreAsync(object, string?, string, CancellationToken)"/>).</param>
public readonly record struct SparkSubscriptionItem<T>(string Id, T Document, string? Version);

/// <summary>One batch: acknowledged when the handler returns, delivered again when it throws.</summary>
public sealed class SparkSubscriptionBatch<T>(IReadOnlyList<SparkSubscriptionItem<T>> items)
{
    /// <summary>The documents, in commit order.</summary>
    public IReadOnlyList<SparkSubscriptionItem<T>> Items { get; } = items;
}

/// <summary>A subscription cannot run and retrying will not help: removed, refused by the licence, or failing past its down time.</summary>
public sealed class SparkSubscriptionException(string message, Exception? innerException = null) : Exception(message, innerException);

/// <summary>
/// The store's subscriptions (#536 D39). RavenDB runs its own data subscriptions; InMemory and the relational providers
/// keep a cursor over their commit order (D6's stamp below the safe horizon) in <see cref="ISparkAtomicValues"/>.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Exclusive:</b> one runner per name at a time. A second waits, without error, and takes over when the first
/// stops or dies (RavenDB's <c>WaitForFree</c>; elsewhere an <see cref="ISparkStoreLock"/> lease).</item>
/// <item><b>At least once:</b> a batch is acknowledged when the handler returns. A handler that throws, or a runner that
/// dies, has the batch delivered again, so a handler checks the state it acts on (a claim with an expected version).</item>
/// <item><b>Current state:</b> a document changed twice before its delivery is delivered once, as it is now; one that
/// stopped matching the filter is not delivered.</item>
/// </list>
/// </remarks>
public interface ISparkSubscriptions
{
    /// <summary>Creates the subscription, or updates its filter. Throws <see cref="SparkSubscriptionException"/> when the store refuses it.</summary>
    Task EnsureAsync<T>(SparkSubscriptionDefinition<T> definition, CancellationToken cancellationToken = default) where T : class;

    /// <summary>
    /// Runs the subscription until <paramref name="cancellationToken"/> is cancelled: waits until no other runner holds it,
    /// then hands each batch to <paramref name="handler"/>. Ensures it first. Returns on cancellation; throws
    /// <see cref="SparkSubscriptionException"/> when it cannot go on.
    /// </summary>
    Task RunAsync<T>(SparkSubscriptionDefinition<T> definition, SparkSubscriptionRunOptions options,
        Func<SparkSubscriptionBatch<T>, CancellationToken, Task> handler, CancellationToken cancellationToken) where T : class;

    /// <summary>The names of the store's subscriptions.</summary>
    Task<IReadOnlyList<string>> GetNamesAsync(CancellationToken cancellationToken = default);

    /// <summary>Deletes a subscription and its cursor; a missing one is no error.</summary>
    Task DeleteAsync(string name, CancellationToken cancellationToken = default);
}
