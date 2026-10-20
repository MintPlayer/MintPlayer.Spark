using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Authentication;
using MintPlayer.Spark.Abstractions.Csv;
using MintPlayer.Spark.Warehouse.Models;
using MintPlayer.Spark.Warehouse.Owners;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Warehouse.Stock;

/// <summary>A shipped quantity of the traced lot (or item): which shipment line and fulfilment order carried it.</summary>
/// <param name="Reference">The ship movement's reference: the shipment line (<c>{fulfilmentOrderId}/shipments/{n}#line</c>) Packing wrote.</param>
public sealed record RecallShipmentLine(string MovementId, string? Reference, string? Channel, string? LotCode, decimal Quantity, DateTimeOffset ShippedAt);

/// <summary>Stock of the traced lot still on hand.</summary>
public sealed record RecallOnHand(string BalanceId, string LocationId, string? LotCode, decimal OnHand, decimal Allocated);

/// <summary>
/// The forward trace (round 5, commerce GA7; PRD §4.5 "Recall trace"): lot → ship movements → shipment lines and fulfilment
/// orders, plus stock still on hand and the lots received with it. The warehouse holds no buyer contact: the shop resolves
/// the fulfilment orders to buyers and sends the notice.
/// </summary>
public sealed record RecallTraceReport(string ItemId, string? LotCode, IReadOnlyList<RecallShipmentLine> Shipments,
    IReadOnlyList<RecallOnHand> OnHand, IReadOnlyList<string> SameReceiptLots)
{
    /// <summary>The report as CSV (WX11 writer: formula injection guarded).</summary>
    public byte[] ToCsv()
    {
        var csv = new SparkCsvWriter();
        csv.Header("kind", "reference", "channel", "lot", "location", "quantity", "at");
        foreach (var line in Shipments)
            csv.Row("shipped", line.Reference, line.Channel, line.LotCode, null, line.Quantity, line.ShippedAt.ToString("O"));
        foreach (var stock in OnHand)
            csv.Row("on-hand", stock.BalanceId, null, stock.LotCode, stock.LocationId, stock.OnHand, null);
        foreach (var lot in SameReceiptLots)
            csv.Row("same-receipt-lot", null, null, lot, null, null, null);
        return csv.ToBytes();
    }
}

public enum RecallTraceRefusal
{
    UnknownItem,
    /// <summary>The caller may not see the item's owner (WI5): answered as unknown.</summary>
    NotVisible,
}

/// <summary>
/// Owner-scoped, audited recall traces (WI5: an owner member traces only their own items; every run is recorded as a
/// <see cref="RecallTraceRun"/>). By lot for lot-tracked items, by item and ship-date range for untracked ones.
/// </summary>
public interface IRecallTrace
{
    Task<(RecallTraceReport? Report, RecallTraceRefusal? Refusal)> TraceAsync(string itemId, string? lotCode, DateOnly? from = null, DateOnly? to = null, CancellationToken cancellationToken = default);
}

internal sealed partial class RecallTrace : IRecallTrace
{
    [Inject] private readonly IDocumentStore documentStore;
    [Inject] private readonly IWarehouseOwnerScope ownerScope;
    [Inject] private readonly ISparkCurrentUser currentUser;
    [Inject] private readonly TimeProvider timeProvider;

    public async Task<(RecallTraceReport? Report, RecallTraceRefusal? Refusal)> TraceAsync(string itemId, string? lotCode, DateOnly? from = null, DateOnly? to = null, CancellationToken cancellationToken = default)
    {
        using var session = documentStore.OpenAsyncSession();
        var item = await session.LoadAsync<WarehouseItem>(itemId, cancellationToken);
        if (item is null)
            return (null, RecallTraceRefusal.UnknownItem);
        var visible = await ownerScope.VisibleOwnersAsync(nameof(StockMovement), cancellationToken);
        if (visible is not null && (item.OrganizationId is null || !visible.Contains(item.OrganizationId)))
            return (null, RecallTraceRefusal.NotVisible);

        DateTimeOffset? fromInstant = from is { } f ? new DateTimeOffset(f.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero) : null;
        DateTimeOffset? toInstant = to is { } t ? new DateTimeOffset(t.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero) : null;
        var shipped = await StockQueries.ShipmentsOfAsync(session, itemId, lotCode, fromInstant, toInstant, cancellationToken);

        var onHand = new List<RecallOnHand>();
        var sameReceipt = new List<string>();
        if (lotCode is not null)
        {
            foreach (var balance in await StockQueries.BalancesOfLotAsync(session, itemId, lotCode, cancellationToken))
            {
                if (balance.OnHand != 0)
                    onHand.Add(new RecallOnHand(balance.Id!, balance.LocationId, balance.LotCode, balance.OnHand, balance.Allocated));
            }
            var lot = await session.LoadAsync<StockLot>(StockLot.IdFor(itemId, lotCode), cancellationToken);
            if (lot?.ReceiptReference is { } receipt)
            {
                // The item's lots, an id-prefix load ({itemId}/lots/), no index.
                sameReceipt = (await session.Advanced.LoadStartingWithAsync<StockLot>($"{itemId}/lots/", pageSize: 1024, token: cancellationToken))
                    .Where(l => l.ReceiptReference == receipt && l.LotCode != lotCode)
                    .Select(l => l.LotCode)
                    .ToList();
            }
        }

        var report = new RecallTraceReport(itemId, lotCode,
            shipped.Select(m => new RecallShipmentLine(m.Id!, m.Reference, m.Channel, m.LotCode, m.Quantity, m.ServerTime)).ToList(),
            onHand, sameReceipt);

        await session.StoreAsync(new RecallTraceRun
        {
            Id = RecallTraceRun.IdPrefix + Guid.NewGuid().ToString("N"),
            OrganizationId = item.OrganizationId,
            ItemId = itemId,
            LotCode = lotCode,
            From = from,
            To = to,
            UserId = currentUser.Id,
            RanAt = timeProvider.GetUtcNow(),
            ShipmentLines = report.Shipments.Count,
            OnHand = report.OnHand.Sum(o => o.OnHand),
        }, cancellationToken);
        await session.SaveChangesAsync(cancellationToken);
        return (report, null);
    }
}
