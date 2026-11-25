using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Storage;
using Raven.Client.Documents;
using Raven.Client.Documents.Operations.CompareExchange;

namespace MintPlayer.Spark.Storage.Raven;

/// <summary>
/// RavenDB's <see cref="ISparkAtomicValues"/> (#536 S15): cluster-wide compare-exchange values, whose index is the
/// version. Index 0 is RavenDB's own "only if absent".
/// </summary>
[Register(typeof(ISparkAtomicValues), ServiceLifetime.Singleton)]
internal sealed partial class RavenAtomicValues : ISparkAtomicValues
{
    [Inject] private readonly IDocumentStore store;

    public async Task<SparkAtomicValue?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        var current = await store.Operations.SendAsync(new GetCompareExchangeValueOperation<string>(key), token: cancellationToken);
        return current is null ? null : new SparkAtomicValue(key, current.Value ?? string.Empty, current.Index);
    }

    public async Task<SparkAtomicValue?> TryPutAsync(string key, string value, long expectedVersion, CancellationToken cancellationToken = default)
    {
        var result = await store.Operations.SendAsync(
            new PutCompareExchangeValueOperation<string>(key, value, expectedVersion), token: cancellationToken);
        return result.Successful ? new SparkAtomicValue(key, value, result.Index) : null;
    }

    public async Task<bool> TryDeleteAsync(string key, long expectedVersion, CancellationToken cancellationToken = default)
    {
        var result = await store.Operations.SendAsync(
            new DeleteCompareExchangeValueOperation<string>(key, expectedVersion), token: cancellationToken);
        return result.Successful;
    }
}
