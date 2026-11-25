namespace MintPlayer.Spark.Abstractions.Storage;

/// <summary>
/// A lock across every instance that shares the database (#536 S6/S15, PRD §4.1): a lease on
/// <see cref="ISparkAtomicValues"/> that expires unless its holder renews it. Core implements it once over the atomic
/// values, so a store provider implements only those. A holder that is killed stops renewing, and its lease is free
/// again after <c>timeToLive</c>; a live holder keeps it however long its work takes.
/// </summary>
/// <remarks>
/// It is not a database session lock: those are released when the connection drops and fight connection pooling (PRD
/// §4.1, rejected alternatives).
/// </remarks>
public interface ISparkStoreLock
{
    /// <summary>
    /// Takes the lease at <paramref name="key"/> when it is free or its holder stopped renewing it; <see langword="null"/>
    /// while somebody else holds it. The lease renews itself every third of <paramref name="timeToLive"/> until it is
    /// disposed, which releases it.
    /// </summary>
    Task<ISparkLease?> TryAcquireAsync(string key, TimeSpan timeToLive, CancellationToken cancellationToken = default);
}

/// <summary>A held lease. Dispose it to release it.</summary>
public interface ISparkLease : IAsyncDisposable
{
    /// <summary>The key the lease is held at.</summary>
    string Key { get; }

    /// <summary>
    /// Cancelled when the lease was lost: a renewal found another holder, or could not reach the store before the lease
    /// expired. Work done under the lease passes it on, so it stops rather than run unguarded.
    /// </summary>
    CancellationToken Lost { get; }
}
