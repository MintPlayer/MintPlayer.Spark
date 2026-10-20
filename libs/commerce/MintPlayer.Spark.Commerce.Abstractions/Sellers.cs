using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.Commerce;

/// <summary>
/// The sellers of a commerce host (D21, §4.19): which organizations may sell, and why not. A seller is an
/// Organizations organization with a seller profile. Checkout asks <see cref="CanSellAsync"/> before a sale opens and
/// before an order is placed; Payments asks <see cref="CanReceivePayoutsAsync"/> before a payout. The DAC7 block (D43)
/// closes selling only: it is a <see cref="CanSellAsync"/> reason, never a payout one.
/// </summary>
public interface ISparkSellers
{
    /// <summary>The standing of <paramref name="organizationId"/>, or null when it has no seller profile.</summary>
    Task<SellerStanding?> GetStandingAsync(string organizationId, CancellationToken cancellationToken = default);

    /// <summary>Whether the organization may sell in <paramref name="channel"/> now: approved (D21), KYC verified in
    /// connected mode, trader data verified (GA5), sales not closed for missing DAC7 data after the reminders
    /// (<c>commerce.seller.dac7Blocked</c>, GA6/D43), and the channel's required legal documents published (GA2). In
    /// <see cref="MerchantMode.Platform"/> mode with no seller (<paramref name="organizationId"/> null) it is the
    /// platform's own legal documents that count.</summary>
    Task<SellerGate> CanSellAsync(string? organizationId, string channel, CancellationToken cancellationToken = default);

    /// <summary>Whether payouts to the organization may be released: KYC verified and not suspended. The DAC7 block
    /// does not apply here: it closes selling instead, and provider payouts are not touched (D43).</summary>
    Task<SellerGate> CanReceivePayoutsAsync(string organizationId, CancellationToken cancellationToken = default);
}

/// <summary>A seller's state as Commerce sees it (D21, GA5, GA6).</summary>
/// <param name="OrganizationId">The seller organization.</param>
/// <param name="Approved">Registration approved (or not needed).</param>
/// <param name="Suspended">Suspended by the platform (a statement of reasons was sent).</param>
/// <param name="Kyc">The provider's verification state (connected mode).</param>
/// <param name="TraderStatus">Trader or not, shown on every listing.</param>
/// <param name="TraderVerified">The best-effort trader verification (DSA Art. 30(2)) passed.</param>
/// <param name="Dac7Complete">All DAC7 data is present.</param>
/// <param name="SalesBlocked">Sales are closed for DAC7 data still missing after the reminders (D43).</param>
public sealed record SellerStanding(
    string OrganizationId,
    bool Approved,
    bool Suspended,
    KycStatus Kyc,
    TraderStatus TraderStatus,
    bool TraderVerified,
    bool Dac7Complete,
    bool SalesBlocked);

/// <summary>The answer of a seller gate: allowed, or the reasons (translation keys) why not.</summary>
/// <param name="Allowed">Whether the action may go ahead.</param>
/// <param name="Reasons">Translation keys of the missing conditions (<c>commerce.seller.notApproved</c>, …).</param>
public sealed record SellerGate(bool Allowed, IReadOnlyList<string> Reasons)
{
    /// <summary>An allowed gate.</summary>
    public static SellerGate Open { get; } = new(true, []);

    /// <summary>A refused gate with <paramref name="reasons"/>.</summary>
    public static SellerGate Refused(params string[] reasons) => new(false, reasons);
}

/// <summary>
/// The KYC gate hook (D21): reports the provider's verification of a connected seller. Implemented by the payments
/// package (one per provider adapter, or one aggregate); Commerce takes the strongest answer. Without any source, a
/// <see cref="MerchantMode.ConnectedSellers"/> host has no verified seller and nobody can sell — startup warns.
/// </summary>
[SparkDiscoverable]
public interface ISellerKycSource
{
    /// <summary>The provider name, for messages (<c>stripe</c>).</summary>
    string Provider { get; }

    /// <summary>The verification state of <paramref name="organizationId"/>'s linked account at this provider.</summary>
    Task<KycStatus> GetKycStatusAsync(string organizationId, CancellationToken cancellationToken = default);
}

/// <summary>
/// A payment provider's capabilities Commerce validates at startup. Registered by each payments adapter
/// (<c>TryAddEnumerable</c>). D41: <c>Basket:MixSellers</c> is refused unless the host opted in
/// (<c>Basket:AcknowledgeSplitMerchantOfRecord</c>) and at least one registered provider can split.
/// </summary>
public interface ICommercePaymentCapabilities
{
    /// <summary>The provider name (<c>mollie</c>, <c>stripe</c>, <c>test</c>).</summary>
    string Provider { get; }

    /// <summary>Whether one payment can be split across several connected sellers (Stripe separate charges and
    /// transfers, Mollie split payments).</summary>
    bool SupportsSplit { get; }

    /// <summary>Whether the provider needs a consent purpose before its scripts load in the browser (GA9); null when none.</summary>
    string? RequiresConsentPurpose => null;
}

/// <summary>
/// A source of country-keyed buyer rules that other packages own (risk signals, payment-method rules, shipping rules).
/// Commerce validates every declared rule at startup against the Geo-blocking Regulation (EU) 2018/302 (GA11). The
/// check validates configuration; it does not judge custom code.
/// </summary>
public interface ICountryKeyedRuleSource
{
    /// <summary>The rules this package applies, as configured.</summary>
    IEnumerable<CountryKeyedRule> GetRules();
}

/// <summary>One country-keyed rule, as declared by an <see cref="ICountryKeyedRuleSource"/>.</summary>
/// <param name="Source">Where it is configured (<c>Spark:Challenges:RiskSignals:country</c>), for the message.</param>
/// <param name="Key">The buyer attribute it reads.</param>
/// <param name="Effect">What it does.</param>
/// <param name="Countries">The countries it matches.</param>
public sealed record CountryKeyedRule(string Source, CountryKey Key, CountryRuleEffect Effect, IReadOnlyList<string> Countries);
