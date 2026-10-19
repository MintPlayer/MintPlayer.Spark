using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Commerce.Models;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Commerce.Services;

/// <summary>
/// <see cref="ISparkSellers"/>: the gates Checkout and Payments ask (D21, GA2, GA5, GA6).
/// </summary>
internal sealed class SparkSellers(
    IDocumentStore store,
    IOptions<SparkCommerceOptions> options,
    ISparkLegalDocuments legalDocuments,
    IServiceProvider services) : ISparkSellers
{
    public async Task<SellerStanding?> GetStandingAsync(string organizationId, CancellationToken cancellationToken = default)
    {
        using var session = store.OpenAsyncSession();
        var profile = await session.LoadAsync<SellerProfile>(SellerProfile.IdFor(organizationId), cancellationToken);
        if (profile is null) return null;
        var kyc = await GetKycAsync(organizationId, profile.Kyc, cancellationToken);
        return Standing(profile, kyc, options.Value);
    }

    public async Task<SellerGate> CanSellAsync(string? organizationId, string channel, CancellationToken cancellationToken = default)
    {
        var o = options.Value;
        var channelOptions = o.GetChannel(channel);
        var reasons = new List<string>();

        if (organizationId is not null)
        {
            var standing = await GetStandingAsync(organizationId, cancellationToken);
            if (standing is null)
                return SellerGate.Refused("commerce.seller.notRegistered");
            reasons.AddRange(SellReasons(standing, o));
        }
        else if (o.Merchant == MerchantMode.ConnectedSellers)
        {
            reasons.Add("commerce.seller.required");
        }

        // GA2: the sale cannot open while a required legal document is missing.
        foreach (var kind in await legalDocuments.GetMissingAsync(organizationId, channelOptions.RequiredLegalDocuments, cancellationToken))
            reasons.Add($"commerce.legal.missing.{kind}");

        return reasons.Count == 0 ? SellerGate.Open : new SellerGate(false, reasons);
    }

    public async Task<SellerGate> CanReceivePayoutsAsync(string organizationId, CancellationToken cancellationToken = default)
    {
        var standing = await GetStandingAsync(organizationId, cancellationToken);
        if (standing is null) return SellerGate.Refused("commerce.seller.notRegistered");
        return PayoutReasons(standing) is { Count: > 0 } reasons ? new SellerGate(false, reasons) : SellerGate.Open;
    }

    /// <summary>Pure: why payouts to a seller with <paramref name="standing"/> may not be released. The DAC7 block is
    /// not one of them: it closes selling, and provider payouts are not touched (D43).</summary>
    internal static IReadOnlyList<string> PayoutReasons(SellerStanding standing)
    {
        var reasons = new List<string>();
        if (standing.Kyc != KycStatus.Verified) reasons.Add("commerce.seller.kycNotVerified");
        if (standing.Suspended) reasons.Add("commerce.seller.suspended");
        return reasons;
    }

    /// <summary>Pure: why a seller with <paramref name="standing"/> may not sell. Empty when it may.</summary>
    internal static IReadOnlyList<string> SellReasons(SellerStanding standing, SparkCommerceOptions options)
    {
        var reasons = new List<string>();
        if (!standing.Approved) reasons.Add("commerce.seller.notApproved");
        if (standing.Suspended) reasons.Add("commerce.seller.suspended");
        if (standing.TraderStatus == TraderStatus.Undeclared) reasons.Add("commerce.seller.traderStatusUndeclared");
        if (options.Sellers.TraderTraceability && standing.TraderStatus == TraderStatus.Trader && !standing.TraderVerified)
            reasons.Add("commerce.seller.traderNotVerified");
        if (options.Merchant == MerchantMode.ConnectedSellers && standing.Kyc != KycStatus.Verified)
            reasons.Add("commerce.seller.kycNotVerified");
        // D43: DAC7 data still missing after the reminders closes the seller's sales until it is complete.
        if (standing.SalesBlocked) reasons.Add("commerce.seller.dac7Blocked");
        return reasons;
    }

    internal static SellerStanding Standing(SellerProfile profile, KycStatus kyc, SparkCommerceOptions options)
        => new(
            profile.OrganizationId,
            profile.Status == SellerRegistrationStatus.Approved,
            profile.Status == SellerRegistrationStatus.Suspended,
            kyc,
            profile.TraderStatus,
            profile.Verification?.Passed == true,
            profile.Dac7.Missing().Count == 0,
            profile.SalesBlocked);

    /// <summary>The strongest answer of the registered KYC sources (Verified beats Pending beats the rest); the cached
    /// value when no source is registered.</summary>
    private async Task<KycStatus> GetKycAsync(string organizationId, KycStatus cached, CancellationToken ct)
    {
        using var scope = services.CreateScope();
        var sources = scope.ServiceProvider.GetServices<ISellerKycSource>().ToArray();
        if (sources.Length == 0) return cached;
        var best = KycStatus.NotStarted;
        foreach (var source in sources)
        {
            var status = await source.GetKycStatusAsync(organizationId, ct);
            if (Rank(status) > Rank(best)) best = status;
        }
        return best;
    }

    private static int Rank(KycStatus status) => status switch
    {
        KycStatus.Verified => 3,
        KycStatus.Pending => 2,
        KycStatus.Rejected => 1,
        _ => 0,
    };
}
