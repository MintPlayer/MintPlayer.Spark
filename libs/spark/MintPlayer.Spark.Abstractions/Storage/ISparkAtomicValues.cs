namespace MintPlayer.Spark.Abstractions.Storage;

/// <summary>A value in <see cref="ISparkAtomicValues"/>, with the version a compare-and-swap must name to replace it.</summary>
public sealed record SparkAtomicValue(string Key, string Value, long Version);

/// <summary>
/// The store's neutral compare-exchange (#536 S15, PRD §4.1): small values written atomically across every instance that
/// shares the database. Each value has a version that changes on every write, so a write or delete that names the version
/// it read succeeds only if nobody wrote in between. Every store provider implements it (RavenDB: compare-exchange values;
/// relational: an <c>AtomicValues</c> table; in-memory: a dictionary).
/// </summary>
/// <remarks>
/// It is for coordination, not data: a cron job's latest claimed occurrence, a lease (<see cref="ISparkStoreLock"/>).
/// A key is the caller's own namespace, such as <c>cron/{job}</c>; values are plain text the caller formats.
/// </remarks>
public interface ISparkAtomicValues
{
    /// <summary>The value at <paramref name="key"/>, or <see langword="null"/> when there is none.</summary>
    Task<SparkAtomicValue?> GetAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes <paramref name="value"/> if the stored version is still <paramref name="expectedVersion"/>; <c>0</c> means
    /// "only if there is no value yet". Returns the stored value with its new version, or <see langword="null"/> when
    /// another writer got there first.
    /// </summary>
    Task<SparkAtomicValue?> TryPutAsync(string key, string value, long expectedVersion, CancellationToken cancellationToken = default);

    /// <summary>Deletes the value if its version is still <paramref name="expectedVersion"/>; <see langword="false"/> otherwise.</summary>
    Task<bool> TryDeleteAsync(string key, long expectedVersion, CancellationToken cancellationToken = default);
}
