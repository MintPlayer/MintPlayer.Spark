using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Commerce.Checkout;
using MintPlayer.Spark.Commerce.Checkout.Refunds;
using MintPlayer.Spark.Commerce.Inventory;
using MintPlayer.Spark.Commerce.Shop.Models;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Commerce.Shop.Catalog;

/// <summary>A variant with its product, as the shop sells it.</summary>
public sealed record ShopSellable(ShopProduct Product, ShopVariant Variant)
{
    /// <summary>Loads <paramref name="variantId"/> and its product in <paramref name="session"/>; null when either is missing.</summary>
    public static async Task<ShopSellable?> LoadAsync(IAsyncDocumentSession session, string? variantId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(variantId))
            return null;
        var variant = await session.Include<ShopVariant>(v => v.ProductId).LoadAsync<ShopVariant>(variantId, cancellationToken);
        if (variant is null)
            return null;
        var product = await session.LoadAsync<ShopProduct>(variant.ProductId, cancellationToken);
        return product is null ? null : new ShopSellable(product, variant);
    }
}

/// <summary>
/// Product variants as checkout sells them (PRD §4.1 "capacity sources"): handles every <c>shop:</c> item. A variant
/// sells while its product is published, it is active, it has a price and its channel is configured. A variant that
/// tracks its inventory claims from its own quota (<see cref="ShopQuotas.Variant"/>); goods are physical (I4: a refund
/// after shipment returns no quota until restock). Registered by <c>AddCommerceShop()</c> (scoped, <c>TryAddEnumerable</c>).
/// </summary>
internal sealed class ShopCapacitySource(IDocumentStore store, IOptionsMonitor<SparkCommerceOptions> commerce) : ICheckoutCapacitySource
{
    public bool Handles(string itemId) => ShopItemIds.Handles(itemId);

    public async Task<CheckoutItem?> ResolveAsync(CheckoutItemRequest request, CancellationToken cancellationToken)
    {
        using var session = store.OpenAsyncSession();
        var sellable = await ShopSellable.LoadAsync(session, ShopItemIds.VariantId(request.ItemId), cancellationToken);
        return sellable is null ? null : Resolve(sellable, request, commerce.CurrentValue);
    }

    /// <summary>The checkout item of a loaded variant, or null when it is not on sale. Pure.</summary>
    internal static CheckoutItem? Resolve(ShopSellable sellable, CheckoutItemRequest request, SparkCommerceOptions commerce)
    {
        var (product, variant) = (sellable.Product, sellable.Variant);
        if (!product.IsPublished || !variant.IsActive || !commerce.Channels.ContainsKey(product.Channel))
            return null;
        if (ProductRules.PriceOf(product, variant) is not { } price || price.Amount < 0)
            return null;
        var characteristics = ProductRules.Characteristics(product, variant);
        return new CheckoutItem
        {
            ItemId = request.ItemId,
            Channel = product.Channel,
            OrganizationId = product.OrganizationId,
            Price = price,
            PriceBasis = PriceBasis.Gross,
            VatRateBasisPoints = product.VatRateBasisPoints,
            Title = ProductRules.Title(product, variant),
            Description = characteristics.Length == 0 ? product.Description : TranslatedString.Of(TranslatedString.DefaultLanguage, characteristics),
            MinimumAge = product.MinimumAge is > 0 ? product.MinimumAge : null,
            IsPhysicalGoods = product.Kind == EShopProductKind.Goods,
            Capacity = variant.TrackInventory
                ? new CheckoutCapacity { Quotas = [new QuotaLine(ShopQuotas.Variant(variant.Id!), Math.Max(0, request.Quantity))] }
                : new CheckoutCapacity(),
        };
    }
}

/// <summary>
/// The seller refund policy and withdrawal terms of a variant (D20, GA1), asked by Checkout when the order is placed.
/// The policy is the product's (Checkout clamps it to the channel's bounds); the withdrawal right is left to Checkout
/// (goods carry it) unless the product names a CRD Art. 16 exemption.
/// </summary>
internal sealed class ShopRefundTermsSource(IDocumentStore store) : IRefundTermsSource
{
    public bool Handles(string itemId) => ShopItemIds.Handles(itemId);

    public async Task<RefundTerms?> ResolveAsync(string itemId, string channel, CancellationToken cancellationToken)
    {
        using var session = store.OpenAsyncSession();
        var sellable = await ShopSellable.LoadAsync(session, ShopItemIds.VariantId(itemId), cancellationToken);
        return sellable is null ? null : Terms(sellable.Product);
    }

    internal static RefundTerms Terms(ShopProduct product)
    {
        var policy = product.RefundPolicy is { } kind ? new RefundPolicy(kind, product.RefundDaysBefore, product.RefundFeeBasisPoints) : null;
        return product.WithdrawalExemption is { Length: > 0 } exemption
            ? new RefundTerms(policy, WithdrawalRight: false, WithdrawalExemption: exemption)
            : new RefundTerms(policy);
    }
}
