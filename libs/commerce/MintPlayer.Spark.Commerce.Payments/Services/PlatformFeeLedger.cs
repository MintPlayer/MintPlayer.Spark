using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Commerce.Payments.Models;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Commerce.Payments.Services;

/// <summary>
/// Books platform-fee ledger entries (GB4) inside the caller's session. Each entry has a natural id from its kind,
/// provider and source, so booking the same fact twice (a redelivered webhook, a re-run import) stores it once.
/// </summary>
internal sealed class PlatformFeeLedger(TimeProvider time)
{
    /// <summary>Books an entry unless one with the same id exists. Returns whether it was booked now.</summary>
    public async Task<bool> BookAsync(
        IAsyncDocumentSession session,
        PlatformFeeEntryKind kind,
        string provider,
        string sourceId,
        Money amount,
        string? sellerOrganizationId,
        string? reference,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        if (amount.IsZero)
            return false;
        var id = PlatformFeeEntry.IdFor(kind, provider, sourceId);
        if (await session.LoadAsync<PlatformFeeEntry>(id, cancellationToken) is not null)
            return false;

        var entry = new PlatformFeeEntry
        {
            Id = id,
            Kind = kind,
            Provider = provider,
            SourceId = sourceId,
            Amount = amount.ToMinorUnits() < 0 ? Money.FromMinorUnits(-amount.ToMinorUnits(), amount.Currency) : amount,
            SellerOrganizationId = sellerOrganizationId,
            Reference = reference,
            OccurredAtUtc = occurredAt.UtcDateTime,
            RecordedAtUtc = time.GetUtcNow().UtcDateTime,
        };
        // An empty change vector means "must not exist yet": a concurrent writer that booked it first makes this save
        // fail with a ConcurrencyException (the webhook retry then finds it booked), never overwrite it.
        await session.StoreAsync(entry, changeVector: string.Empty, id, cancellationToken);
        return true;
    }
}
