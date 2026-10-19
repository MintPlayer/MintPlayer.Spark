using Raven.Client.Documents.Operations.CompareExchange;
using Raven.Client.Documents.Session;
using Raven.Client.Exceptions;

namespace MintPlayer.Spark.Claims;

/// <summary>
/// <see cref="ISparkClaimsUnitOfWork"/> over one RavenDB cluster-wide session. Claims are read through the session's
/// cluster transaction, so the commit compares every key against the index read here: a key changed meanwhile fails
/// the whole commit, documents included.
/// </summary>
internal sealed class SparkClaimsUnitOfWork(IAsyncDocumentSession session, TimeProvider timeProvider) : ISparkClaimsUnitOfWork
{
    public IAsyncDocumentSession Session => session;

    public async Task<SparkClaimResult<TValue>> ReserveAsync<TValue>(
        SparkClaimKind<TValue> kind, SparkClaimKey key, string owner, Func<SparkClaim<TValue>?, TValue?> create,
        DateTime? nowUtc = null, CancellationToken cancellationToken = default)
        where TValue : class
    {
        ArgumentNullException.ThrowIfNull(owner);
        var tracked = await ReadAsync(kind, key, cancellationToken);
        var current = AsClaim(key, tracked);
        if (await SparkClaims.JudgeAsync(kind, current, owner, nowUtc ?? timeProvider.GetUtcNow().UtcDateTime, cancellationToken) is { } refusal)
            return refusal;

        var value = create(current);
        if (value is null)
            return new(SparkClaimStatus.Declined, current);

        // A tracked value is written back at commit, compared against the index read; a new key is created at
        // commit and fails it if another writer created the key first.
        if (tracked is null)
            session.Advanced.ClusterTransaction.CreateCompareExchangeValue(key.Value, value);
        else
            tracked.Value = value;
        return new(SparkClaimStatus.Reserved, new SparkClaim<TValue>(key, value, tracked?.Index ?? 0));
    }

    public async Task<SparkClaimResult<TValue>> UpdateAsync<TValue>(
        SparkClaimKind<TValue> kind, SparkClaimKey key, string owner, Func<TValue, TValue?> change, CancellationToken cancellationToken = default)
        where TValue : class
    {
        var tracked = await ReadAsync(kind, key, cancellationToken);
        var current = AsClaim(key, tracked);
        if (tracked is null || current is null || !kind.IsOwnedBy(current.Value, owner))
            return new(SparkClaimStatus.NotYours, current);

        var next = change(current.Value);
        if (next is null)
            return new(SparkClaimStatus.Declined, current);

        tracked.Value = next;
        return new(SparkClaimStatus.Updated, new SparkClaim<TValue>(key, next, tracked.Index));
    }

    public async Task<SparkClaimResult<TValue>> ReleaseAsync<TValue>(
        SparkClaimKind<TValue> kind, SparkClaimKey key, string owner, CancellationToken cancellationToken = default)
        where TValue : class
    {
        var tracked = await ReadAsync(kind, key, cancellationToken);
        var current = AsClaim(key, tracked);
        if (tracked is null || current is null || !kind.IsOwnedBy(current.Value, owner))
            return new(SparkClaimStatus.NotYours, current);

        session.Advanced.ClusterTransaction.DeleteCompareExchangeValue(key.Value, tracked.Index);
        return new(SparkClaimStatus.Released, current);
    }

    public async Task<bool> CommitAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await session.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (ConcurrencyException)
        {
            // A compare-exchange key or a guarded document changed since this unit of work read it.
            return false;
        }
    }

    public void Dispose() => session.Dispose();

    private async Task<CompareExchangeValue<TValue>?> ReadAsync<TValue>(SparkClaimKind<TValue> kind, SparkClaimKey key, CancellationToken cancellationToken)
        where TValue : class
    {
        SparkClaims.EnsureKind(kind, key);
        return await session.Advanced.ClusterTransaction.GetCompareExchangeValueAsync<TValue>(key.Value, cancellationToken);
    }

    private static SparkClaim<TValue>? AsClaim<TValue>(SparkClaimKey key, CompareExchangeValue<TValue>? tracked) where TValue : class
        => tracked?.Value is { } value ? new SparkClaim<TValue>(key, value, tracked.Index) : null;
}
