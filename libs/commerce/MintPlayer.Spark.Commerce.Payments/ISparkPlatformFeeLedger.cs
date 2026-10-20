using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Commerce.Payments.Models;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Commerce.Payments;

/// <summary>
/// The public booking API of the platform-fee ledger (GB4) for the kinds other packages own: commissions and their
/// reversals (GC2, booked by Promotions' tracking links). The provider kinds (application fees, fee refunds, dispute
/// debits) stay with the payments package, which books them from the provider's own events.
/// <para>
/// Every call works in the caller's session, so a booking is saved with the caller's other writes (its audit entry) or
/// not at all. Entries have natural ids from the booker and its source key, so booking the same fact twice (a
/// redelivered order event) stores it once.
/// </para>
/// </summary>
public interface ISparkPlatformFeeLedger
{
    /// <summary>
    /// Books a commission unless the booker already booked one for <see cref="CommissionBooking.SourceId"/>. Returns
    /// the entry booked now, or null when it existed already or the amount is zero.
    /// </summary>
    /// <exception cref="ArgumentException">The amount is negative, or the booker or source key is empty.</exception>
    Task<PlatformFeeEntry?> BookCommissionAsync(IAsyncDocumentSession session, CommissionBooking booking, CancellationToken cancellationToken = default);

    /// <summary>The commission the booker booked for <paramref name="sourceId"/>, or null.</summary>
    Task<PlatformFeeEntry?> GetCommissionAsync(IAsyncDocumentSession session, string booker, string sourceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reverses the commission of <paramref name="sourceId"/> until <paramref name="reversedTotal"/> of it is reversed
    /// in all (capped at the commission), under <paramref name="reversalKey"/> (a refund, credit-note or dispute id).
    /// Returns the amount reversed now: zero when the key was booked before, there is no commission, or earlier
    /// reversals already reached the total.
    /// </summary>
    Task<Money> ReverseCommissionAsync(IAsyncDocumentSession session, string booker, string sourceId, string reversalKey, Money reversedTotal, DateTimeOffset occurredAt, CancellationToken cancellationToken = default);

    /// <summary>Every commission and reversal attributed to <paramref name="attributionRef"/> (a tracking link), for its report.</summary>
    Task<IReadOnlyList<PlatformFeeEntry>> ListCommissionsAsync(IAsyncDocumentSession session, string attributionRef, CancellationToken cancellationToken = default);
}

/// <summary>A commission to book (GC2).</summary>
public sealed record CommissionBooking
{
    /// <summary>The booking package's alias (<c>commerce-promotions</c>); part of the natural id.</summary>
    public required string Booker { get; init; }

    /// <summary>The booker's key of the fact (the order id: one commission per order); part of the natural id.</summary>
    public required string SourceId { get; init; }

    /// <summary>Positive.</summary>
    public required Money Amount { get; init; }

    /// <summary>The seller that owes it; null for a platform sale.</summary>
    public string? SellerOrganizationId { get; init; }

    /// <summary>The order reference.</summary>
    public string? Reference { get; init; }

    /// <summary>Who earns it (the promoter's user id).</summary>
    public string? BeneficiaryId { get; init; }

    /// <summary>What it is attributed to (the tracking link's <c>ref</c>).</summary>
    public string? AttributionRef { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }
}
