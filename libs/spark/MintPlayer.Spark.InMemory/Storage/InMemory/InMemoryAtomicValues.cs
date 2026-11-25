using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Storage;

namespace MintPlayer.Spark.Storage.InMemory;

/// <summary>The in-memory store's <see cref="ISparkAtomicValues"/> (#536 S15): a dictionary under one lock, for this process only.</summary>
[Register(typeof(ISparkAtomicValues), ServiceLifetime.Singleton)]
internal sealed class InMemoryAtomicValues : ISparkAtomicValues
{
    private readonly Dictionary<string, SparkAtomicValue> values = new(StringComparer.Ordinal);
    private readonly Lock gate = new();
    private long version;

    public Task<SparkAtomicValue?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        lock (gate)
            return Task.FromResult(values.GetValueOrDefault(key));
    }

    public Task<SparkAtomicValue?> TryPutAsync(string key, string value, long expectedVersion, CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            if ((values.TryGetValue(key, out var current) ? current.Version : 0) != expectedVersion)
                return Task.FromResult<SparkAtomicValue?>(null);

            var stored = new SparkAtomicValue(key, value, ++version);
            values[key] = stored;
            return Task.FromResult<SparkAtomicValue?>(stored);
        }
    }

    public Task<bool> TryDeleteAsync(string key, long expectedVersion, CancellationToken cancellationToken = default)
    {
        lock (gate)
            return Task.FromResult(values.TryGetValue(key, out var current) && current.Version == expectedVersion && values.Remove(key));
    }
}
