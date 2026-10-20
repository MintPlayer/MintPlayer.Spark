using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Commerce.Checkout;
using MintPlayer.Spark.Commerce.Checkout.Models;
using MintPlayer.Spark.Commerce.Fulfilment.Models;
using MintPlayer.Spark.Commerce.Shop;
using MintPlayer.Spark.Commerce.Shop.Catalog;
using MintPlayer.Spark.Commerce.Shop.Models;
using MintPlayer.Spark.Cron;
using MintPlayer.Spark.Fulfilment.Contracts;
using MintPlayer.Spark.Replication.Abstractions;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;

namespace MintPlayer.Spark.Commerce.Fulfilment.Services;

/// <summary>
/// Checkout's stock-feed seam (<see cref="ICheckoutStockFeed"/>): the quota of a goods line the warehouse ships is owned by
/// the stock feed (warehouse §4.4). A line is the warehouse's once its shop order was placed in Warehouse mode (the mode is
/// fixed per shop order), or once a fulfilment order was published for it.
/// </summary>
internal sealed class FulfilmentStockFeed(IDocumentStore store, IOptionsMonitor<SparkCommerceFulfilmentOptions> options) : ICheckoutStockFeed
{
    public async Task<bool> BacksAsync(CheckoutOrder order, CheckoutOrderLine line, CancellationToken cancellationToken)
    {
        var o = options.CurrentValue;
        if (order.Id is null || !FulfilmentContractBuilder.IsWarehouseLine(line, o))
            return false;
        using var session = store.OpenAsyncSession();
        if (await session.Advanced.ExistsAsync(FulfilmentIds.Link(o.ShopName, order.Number, line.SellerOrganizationId), cancellationToken))
            return true;
        var shopOrder = await session.LoadAsync<ShopOrder>(ShopDocumentIds.Order(order.Id, line.SellerOrganizationId), cancellationToken);
        return shopOrder?.Fulfilment == ShopFulfilmentMode.Warehouse;
    }
}

/// <summary>
/// Checkout's order events for the warehouse: a confirmed order is published, a refund or a lost dispute before shipment
/// becomes a cancellation, a return becomes an expected return. Idempotent: every event re-derives the whole state
/// (<see cref="FulfilmentSync"/>). A refund still pending at the provider raises no event yet; the reconcile picks it up.
/// </summary>
internal sealed class FulfilmentOrderEvents(FulfilmentSync sync, IOptionsMonitor<SparkCommerceFulfilmentOptions> options) : IOrderEventRecipient
{
    private static readonly HashSet<OrderEventKind> Relevant =
    [
        OrderEventKind.Confirmed, OrderEventKind.LineRefused, OrderEventKind.RefundIssued, OrderEventKind.Refunded, OrderEventKind.RefundFailed,
        OrderEventKind.Disputed, OrderEventKind.DisputeWon, OrderEventKind.DisputeLost, OrderEventKind.WithdrawalRequested, OrderEventKind.ReturnExpected,
        OrderEventKind.GuaranteeClaimOpened, OrderEventKind.GuaranteeClaimResolved, OrderEventKind.Cancelled, OrderEventKind.Voided,
    ];

    public async Task OnOrderEventAsync(OrderEvent orderEvent, CancellationToken cancellationToken)
    {
        var order = orderEvent.Order;
        if (order.Id is null || !Relevant.Contains(orderEvent.Kind) || !order.Lines.Any(l => FulfilmentContractBuilder.IsWarehouseLine(l, options.CurrentValue)))
            return;
        await sync.SyncOrderAsync(order.Id, cancellationToken);
    }
}

/// <summary>
/// The warehouse's facts arriving at the shop (<see cref="IReplicaChanged{T}"/>, HTTP transport): each one re-derives the
/// shop order it belongs to, or re-applies the stock level. The handlers read the stored replica, never the change's
/// payload alone, so a late notification of an older state does nothing (order-insensitive).
/// </summary>
internal sealed class FulfilmentReplicaHandlers(
    FulfilmentSync sync,
    FulfilmentStock stock,
    IOptionsMonitor<SparkCommerceFulfilmentOptions> options,
    ILogger<FulfilmentReplicaHandlers> logger)
    : IReplicaChanged<FulfilmentOrderStatus>, IReplicaChanged<FulfilmentShipment>, IReplicaChanged<FulfilmentReturnReceipt>, IReplicaChanged<FulfilmentStockLevel>
{
    private string Shop => options.CurrentValue.ShopName;

    public Task OnReplicaChangedAsync(ReplicaChange<FulfilmentOrderStatus> change, CancellationToken cancellationToken)
        => SyncAsync(FulfilmentIds.LinkOfOrder(Shop, (change.Current ?? change.Previous)?.FulfilmentOrderId), change.Id, cancellationToken);

    public Task OnReplicaChangedAsync(ReplicaChange<FulfilmentShipment> change, CancellationToken cancellationToken)
        => SyncAsync(FulfilmentIds.LinkOfOrder(Shop, (change.Current ?? change.Previous)?.FulfilmentOrderId), change.Id, cancellationToken);

    public Task OnReplicaChangedAsync(ReplicaChange<FulfilmentReturnReceipt> change, CancellationToken cancellationToken)
    {
        var receipt = change.Current ?? change.Previous;
        var linkId = FulfilmentIds.LinkOfExpectedReturn(Shop, receipt?.ExpectedReturnId) ?? FulfilmentIds.LinkOfOrder(Shop, receipt?.FulfilmentOrderId);
        if (linkId is null && change.Current is { ExpectedReturnId: null })
            logger.LogWarning("Blind return receipt {ReceiptId} (owner {OwnerId}) names no expected return; staff match it to a return.", change.Id, change.Current.OwnerId);
        return SyncAsync(linkId, change.Id, cancellationToken);
    }

    public async Task OnReplicaChangedAsync(ReplicaChange<FulfilmentStockLevel> change, CancellationToken cancellationToken)
    {
        if (change.Current is { } level)
            await stock.ApplyAsync(level, force: false, cancellationToken);
    }

    private async Task SyncAsync(string? linkId, string replicaId, CancellationToken cancellationToken)
    {
        if (linkId is null)
        {
            logger.LogDebug("Replica {ReplicaId} names no fulfilment order of this shop; ignored.", replicaId);
            return;
        }
        await sync.SyncLinkAsync(linkId, cancellationToken);
    }
}

/// <summary>
/// The reconcile (<c>Spark:Commerce:Fulfilment:ReconcileSchedule</c>): re-derives the open shop orders, least recently
/// checked first. It is the only path on the ETL transport (ETL raises no change notifications), picks up refunds still
/// pending at the provider (no event yet), and repairs anything a lost notification missed.
/// </summary>
internal sealed class FulfilmentReconcileJob(IDocumentStore store, FulfilmentSync sync, IOptionsMonitor<SparkCommerceFulfilmentOptions> options, ILogger<FulfilmentReconcileJob> logger) : ISparkCronJob
{
    public static string CronSchedule => "0 * * * * *";

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        List<string> due;
        using (var session = store.OpenAsyncSession())
            due = await session.Query<FulfilmentLink>()
                .Where(l => !l.Closed)
                .OrderBy(l => l.CheckedAt)
                .Select(l => l.Id!)
                .Take(Math.Max(1, options.CurrentValue.ReconcileBatchSize))
                .ToListAsync(cancellationToken);
        foreach (var linkId in due)
        {
            try
            {
                await sync.SyncLinkAsync(linkId, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Reconciling fulfilment {LinkId} failed; the next run retries.", linkId);
            }
        }
    }
}

/// <summary>The catalog and stock sweep (<c>Spark:Commerce:Fulfilment:CatalogSchedule</c>).</summary>
internal sealed class FulfilmentCatalogJob(FulfilmentCatalog catalog, FulfilmentStock stock, ILogger<FulfilmentCatalogJob> logger) : ISparkCronJob
{
    public static string CronSchedule => "0 */5 * * * *";

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var published = await catalog.SweepAsync(cancellationToken);
        var applied = await stock.SweepAsync(cancellationToken);
        if (published > 0 || applied > 0)
            logger.LogInformation("Fulfilment sweep: {Published} catalog items published, {Applied} stock levels applied.", published, applied);
    }
}
