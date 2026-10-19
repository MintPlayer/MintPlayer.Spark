using Microsoft.Extensions.Options;
using MintPlayer.Spark.Commerce.Checkout.Models;
using MintPlayer.Spark.Commerce.Invoicing.Models;
using MintPlayer.Spark.Commerce.Invoicing.Vat;
using MintPlayer.Spark.Commerce.Models;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Commerce.Invoicing.Services;

/// <summary>An issuer as invoices print it, with what the VAT rules need about it.</summary>
internal sealed record InvoiceIssuer(
    string Key,
    string? OrganizationId,
    InvoiceParty Party,
    string Country,
    bool InEu,
    bool DestinationTaxationOption,
    bool OssRegistered,
    string? IossNumber);

/// <summary>
/// Who issues (§4.8): the seller in <c>ConnectedSellers</c> mode, the platform in <c>Platform</c> mode, and the platform
/// for D41's mixed-seller orders (it is the merchant of record then). The platform also issues the fee invoices.
/// A seller's identity comes from its trader data (Commerce's <c>SellerProfile</c>, GA5) and its
/// <see cref="SellerInvoicingProfile"/>.
/// </summary>
internal sealed class InvoiceIssuers(
    IDocumentStore store,
    IOptionsMonitor<SparkCommerceInvoicingOptions> options,
    IOptionsMonitor<SparkCommerceOptions> commerce)
{
    public const string PlatformKey = "platform";

    /// <summary>The issuer key of an order line.</summary>
    public string KeyFor(CheckoutOrder order, CheckoutOrderLine line)
        => commerce.CurrentValue.Merchant == MerchantMode.ConnectedSellers && !order.MixedSellers && line.SellerOrganizationId is { Length: > 0 } seller
            ? seller
            : PlatformKey;

    public InvoiceIssuer Platform()
    {
        var p = options.CurrentValue.Platform;
        var country = (p.Country is { Length: 2 } c ? c : commerce.CurrentValue.Country).ToUpperInvariant();
        return new InvoiceIssuer(PlatformKey, null, new InvoiceParty
        {
            Name = p.Name ?? "",
            VatId = p.VatId,
            CompanyId = p.CompanyId,
            Country = country,
            AddressLines = [.. p.AddressLines],
            PostalCode = p.PostalCode,
            City = p.City,
            Email = p.Email,
            Iban = p.Iban,
            PeppolId = p.PeppolId,
        }, country, EuMemberStates.ContainsForGoods(country), false, p.OssRegistered, options.CurrentValue.IossNumber);
    }

    public async Task<InvoiceIssuer> GetAsync(string key, CancellationToken cancellationToken)
    {
        if (key == PlatformKey)
            return Platform();
        using var session = store.OpenAsyncSession();
        var seller = await session.LoadAsync<SellerProfile>(SellerProfile.IdFor(key), cancellationToken);
        var profile = await session.LoadAsync<SellerInvoicingProfile>(SellerInvoicingProfile.IdFor(key), cancellationToken);
        var trader = seller?.Trader;
        var country = (profile?.Country ?? trader?.Country ?? commerce.CurrentValue.Country).ToUpperInvariant();
        var address = SplitAddress(trader?.Address);
        return new InvoiceIssuer(key, key, new InvoiceParty
        {
            Name = trader?.Name ?? key,
            VatId = trader?.VatId ?? seller?.Dac7.VatId,
            CompanyId = profile?.CompanyId ?? trader?.RegistrationNumber,
            Country = country,
            AddressLines = address,
            Email = trader?.Email,
            Iban = profile?.Iban,
            PeppolId = profile?.PeppolId,
        }, country, EuMemberStates.ContainsForGoods(country), profile?.DestinationTaxationOption ?? false, profile?.OssRegistered ?? false, profile?.IossNumber);
    }

    /// <summary>The trader data keeps the address as one text; invoices print it line by line.</summary>
    private static List<string> SplitAddress(string? address)
        => address is null ? [] : address.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
}
