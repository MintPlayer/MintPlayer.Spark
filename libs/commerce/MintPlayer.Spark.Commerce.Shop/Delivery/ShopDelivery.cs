using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Visitor;
using MintPlayer.Spark.Commerce.Checkout;
using MintPlayer.Spark.Commerce.Checkout.Contracts;
using MintPlayer.Spark.Commerce.Checkout.Models;
using MintPlayer.Spark.Commerce.Checkout.Refunds;
using MintPlayer.Spark.Commerce.Shipping;
using MintPlayer.Spark.Commerce.Shipping.Models;
using MintPlayer.Spark.Commerce.Shop.Catalog;
using MintPlayer.Spark.Commerce.Shop.Contracts;
using MintPlayer.Spark.Commerce.Shop.Models;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Commerce.Shop.Delivery;

/// <summary>The goods of a basket or order as Shipping prices them: per seller, the subtotal, the weight and the dangerous goods.</summary>
internal sealed record DeliveryParcel(string? Seller, Money Subtotal, int WeightGrams, IReadOnlyList<DangerousGoodsItem> DangerousGoods);

/// <summary>
/// The checkout delivery step (PRD §4.1: "the checkout shipping step lives in Shop"; Checkout never references Shipping):
/// the rates the basket can be sent with, the buyer's choice (address, method, service point, GB5) and its price as an
/// order extra. Shipping prices and checks every rate against the carrier catalog (dangerous goods, GC9; customs and
/// Incoterm, GB6; delivery promises, GC11).
/// </summary>
public interface ISparkShopDelivery
{
    /// <summary>The rates for the current buyer's basket to <paramref name="country"/>/<paramref name="postalCode"/>, and the current choice.</summary>
    Task<SparkShopDeliveryOptions> GetOptionsAsync(string country, string? postalCode, CancellationToken cancellationToken = default);

    /// <summary>Stores the buyer's choice and names it in the basket's choices (<c>shop.delivery</c>).</summary>
    /// <exception cref="ShopException">An invalid address, a method the basket is not offered, a missing or unknown service point.</exception>
    Task<SparkShopDeliveryChoice> ChooseAsync(SparkShopDeliveryRequest request, CancellationToken cancellationToken = default);
}

internal sealed class ShopDelivery(
    IDocumentStore store,
    ISparkCheckout checkout,
    ISparkShipping shipping,
    ISparkVisitor visitor,
    IOptionsMonitor<SparkCommerceShopOptions> options,
    TimeProvider clock) : ISparkShopDelivery
{
    public async Task<SparkShopDeliveryOptions> GetOptionsAsync(string country, string? postalCode, CancellationToken cancellationToken = default)
    {
        var basket = await checkout.GetBasketAsync(cancellationToken);
        using var session = store.OpenAsyncSession();
        var parcels = await DeliveryMath.FromBasketAsync(session, basket, options.CurrentValue, cancellationToken);
        var chosen = await CurrentChoiceAsync(session, basket, cancellationToken);
        if (parcels.Count == 0)
            return new SparkShopDeliveryOptions(false, [], null);
        if (!ShippingAddressRules.IsCountryCode(country))
            return new SparkShopDeliveryOptions(true, [], chosen is null ? null : DeliveryMath.View(chosen));
        var shipTo = new ShippingAddress { Country = country.ToUpperInvariant(), PostalCode = postalCode?.Trim() is { Length: <= 16 } p ? p : "" };
        var rates = await DeliveryMath.CommonRatesAsync(shipping, shipTo, parcels, cancellationToken);
        return new SparkShopDeliveryOptions(true, rates.Select(DeliveryMath.View).ToList(), chosen is null ? null : DeliveryMath.View(chosen));
    }

    public async Task<SparkShopDeliveryChoice> ChooseAsync(SparkShopDeliveryRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var shipTo = ShippingAddressRules.Normalize(DeliveryMath.Address(request.ShipTo));
        if (ShippingAddressRules.Validate(shipTo, "delivery").Count > 0 || string.IsNullOrWhiteSpace(shipTo.PostalCode))
            throw ShopException.Of(ShopRefusals.InvalidAddress);

        var basket = await checkout.GetBasketAsync(cancellationToken);
        using var session = store.OpenAsyncSession();
        var parcels = await DeliveryMath.FromBasketAsync(session, basket, options.CurrentValue, cancellationToken);
        if (parcels.Count == 0)
            throw ShopException.Of(ShopRefusals.NothingToShip);
        var rates = await DeliveryMath.CommonRatesAsync(shipping, shipTo, parcels, cancellationToken);
        var rate = rates.FirstOrDefault(r => string.Equals(r.Method, request.Method, StringComparison.OrdinalIgnoreCase))
            ?? throw ShopException.Of(ShopRefusals.MethodNotOffered, request.Method);

        ShipmentServicePoint? point = null;
        if (rate.ServicePoint)
        {
            if (string.IsNullOrWhiteSpace(request.ServicePointId))
                throw ShopException.Of(ShopRefusals.ServicePointRequired);
            // The client names the point; its address comes from the carrier again, never from the request (GB5).
            var points = await shipping.SearchServicePointsAsync(request.ServicePointCarrier ?? rate.Carrier,
                new ServicePointQuery { Country = shipTo.Country, PostalCode = shipTo.PostalCode, City = shipTo.City, Street = shipTo.Street, HouseNumber = shipTo.HouseNumber, Limit = 50 },
                cancellationToken);
            var found = points.FirstOrDefault(p => p.Id == request.ServicePointId) ?? throw ShopException.Of(ShopRefusals.ServicePointRequired, request.ServicePointId);
            point = new ShipmentServicePoint { Id = found.Id, Name = found.Name, Kind = found.Kind, Address = found.Address };
        }

        var now = clock.GetUtcNow();
        var choice = new ShopDeliveryChoice
        {
            Id = "ShopDeliveryChoices/" + Guid.NewGuid().ToString("N"),
            VisitorId = visitor.VisitorId,
            UserId = visitor.UserId,
            ShipTo = shipTo,
            Method = rate.Method,
            ServicePoint = point,
            UpdatedAt = now,
            ExpiresAt = now + options.CurrentValue.Delivery.ChoiceLifetime,
        };
        await session.StoreAsync(choice, choice.Id, cancellationToken);
        session.Advanced.GetMetadataFor(choice)[Raven.Client.Constants.Documents.Metadata.Expires] = choice.ExpiresAt.UtcDateTime.ToString("O");
        await session.SaveChangesAsync(cancellationToken);
        await checkout.SetChoicesAsync(new Dictionary<string, string?> { [ShopChoices.Delivery] = choice.Id }, shipTo.Email, cancellationToken);
        return DeliveryMath.View(choice);
    }

    private async Task<ShopDeliveryChoice?> CurrentChoiceAsync(IAsyncDocumentSession session, SparkBasket basket, CancellationToken cancellationToken)
    {
        if (!basket.Choices.TryGetValue(ShopChoices.Delivery, out var id) || string.IsNullOrEmpty(id))
            return null;
        var choice = await session.LoadAsync<ShopDeliveryChoice>(id, cancellationToken);
        return choice is not null && DeliveryMath.Owns(choice, visitor.UserId, visitor.VisitorId) ? choice : null;
    }
}

/// <summary>
/// The delivery as a pricing step (order <c>Delivery:PricingStepOrder</c>, default 500): one order extra per seller with
/// goods, coded <c>delivery:{method}</c> (<c>delivery:{method}:{seller}</c> when several sellers ship), priced by
/// Shipping for the chosen address. Side-effect free, as every pricing step must be. A basket with goods and no valid
/// choice quotes without delivery; "Order with obligation to pay" is then refused with <c>deliveryRequired</c>, since the
/// total must include delivery (CRD Art. 6(1)(e)).
/// </summary>
internal sealed class ShopDeliveryPricingStep(
    IDocumentStore store,
    ISparkShipping shipping,
    IServiceProvider services,
    IOptionsMonitor<SparkCommerceShopOptions> options) : ICheckoutPricingStep
{
    public int Order => options.CurrentValue.Delivery.PricingStepOrder;

    public async Task ApplyAsync(CheckoutPricingContext context, CancellationToken cancellationToken)
    {
        using var session = store.OpenAsyncSession();
        var parcels = await DeliveryMath.FromLinesAsync(session, context.Lines.Select(l => (l.Item, l.Quantity, l.Total)).ToList(), context.Currency, options.CurrentValue, cancellationToken);
        if (parcels.Count == 0)
            return;

        var choice = context.Choices.TryGetValue(ShopChoices.Delivery, out var id) && !string.IsNullOrEmpty(id)
            ? await session.LoadAsync<ShopDeliveryChoice>(id, cancellationToken)
            : null;
        var visitor = services.GetService<ISparkVisitor>();
        if (choice is null || !DeliveryMath.Owns(choice, context.BuyerUserId ?? visitor?.UserId, visitor?.VisitorId))
        {
            if (context.Placing)
                throw CheckoutException.Of(ShopRefusals.DeliveryRequired);
            return;
        }

        foreach (var parcel in parcels)
        {
            var rates = await shipping.GetRatesAsync(DeliveryMath.RateRequest(choice.ShipTo, parcel), cancellationToken);
            var rate = rates.FirstOrDefault(r => string.Equals(r.Method, choice.Method, StringComparison.OrdinalIgnoreCase));
            if (rate is null || rate.Price.Currency != context.Currency)
            {
                if (context.Placing)
                    throw CheckoutException.Of(ShopRefusals.DeliveryRequired, subject: choice.Method);
                return;
            }
            context.Extras.Add(new PricedExtra(
                DeliveryMath.ExtraCode(rate.Method, parcel.Seller, parcels.Count > 1),
                PricedExtraKind.Other,
                rate.Price,
                "commerceShop.delivery.extra",
                ShopChoices.Source,
                VatCategory: null, // ancillary to the goods: Invoicing follows the principal supply (VAT Directive Art. 78(b): transport costs charged to the customer belong to the taxable amount; checked 2026-10-20)
                Reference: choice.Id));
        }
    }
}

/// <summary>
/// GA1, CRD Art. 13(2): the least expensive standard delivery the buyer could have chosen, recorded on the shop's order
/// when it was placed. A withdrawal refunds that much of the delivery and keeps the surcharge for a dearer method.
/// </summary>
internal sealed class ShopStandardDeliveryCost(IDocumentStore store) : IStandardDeliveryCost
{
    public async Task<Money?> GetStandardAsync(CheckoutOrder order, CheckoutOrderExtra extra, CancellationToken cancellationToken)
    {
        if (!string.Equals(extra.Source, ShopChoices.Source, StringComparison.Ordinal) || order.Id is null)
            return null;
        using var session = store.OpenAsyncSession();
        var orderId = order.Id;
        var shopOrders = await session.Query<ShopOrder>().Where(o => o.OrderId == orderId).Take(64).ToListAsync(cancellationToken);
        var shopOrder = shopOrders.FirstOrDefault(o => string.Equals(o.DeliveryExtraCode, extra.Code, StringComparison.Ordinal));
        if (shopOrder?.StandardDeliveryCost is not { } standard || standard.Currency != extra.Amount.Currency)
            return null;
        return standard < extra.Amount ? standard : extra.Amount;
    }
}

/// <summary>The delivery step's shared arithmetic and mappings.</summary>
internal static class DeliveryMath
{
    public static bool Owns(ShopDeliveryChoice choice, string? userId, string? visitorId)
        => (choice.UserId is { Length: > 0 } u && u == userId) || (choice.VisitorId is { Length: > 0 } v && v == visitorId);

    public static string ExtraCode(string method, string? seller, bool perSeller)
        => ShopChoices.DeliveryExtraPrefix + method + (perSeller ? ":" + (seller ?? "platform") : "");

    public static ShippingRateRequest RateRequest(ShippingAddress shipTo, DeliveryParcel parcel) => new()
    {
        ShipTo = shipTo,
        Subtotal = parcel.Subtotal,
        WeightGrams = parcel.WeightGrams,
        DangerousGoods = parcel.DangerousGoods,
        Owner = parcel.Seller,
    };

    /// <summary>The rates every seller's parcel is offered (a method missing for one seller is not offered), priced as their sum.</summary>
    public static async Task<IReadOnlyList<ShippingRate>> CommonRatesAsync(ISparkShipping shipping, ShippingAddress shipTo, IReadOnlyList<DeliveryParcel> parcels, CancellationToken cancellationToken)
    {
        Dictionary<string, ShippingRate>? common = null;
        foreach (var parcel in parcels)
        {
            var rates = await shipping.GetRatesAsync(RateRequest(shipTo, parcel), cancellationToken);
            var byMethod = rates.GroupBy(r => r.Method, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            common = common is null
                ? byMethod
                : common.Where(c => byMethod.ContainsKey(c.Key) && byMethod[c.Key].Price.Currency == c.Value.Price.Currency)
                    .ToDictionary(c => c.Key, c => c.Value with { Price = c.Value.Price + byMethod[c.Key].Price, Free = c.Value.Free && byMethod[c.Key].Free }, StringComparer.OrdinalIgnoreCase);
        }
        return (common ?? []).Values.OrderBy(r => r.Order).ThenBy(r => r.Price.ToMinorUnits()).ToList();
    }

    /// <summary>The basket's goods per seller (shop items of goods only).</summary>
    public static async Task<List<DeliveryParcel>> FromBasketAsync(IAsyncDocumentSession session, SparkBasket basket, SparkCommerceShopOptions options, CancellationToken cancellationToken)
    {
        var currency = basket.Currency ?? basket.Total?.Currency;
        if (currency is null)
            return [];
        var lines = new List<(string ItemId, string? Seller, int Quantity, Money Total)>();
        foreach (var line in basket.Lines.Where(l => ShopItemIds.Handles(l.ItemId)))
            lines.Add((line.ItemId, line.SellerOrganizationId, line.Quantity, line.Total ?? Money.Zero(currency)));
        return await GroupAsync(session, lines, currency, options, onlyGoods: true, cancellationToken);
    }

    /// <summary>The priced lines' goods per seller.</summary>
    public static async Task<List<DeliveryParcel>> FromLinesAsync(IAsyncDocumentSession session, IReadOnlyList<(CheckoutItem Item, int Quantity, Money Total)> lines,
        string currency, SparkCommerceShopOptions options, CancellationToken cancellationToken)
    {
        var goods = lines.Where(l => l.Item.IsPhysicalGoods && ShopItemIds.Handles(l.Item.ItemId))
            .Select(l => (l.Item.ItemId, l.Item.OrganizationId, l.Quantity, l.Total)).ToList();
        return await GroupAsync(session, goods, currency, options, onlyGoods: false, cancellationToken);
    }

    private static async Task<List<DeliveryParcel>> GroupAsync(IAsyncDocumentSession session, IReadOnlyList<(string ItemId, string? Seller, int Quantity, Money Total)> lines,
        string currency, SparkCommerceShopOptions options, bool onlyGoods, CancellationToken cancellationToken)
    {
        if (lines.Count == 0)
            return [];
        var variantIds = lines.Select(l => ShopItemIds.VariantId(l.ItemId)!).Distinct(StringComparer.Ordinal).ToList();
        var variants = await session.Include<ShopVariant>(v => v.ProductId).LoadAsync<ShopVariant>(variantIds, cancellationToken);
        var productIds = variants.Values.OfType<ShopVariant>().Select(v => v.ProductId).Distinct(StringComparer.Ordinal).ToList();
        var products = await session.LoadAsync<ShopProduct>(productIds, cancellationToken);

        var parcels = new List<DeliveryParcel>();
        foreach (var group in lines.GroupBy(l => l.Seller ?? "", StringComparer.Ordinal))
        {
            var subtotal = Money.Zero(currency);
            var weight = 0;
            var dangerous = new List<DangerousGoodsItem>();
            var any = false;
            foreach (var line in group)
            {
                if (!variants.TryGetValue(ShopItemIds.VariantId(line.ItemId)!, out var variant) || variant is null
                    || !products.TryGetValue(variant.ProductId, out var product) || product is null)
                    continue;
                if (onlyGoods && product.Kind != EShopProductKind.Goods)
                    continue;
                any = true;
                if (line.Total.Currency == currency)
                    subtotal += line.Total;
                weight += (variant.WeightGrams ?? options.Delivery.DefaultWeightGrams) * Math.Max(0, line.Quantity);
                if (product.DangerousGoods is { } dg)
                    dangerous.Add(new DangerousGoodsItem { Class = dg, UnNumber = product.UnNumber });
            }
            if (any)
                parcels.Add(new DeliveryParcel(group.Key.Length == 0 ? null : group.Key, subtotal, weight, dangerous));
        }
        return parcels;
    }

    public static ShippingAddress Address(SparkShopAddress address) => new()
    {
        Name = address.Name ?? "",
        Company = address.Company,
        Street = address.Street ?? "",
        HouseNumber = address.HouseNumber,
        Addition = address.Addition,
        PostalCode = address.PostalCode ?? "",
        City = address.City ?? "",
        Country = address.Country ?? "",
        Email = address.Email,
        Phone = address.Phone,
    };

    public static SparkShopAddress View(ShippingAddress address)
        => new(address.Name, address.Company, address.Street, address.HouseNumber, address.Addition, address.PostalCode, address.City, address.Country, address.Email, address.Phone);

    public static SparkShopDeliveryChoice View(ShopDeliveryChoice choice)
        => new(View(choice.ShipTo), choice.Method, choice.ServicePoint?.Id, choice.ServicePoint?.Name,
            choice.ServicePoint is { } p ? PointLines(p.Address) : []);

    public static IReadOnlyList<string> PointLines(ShippingAddress address)
        => new[]
        {
            string.Join(" ", new[] { address.Street, address.HouseNumber }.Where(s => !string.IsNullOrWhiteSpace(s))),
            string.Join(" ", new[] { address.PostalCode, address.City }.Where(s => !string.IsNullOrWhiteSpace(s))),
        }.Where(s => s.Length > 0).ToList();

    public static SparkShopDeliveryRate View(ShippingRate rate)
        => new(rate.Method, rate.Kind.ToString(), rate.Carrier, rate.Price, rate.Free, rate.FreeFrom, rate.ServicePoint, rate.Customs.ToString(),
            rate.Incoterm?.ToString(), rate.Promise?.From, rate.Promise?.By, rate.PickupLocation);
}
