using Microsoft.Extensions.Logging.Abstractions;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Commerce.Checkout.Models;
using MintPlayer.Spark.Commerce.Fulfilment;
using MintPlayer.Spark.Commerce.Fulfilment.Services;
using MintPlayer.Spark.Commerce.Inventory;
using MintPlayer.Spark.Commerce.Shipping.Models;
using MintPlayer.Spark.Commerce.Shop;
using MintPlayer.Spark.Commerce.Shop.Catalog;
using MintPlayer.Spark.Commerce.Shop.Contracts;
using MintPlayer.Spark.Commerce.Shop.Models;
using MintPlayer.Spark.Commerce.Shop.Orders;
using MintPlayer.Spark.Commerce.Tests.Events;
using MintPlayer.Spark.Commerce.Tests.Shop;
using MintPlayer.Spark.Fulfilment.Contracts;
using MintPlayer.Spark.Replication.Abstractions;
using Raven.Client.Documents;
using static MintPlayer.Spark.Commerce.Tests.Events.EventFixtures;

namespace MintPlayer.Spark.Commerce.Tests.Fulfilment;

/// <summary>Records what the fulfilment side tells Shop (shipped, delivered, stock).</summary>
internal sealed class RecordingShopOrders : ISparkShopOrders, ISparkShopStock
{
    public List<(string OrderId, string? Seller, IReadOnlyList<ShopShippedLine> Lines, DateTimeOffset At, string Reference)> Shipped { get; } = [];

    public List<(string OrderId, string? Seller, string Reference, DateTimeOffset At)> Delivered { get; } = [];

    public List<(string VariantId, int Capacity)> Stock { get; } = [];

    /// <summary>Capacities below this are refused, as the quota refuses one below sold + held.</summary>
    public int Floor { get; set; }

    public Task<ShopOrder?> GetAsync(string orderId, string? sellerOrganizationId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<IReadOnlyList<SparkShopOrder>?> GetOwnAsync(string orderNumber, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<SparkShopShipResult> ShipAsync(SparkShopShipRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException("The warehouse ships.");

    public Task MarkShippedAsync(string orderId, string? sellerOrganizationId, IReadOnlyList<ShopShippedLine> lines, DateTimeOffset at, string reference, CancellationToken cancellationToken = default)
    {
        Shipped.Add((orderId, sellerOrganizationId, lines, at, reference));
        return Task.CompletedTask;
    }

    public Task MarkDeliveredAsync(string orderId, string? sellerOrganizationId, string reference, DateTimeOffset at, CancellationToken cancellationToken = default)
    {
        Delivered.Add((orderId, sellerOrganizationId, reference, at));
        return Task.CompletedTask;
    }

    public Task InspectAsync(SparkShopInspectRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<string> CreateReturnLabelAsync(SparkShopReturnLabelRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException("The warehouse packs returns.");

    public Task<bool> SetStockAsync(string variantId, int capacity, CancellationToken cancellationToken = default)
    {
        if (capacity < Floor)
            return Task.FromResult(false);
        Stock.Add((variantId, capacity));
        return Task.FromResult(true);
    }
}

/// <summary>An inventory that knows the claims a test gives it and records restocks; anything else fails the test.</summary>
internal sealed class RecordingInventory : ISparkInventory
{
    public Dictionary<string, InventoryClaim> Claims { get; } = new(StringComparer.Ordinal);

    public List<(string ClaimId, string ReturnId)> Restocked { get; } = [];

    public QuotaAvailability Availability { get; set; } = new("q", 0, 0, 0, 0);

    public Task<InventoryClaim?> GetClaimAsync(string claimId, CancellationToken cancellationToken = default)
        => Task.FromResult(Claims.GetValueOrDefault(claimId));

    public Task<ClaimResult> RestockAsync(string claimId, string returnId, CancellationToken cancellationToken = default)
    {
        var record = Claims[claimId].Returns.First(r => r.Id == returnId);
        if (!record.StockReturned)
        {
            record.StockReturned = true;
            Restocked.Add((claimId, returnId));
        }
        return Task.FromResult(ClaimResult.Ok);
    }

    public Task<QuotaAvailability> GetAvailabilityAsync(string quotaId, CancellationToken cancellationToken = default) => Task.FromResult(Availability with { QuotaId = quotaId });

    private static Exception Unexpected() => new InvalidOperationException("The fulfilment side must not call this.");
    public Task CreateQuotaAsync(string quotaId, int capacity, int shardCount, CancellationToken cancellationToken = default) => throw Unexpected();
    public Task<ClaimResult> SetCapacityAsync(string quotaId, int capacity, CancellationToken cancellationToken = default) => throw Unexpected();
    public Task MapItemAsync(string itemId, IReadOnlyList<ItemQuota> quotas, CancellationToken cancellationToken = default) => throw Unexpected();
    public Task<ClaimResult> DefineChainAsync(string chainId, IReadOnlyList<IReadOnlyList<string>> rows, CancellationToken cancellationToken = default) => throw Unexpected();
    public Task<ClaimResult> TryClaimAsync(ClaimRequest request, CancellationToken cancellationToken = default) => throw Unexpected();
    public Task<ClaimResult> AdjustAsync(ClaimRequest target, CancellationToken cancellationToken = default) => throw Unexpected();
    public Task<ClaimResult> ExtendAsync(string claimId, DateTimeOffset expiresAt, CancellationToken cancellationToken = default) => throw Unexpected();
    public Task<ClaimResult> ConvertAsync(string claimId, IReadOnlyList<LimitLine>? lateLimits = null, CancellationToken cancellationToken = default) => throw Unexpected();
    public Task<ClaimResult> ReleaseAsync(string claimId, ReturnReason reason, bool returnStock = true, CancellationToken cancellationToken = default) => throw Unexpected();
    public Task<ClaimResult> ReturnAsync(string claimId, InventoryReturn request, CancellationToken cancellationToken = default) => throw Unexpected();
    public Task<ClaimResult> ExchangeAsync(InventoryExchange request, CancellationToken cancellationToken = default) => throw Unexpected();
    public Task<IReadOnlyDictionary<string, UnitState>> GetUnitStatesAsync(IEnumerable<string> unitIds, CancellationToken cancellationToken = default) => throw Unexpected();
    public Task<IReadOnlyList<ItemQuota>?> GetItemMappingAsync(string itemId, CancellationToken cancellationToken = default) => throw Unexpected();
}

/// <summary>Checkout's undelivered-goods refund as far as the fulfilment side sees it: the refund lands in the ledger (no return, units kept sold).</summary>
internal sealed class RecordingUndelivered(IDocumentStore store) : MintPlayer.Spark.Commerce.Checkout.Refunds.ISparkCheckoutUndelivered
{
    public List<MintPlayer.Spark.Commerce.Checkout.Refunds.UndeliveredRefund> Requests { get; } = [];

    public async Task<string?> RefundUndeliveredAsync(MintPlayer.Spark.Commerce.Checkout.Refunds.UndeliveredRefund request, CancellationToken cancellationToken = default)
    {
        Requests.Add(request);
        var refundId = "u-" + Requests.Count;
        using var session = store.OpenAsyncSession();
        var id = MintPlayer.Spark.Commerce.Checkout.Refunds.CheckoutRefundLedger.IdFor(request.OrderId);
        var ledger = await session.LoadAsync<MintPlayer.Spark.Commerce.Checkout.Refunds.CheckoutRefundLedger>(id, cancellationToken)
            ?? new MintPlayer.Spark.Commerce.Checkout.Refunds.CheckoutRefundLedger { OrderId = request.OrderId };
        ledger.Refunds.Add(new MintPlayer.Spark.Commerce.Checkout.Refunds.CheckoutRefund
        {
            RefundId = refundId,
            Kind = MintPlayer.Spark.Commerce.Checkout.Refunds.RefundKind.Cancellation,
            Note = FulfilmentSync.UndeliveredNote + request.Reference,
            Status = MintPlayer.Spark.Commerce.Checkout.Refunds.RefundStatus.Succeeded,
            Lines = request.Lines.Select(l => new MintPlayer.Spark.Commerce.Checkout.Refunds.RefundUnits { LineId = l.LineId, Quantity = l.Quantity }).ToList(),
        });
        await session.StoreAsync(ledger, id, cancellationToken);
        await session.SaveChangesAsync(cancellationToken);
        return refundId;
    }
}

/// <summary>Records the pushes the shop asks replication for (the session part is replication's own, tested there).</summary>
internal sealed class RecordingPublisher : IReplicaPublisher
{
    public List<(Type Type, string Id)> Changed { get; } = [];

    public Task MarkChangedAsync(object session, Type sourceType, string id, CancellationToken cancellationToken = default)
    {
        Changed.Add((sourceType, id));
        return Task.CompletedTask;
    }
}

/// <summary>
/// A stub warehouse (W8): it uses nothing but <c>MintPlayer.Spark.Fulfilment.Contracts</c> and a document store, the way a
/// Spark app without any commerce package consumes the shop's contracts and owns its own. Its documents land in the same
/// database the replicas would be delivered to.
/// </summary>
internal sealed class StubWarehouse(IDocumentStore store)
{
    private long version;

    public async Task<FulfilmentOrder?> OrderAsync(string id)
    {
        using var session = store.OpenAsyncSession();
        return await session.LoadAsync<FulfilmentOrder>(id);
    }

    public async Task<FulfilmentCatalogItem?> CatalogItemAsync(string id)
    {
        using var session = store.OpenAsyncSession();
        return await session.LoadAsync<FulfilmentCatalogItem>(id);
    }

    public async Task<FulfilmentExpectedReturn?> ExpectedReturnAsync(string id)
    {
        using var session = store.OpenAsyncSession();
        return await session.LoadAsync<FulfilmentExpectedReturn>(id);
    }

    /// <summary>Answers an order in its own status document (<c>{id}/status</c>).</summary>
    public async Task AnswerAsync(FulfilmentOrder order, FulfilmentOrderState state, Action<FulfilmentOrderStatus>? configure = null)
    {
        using var session = store.OpenAsyncSession();
        var id = order.Id + "/status";
        var status = await session.LoadAsync<FulfilmentOrderStatus>(id) ?? new FulfilmentOrderStatus { Id = id, FulfilmentOrderId = order.Id!, OwnerId = order.OwnerId };
        status.State = state;
        status.Lines = order.Lines.Select(l => status.Lines.FirstOrDefault(x => x.LineId == l.LineId) ?? new FulfilmentOrderLineStatus { LineId = l.LineId }).ToList();
        status.UpdatedAt = Now;
        configure?.Invoke(status);
        await session.StoreAsync(status, id);
        await session.SaveChangesAsync();
    }

    /// <summary>Ships <paramref name="lines"/> of an order from a lot, as shipment <paramref name="n"/>.</summary>
    public async Task<FulfilmentShipment> ShipAsync(FulfilmentOrder order, int n, string lotCode, FulfilmentTrackingState tracking, params (string LineId, decimal Quantity)[] lines)
    {
        using var session = store.OpenAsyncSession();
        var shipment = new FulfilmentShipment
        {
            Id = $"{order.Id}/shipments/{n}",
            FulfilmentOrderId = order.Id!,
            OwnerId = order.OwnerId,
            ShippedAt = Now.AddHours(n),
            TrackingState = tracking,
            CarrierCode = "test",
            Lines = lines.Select(l => new FulfilmentShipmentLine
            {
                LineId = l.LineId,
                Sku = order.Lines.First(x => x.LineId == l.LineId).Sku,
                Quantity = l.Quantity,
                Lots = [new FulfilmentShippedLot { LotCode = lotCode, Quantity = l.Quantity }],
            }).ToList(),
            Packages = [new FulfilmentPackage { TrackingNumber = "TRACK-" + n, WeightGrams = 500 }],
        };
        await session.StoreAsync(shipment, shipment.Id);
        await session.SaveChangesAsync();
        return shipment;
    }

    /// <summary>Publishes a stock level (one document per owner and SKU, a higher version each time).</summary>
    public async Task<FulfilmentStockLevel> StockAsync(string ownerId, string sku, decimal onHand, params FulfilmentChannelStock[] channels)
    {
        using var session = store.OpenAsyncSession();
        var id = $"FulfilmentStockLevels/{ownerId}/{sku}";
        var level = new FulfilmentStockLevel { Id = id, OwnerId = ownerId, Sku = sku, OnHandSellable = onHand, Channels = [.. channels], Version = ++version };
        await session.StoreAsync(level, id);
        await session.SaveChangesAsync();
        return level;
    }

    /// <summary>Receives an expected return and gives every line <paramref name="disposition"/>.</summary>
    public async Task<FulfilmentReturnReceipt> ReceiveAsync(FulfilmentExpectedReturn expected, FulfilmentReturnDisposition disposition)
    {
        using var session = store.OpenAsyncSession();
        var receipt = new FulfilmentReturnReceipt
        {
            Id = "FulfilmentReturnReceipts/" + expected.ReturnNumber,
            ExpectedReturnId = expected.Id,
            FulfilmentOrderId = expected.FulfilmentOrderId,
            OwnerId = expected.OwnerId,
            ReceivedAt = Now.AddDays(10),
            Lines = expected.Lines.Select(l => new FulfilmentReturnReceiptLine { ReturnLineId = l.LineId, Sku = l.Sku, Quantity = l.Quantity, Disposition = disposition }).ToList(),
        };
        await session.StoreAsync(receipt, receipt.Id);
        await session.SaveChangesAsync();
        return receipt;
    }
}

/// <summary>The shop side over RavenDB with recording Shop, Checkout and inventory ports.</summary>
internal sealed class FulfilmentHost
{
    public const string ShopName = "shop-a";
    public const string OrderId = "CheckoutOrders/1-A";
    public const string Number = "SO-1";
    public const string VariantA = "ShopVariants/1-A";
    public const string VariantB = "ShopVariants/2-A";

    public FulfilmentHost(IDocumentStore store, Action<SparkCommerceFulfilmentOptions>? configure = null)
    {
        Store = store;
        Options.ShopName = ShopName;
        configure?.Invoke(Options);
        Warehouse = new StubWarehouse(store);
        Undelivered = new RecordingUndelivered(store);
    }

    public IDocumentStore Store { get; }

    public SparkCommerceFulfilmentOptions Options { get; } = new();

    public EventTestClock Clock { get; } = new(Now);

    public RecordingShopOrders Shop { get; } = new();

    public RecordingRefunds Refunds { get; } = new();

    public RecordingInventory Inventory { get; } = new();

    public RecordingPublisher Publisher { get; } = new();

    public RecordingUndelivered Undelivered { get; private set; } = null!;

    public StubWarehouse Warehouse { get; }

    public FulfilmentCatalog Catalog => new(Store, Publisher, Monitor(Options), NullLogger<FulfilmentCatalog>.Instance);

    public FulfilmentSync Sync => new(Store, Shop, Refunds, Undelivered, Inventory, Publisher, Catalog, Monitor(Options), Clock, NullLogger<FulfilmentSync>.Instance);

    public FulfilmentStock Stock => new(Store, Shop, Inventory, Monitor(Options), Clock, NullLogger<FulfilmentStock>.Instance);

    public FulfilmentRecalls Recalls(IServiceProvider services) => new(Store, services, Monitor(Options), Clock, NullLogger<FulfilmentRecalls>.Instance);

    /// <summary>The first fulfilment order's id.</summary>
    public static string Revision(int n) => $"FulfilmentOrders/{ShopName}/{Number}/platform/{n}";

    /// <summary>A confirmed order of 2 × A and 1 × B, its shop order in Warehouse mode, the products and variants.</summary>
    public async Task SeedAsync(Action<CheckoutOrder>? order = null, Action<ShopOrder>? shopOrder = null)
    {
        var product = ShopRulesTests.Goods(p => p.IsPublished = true);
        var variants = new[]
        {
            new ShopVariant { Id = VariantA, ProductId = product.Id!, Sku = "OIL-75", Gtin = "5400000000005", Options = [new ShopVariantOption { Name = "size", Value = "75 cl" }] },
            new ShopVariant { Id = VariantB, ProductId = product.Id!, Sku = "OIL-50" },
        };
        var checkoutOrder = new CheckoutOrder
        {
            Id = OrderId,
            Number = Number,
            State = OrderState.Confirmed,
            BuyerEmail = "buyer@example.org",
            BuyerName = "A. Buyer",
            CreatedAt = Now.AddMinutes(-5),
            ConfirmedAt = Now,
            Lines =
            [
                Line("L1", VariantA, 2, "claims/L1"),
                Line("L2", VariantB, 1, "claims/L2"),
            ],
        };
        order?.Invoke(checkoutOrder);
        var shop = new ShopOrder
        {
            Id = ShopDocumentIds.Order(OrderId, null),
            OrderId = OrderId,
            OrderNumber = Number,
            State = EShopOrderState.Paid,
            Fulfilment = ShopFulfilmentMode.Warehouse,
            Carrier = "test",
            ServiceCode = "standard",
            ShipTo = new ShippingAddress { Name = "A. Buyer", Street = "Kerkstraat", HouseNumber = "1", PostalCode = "1000", City = "Brussel", Country = "be", Email = "buyer@example.org" },
            Lines =
            [
                new ShopOrderLine { LineId = "L1", ItemId = ShopItemIds.For(VariantA), VariantId = VariantA, ProductId = product.Id!, Sku = "OIL-75", Quantity = 2, Title = TranslatedString.Of("en", "Olive oil · 75 cl") },
                new ShopOrderLine { LineId = "L2", ItemId = ShopItemIds.For(VariantB), VariantId = VariantB, ProductId = product.Id!, Sku = "OIL-50", Quantity = 1, Title = TranslatedString.Of("en", "Olive oil") },
            ],
        };
        shopOrder?.Invoke(shop);
        using var session = Store.OpenAsyncSession();
        await session.StoreAsync(product);
        foreach (var variant in variants)
            await session.StoreAsync(variant);
        await session.StoreAsync(checkoutOrder);
        await session.StoreAsync(shop);
        await session.SaveChangesAsync();
    }

    private static CheckoutOrderLine Line(string lineId, string variantId, int quantity, string claimId) => new()
    {
        LineId = lineId,
        ItemId = ShopItemIds.For(variantId),
        Channel = "shop",
        Quantity = quantity,
        IsPhysicalGoods = true,
        ClaimId = claimId,
        Capacity = new CheckoutCapacitySnapshot { Quotas = [new QuotaLine(ShopQuotas.Variant(variantId), quantity)] },
        Status = OrderLineStatus.Active,
    };

    public async Task UpdateAsync<T>(string id, Action<T> change) where T : class
    {
        using var session = Store.OpenAsyncSession();
        change((await session.LoadAsync<T>(id))!);
        await session.SaveChangesAsync();
    }

    public async Task StoreAsync(object document, string id)
    {
        using var session = Store.OpenAsyncSession();
        await session.StoreAsync(document, id);
        await session.SaveChangesAsync();
    }

    public async Task<T?> LoadAsync<T>(string id) where T : class
    {
        using var session = Store.OpenAsyncSession();
        return await session.LoadAsync<T>(id);
    }

    /// <summary>A converted claim with one return per id (units of one quota).</summary>
    public void Claim(string claimId, string variantId, params (string ReturnId, int Units, bool StockReturned)[] returns)
        => Inventory.Claims[claimId] = new InventoryClaim
        {
            Id = claimId,
            State = ClaimState.Converted,
            Returns = returns.Select((r, i) => new ClaimReturn
            {
                Id = r.ReturnId,
                Reason = ReturnReason.Refund,
                At = Now.AddMinutes(i),
                StockReturned = r.StockReturned,
                Allocations = [new ShardAllocation { QuotaId = ShopQuotas.Variant(variantId), ShardId = ShopQuotas.Variant(variantId) + "/shards/0", N = r.Units }],
            }).ToList(),
        };
}
