using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Commerce.Payments.Models;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Commerce.Payments.Services;

/// <summary>
/// Books platform-fee ledger entries (GB4) inside the caller's session. Each entry has a natural id from its kind,
/// provider (or booker) and source, so booking the same fact twice (a redelivered webhook, a re-run import, a
/// redelivered order event) stores it once. The provider kinds are booked by this package through
/// <see cref="BookAsync"/>; commissions by other packages through <see cref="ISparkPlatformFeeLedger"/>.
/// </summary>
internal sealed class PlatformFeeLedger(TimeProvider time) : ISparkPlatformFeeLedger
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
        => await BookCoreAsync(session, kind, provider, sourceId, amount, sellerOrganizationId, reference, null, null, occurredAt, cancellationToken) is not null;

    public async Task<PlatformFeeEntry?> BookCommissionAsync(IAsyncDocumentSession session, CommissionBooking booking, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(booking);
        RequireKey(booking.Booker, nameof(booking.Booker));
        RequireKey(booking.SourceId, nameof(booking.SourceId));
        if (booking.Amount.ToMinorUnits() < 0)
            throw new ArgumentException("A commission is booked as a positive amount; a reversal goes through ReverseCommissionAsync.", nameof(booking));
        return await BookCoreAsync(session, PlatformFeeEntryKind.Commission, booking.Booker, booking.SourceId, booking.Amount,
            booking.SellerOrganizationId, booking.Reference, booking.BeneficiaryId, booking.AttributionRef, booking.OccurredAt, cancellationToken);
    }

    public async Task<PlatformFeeEntry?> GetCommissionAsync(IAsyncDocumentSession session, string booker, string sourceId, CancellationToken cancellationToken = default)
    {
        RequireKey(booker, nameof(booker));
        RequireKey(sourceId, nameof(sourceId));
        return await session.LoadAsync<PlatformFeeEntry>(PlatformFeeEntry.IdFor(PlatformFeeEntryKind.Commission, booker, sourceId), cancellationToken);
    }

    public async Task<Money> ReverseCommissionAsync(IAsyncDocumentSession session, string booker, string sourceId, string reversalKey, Money reversedTotal, DateTimeOffset occurredAt, CancellationToken cancellationToken = default)
    {
        RequireKey(reversalKey, nameof(reversalKey));
        var commission = await GetCommissionAsync(session, booker, sourceId, cancellationToken);
        if (commission is null)
            return Money.Zero(reversedTotal.Currency);
        var zero = Money.Zero(commission.Amount.Currency);
        var reversalSource = ReversalSourceId(sourceId, reversalKey);
        if (await session.LoadAsync<PlatformFeeEntry>(PlatformFeeEntry.IdFor(PlatformFeeEntryKind.CommissionReversal, booker, reversalSource), cancellationToken) is not null)
            return zero;

        var earlier = (await session.Advanced.LoadStartingWithAsync<PlatformFeeEntry>(
                PlatformFeeEntry.IdFor(PlatformFeeEntryKind.CommissionReversal, booker, ReversalSourceId(sourceId, "")), pageSize: 1024, token: cancellationToken))
            .Aggregate(zero, (sum, r) => sum + r.Amount);
        // The target never exceeds the commission: a reversal can take back what was booked, not more.
        var target = reversedTotal > commission.Amount ? commission.Amount : reversedTotal;
        var delta = target - earlier;
        if (delta.ToMinorUnits() <= 0)
            return zero;

        await BookCoreAsync(session, PlatformFeeEntryKind.CommissionReversal, booker, reversalSource, delta,
            commission.SellerOrganizationId, commission.Reference, commission.BeneficiaryId, commission.AttributionRef, occurredAt, cancellationToken);
        return delta;
    }

    public async Task<IReadOnlyList<PlatformFeeEntry>> ListCommissionsAsync(IAsyncDocumentSession session, string attributionRef, CancellationToken cancellationToken = default)
    {
        RequireKey(attributionRef, nameof(attributionRef));
        var entries = new List<PlatformFeeEntry>();
        var skip = 0;
        while (true)
        {
            // The ref is a query parameter (LINQ), never spliced into RQL.
            var page = await session.Query<PlatformFeeEntry>()
                .Where(e => e.AttributionRef == attributionRef
                    && (e.Kind == PlatformFeeEntryKind.Commission || e.Kind == PlatformFeeEntryKind.CommissionReversal))
                .Skip(skip).Take(1024)
                .ToListAsync(cancellationToken);
            entries.AddRange(page);
            if (page.Count < 1024)
                return entries;
            skip += page.Count;
        }
    }

    /// <summary>A reversal's source key: <c>{sourceId}/reversal/{reversalKey}</c> (the empty key gives the prefix of them all).</summary>
    internal static string ReversalSourceId(string sourceId, string reversalKey) => $"{sourceId}/reversal/{reversalKey}";

    private async Task<PlatformFeeEntry?> BookCoreAsync(
        IAsyncDocumentSession session,
        PlatformFeeEntryKind kind,
        string provider,
        string sourceId,
        Money amount,
        string? sellerOrganizationId,
        string? reference,
        string? beneficiaryId,
        string? attributionRef,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        if (amount.IsZero)
            return null;
        var id = PlatformFeeEntry.IdFor(kind, provider, sourceId);
        if (await session.LoadAsync<PlatformFeeEntry>(id, cancellationToken) is not null)
            return null;

        var entry = new PlatformFeeEntry
        {
            Id = id,
            Kind = kind,
            Provider = provider,
            SourceId = sourceId,
            Amount = amount.ToMinorUnits() < 0 ? Money.FromMinorUnits(-amount.ToMinorUnits(), amount.Currency) : amount,
            SellerOrganizationId = sellerOrganizationId,
            Reference = reference,
            BeneficiaryId = beneficiaryId,
            AttributionRef = attributionRef,
            OccurredAtUtc = occurredAt.UtcDateTime,
            RecordedAtUtc = time.GetUtcNow().UtcDateTime,
        };
        // An empty change vector means "must not exist yet": a concurrent writer that booked it first makes this save
        // fail with a ConcurrencyException (the retry then finds it booked), never overwrite it.
        await session.StoreAsync(entry, changeVector: string.Empty, id, cancellationToken);
        return entry;
    }

    private static void RequireKey(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("A ledger key cannot be empty.", name);
    }
}
