using MintPlayer.SourceGenerators.Attributes;
using Raven.Client.Documents;
using Raven.Client.Documents.Operations.CompareExchange;

namespace MintPlayer.Spark.Sequences;

/// <summary>
/// Cluster-wide counters over RavenDB compare-exchange (warehouse PRD WM2: the SSCC serial sequence): each call hands
/// out the next value of a named sequence, never the same value twice, even across processes and cluster nodes.
/// </summary>
/// <remarks>
/// A value is read with its compare-exchange index and written back as value + 1 under that index; a lost race reads
/// again. A number handed out and then not used (the caller crashed before storing it) is skipped, not reissued: a
/// sequence is unique, not gapless. A gapless series (invoice numbers) needs the number stored in the same
/// cluster-wide transaction as the document, which <c>ISparkClaims.BeginUnitOfWork</c> gives.
/// </remarks>
public interface ISparkSequences
{
    /// <summary>
    /// The next value of <paramref name="key"/>: <paramref name="firstValue"/> on the first call, then one more each
    /// call.
    /// </summary>
    /// <param name="key">The compare-exchange key, e.g. <c>gs1/sscc/09521234</c>. Shared by every caller of the sequence.</param>
    /// <exception cref="InvalidOperationException">The write lost its race more often than the retry budget allows.</exception>
    Task<long> NextAsync(string key, long firstValue = 0, CancellationToken cancellationToken = default);

    /// <summary>The last value handed out, or null when the sequence was never used.</summary>
    Task<long?> PeekAsync(string key, CancellationToken cancellationToken = default);
}

[Register(typeof(ISparkSequences), ServiceLifetime.Singleton)]
public sealed class SparkSequences : ISparkSequences
{
    // Every lost race means another caller got a value, so the budget only runs out under extreme contention.
    private const int MaxAttempts = 64;

    private readonly IDocumentStore documentStore;

    public SparkSequences(IDocumentStore documentStore)
    {
        this.documentStore = documentStore;
    }

    public async Task<long> NextAsync(string key, long firstValue = 0, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            var current = await documentStore.Operations.SendAsync(new GetCompareExchangeValueOperation<long>(key), token: cancellationToken);
            var next = current is null ? firstValue : checked(current.Value + 1);
            var result = await documentStore.Operations.SendAsync(
                new PutCompareExchangeValueOperation<long>(key, next, current?.Index ?? 0), token: cancellationToken);
            if (result.Successful)
                return next;
        }
        throw new InvalidOperationException($"The sequence '{key}' lost {MaxAttempts} races in a row; try again.");
    }

    public async Task<long?> PeekAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var current = await documentStore.Operations.SendAsync(new GetCompareExchangeValueOperation<long>(key), token: cancellationToken);
        return current?.Value;
    }
}
