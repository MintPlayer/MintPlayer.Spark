using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Claims;
using MintPlayer.Spark.Replication.Abstractions;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Warehouse.Barcodes;
using MintPlayer.Spark.Warehouse.Items;
using MintPlayer.Spark.Warehouse.Models;
using MintPlayer.Spark.Warehouse.Outbound.Models;
using MintPlayer.Spark.Warehouse.Owners;
using MintPlayer.Spark.Warehouse.Stock;
using MintPlayer.Spark.Warehouse.Work;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Warehouse.Outbound.Services;

/// <summary>One allocation a pick task visits: carried in the task's payload (one task can serve several orders in a batch).</summary>
internal sealed record TaskAllocationRef(string OrderId, string LineId, int Seq, decimal Quantity);

/// <summary>
/// Outbound (PRD §4.3, §4.7). One scoped service behind the public interfaces, split by concern over partial files:
/// intake, release and allocation, work, picking, short picks, cancellation, consolidation, backorders and waves, recall.
/// </summary>
/// <remarks>
/// Every stock change goes through the core engine (<see cref="IWarehouseStock"/>) with a deterministic op id, and the
/// order's own bookkeeping rides in the same session (<see cref="StockOp.InSession"/>), so a retried or replayed step is
/// applied once and the order never disagrees with the balances. Order-only changes use optimistic concurrency with retry.
/// </remarks>
internal sealed partial class OutboundService : IOutboundIntake, IOutboundRelease, IOutboundWaves, IOutboundPicking,
    IOutboundCancellation, IOutboundConsolidation, IOutboundBackorders, ILotStatusObserver
{
    internal const string PayloadAllocations = "allocations";
    internal const string PayloadSortation = "sortation";
    internal const string PayloadOrder = "order";
    internal const string PayloadLine = "line";
    internal const string PayloadPick = "pick";
    internal const string OutboundReference = "outbound";

    /// <summary>Totes are unique while in use (commerce X12): one hold per (site, tote), owned by the batch (or batch#order on a cluster cart).</summary>
    internal static readonly SparkClaimKind<SparkClaimHold> Totes = SparkClaimKinds.Holds("warehouse/totes/");

    [Inject] private readonly IDocumentStore store;
    [Inject] private readonly IWarehouseStock stock;
    [Inject] private readonly IOptions<SparkWarehouseOptions> coreOptions;
    [Inject] private readonly IOptions<SparkWarehouseOutboundOptions> options;
    [Inject] private readonly TimeProvider clock;
    [Inject] private readonly IReplicaPublisher publisher;
    [Inject] private readonly IWarehouseOwnerLinks links;
    [Inject] private readonly IWarehouseItems items;
    [Inject] private readonly IEnumerable<IAllocationRule> rules;
    [Inject] private readonly IEnumerable<IPickStrategy> strategies;
    [Inject] private readonly IEnumerable<IPickRouter> routers;
    [Inject] private readonly IEnumerable<ISiteRouter> siteRouters;
    [Inject] private readonly IBarcodeResolver barcodes;
    [Inject] private readonly ISparkClaims claims;
    [Inject] private readonly IWarehouseReplenishment replenishment;
    [Inject] private readonly ILogger<OutboundService> logger;
    [Inject] private readonly IPackedOrderCancellation? packedCancellation;

    private DateTimeOffset Now => clock.GetUtcNow();

    private SparkWarehouseOutboundOptions Options => options.Value;

    // --- shared helpers ------------------------------------------------------------------------------------------

    internal static List<TaskAllocationRef> AllocationsOf(WarehouseTask task)
        => task.Payload.TryGetValue(PayloadAllocations, out var json) && !string.IsNullOrEmpty(json)
            ? JsonSerializer.Deserialize<List<TaskAllocationRef>>(json) ?? []
            : [];

    internal static void SetAllocations(WarehouseTask task, IEnumerable<TaskAllocationRef> allocations)
        => task.Payload[PayloadAllocations] = JsonSerializer.Serialize(allocations.ToList());

    internal static PickSortation SortationOf(WarehouseTask task)
        => task.Payload.TryGetValue(PayloadSortation, out var text) && Enum.TryParse<PickSortation>(text, out var sortation) ? sortation : PickSortation.None;

    /// <summary>The task ids of a pick batch: <c>warehouse-tasks/{batchKey}/{n}</c>, an id-prefix load (no index).</summary>
    internal static string TaskPrefix(string groupId) => WarehouseTask.IdPrefix + groupId[WarehouseTaskGroup.IdPrefix.Length..] + "/";

    internal static async Task<List<WarehouseTask>> TasksOfAsync(IAsyncDocumentSession session, string groupId, CancellationToken cancellationToken)
        => (await session.Advanced.LoadStartingWithAsync<WarehouseTask>(TaskPrefix(groupId), pageSize: 1024, token: cancellationToken)).ToList();

    private async Task<DateOnly> SiteTodayAsync(IAsyncDocumentSession session, string? siteId, CancellationToken cancellationToken)
    {
        var site = siteId is null ? null : await session.LoadAsync<WarehouseSite>(siteId, cancellationToken);
        var zone = SparkTimeZones.FindOrUtc(site?.TimeZone);
        return DateOnly.FromDateTime(SparkTimeZones.ToZone(Now, zone).DateTime);
    }

    private async Task<OutboundOrder?> LoadOrderAsync(string orderId, CancellationToken cancellationToken)
    {
        using var session = store.OpenAsyncSession();
        return await session.LoadAsync<OutboundOrder>(orderId, cancellationToken);
    }

    /// <summary>Changes the order in its own session (optimistic concurrency, retried) and publishes its status with it.</summary>
    private Task<T> ChangeOrderAsync<T>(string orderId, Func<IAsyncDocumentSession, OutboundOrder, Task<(T Result, bool Save)>> change, CancellationToken cancellationToken)
        => OutboundSessions.RetryAsync(store, async session =>
        {
            var order = await session.LoadAsync<OutboundOrder>(orderId, cancellationToken);
            if (order is null)
                return (default!, false);
            var (result, save) = await change(session, order);
            if (save)
            {
                order.UpdatedAt = Now;
                await OutboundStatus.WriteAsync(session, order, publisher, Now, cancellationToken);
            }
            return (result, save);
        }, cancellationToken);

    /// <summary>The <see cref="StockOp.InSession"/> body that changes the order (and publishes its status) with the stock command.</summary>
    private Func<IAsyncDocumentSession, CancellationToken, Task<string?>> WithOrder(string orderId, Func<IAsyncDocumentSession, OutboundOrder, Task<string?>> change)
        => async (session, cancellationToken) =>
        {
            var order = await session.LoadAsync<OutboundOrder>(orderId, cancellationToken);
            if (order is null)
                return $"Order {orderId} is gone.";
            var declined = await change(session, order);
            if (declined is not null)
                return declined;
            order.UpdatedAt = Now;
            await OutboundStatus.WriteAsync(session, order, publisher, Now, cancellationToken);
            return null;
        };

    private StockOp Op(string opId, string? userId = null, string? reference = null, string? deviceId = null, DateTimeOffset? deviceTime = null)
        => new(opId, userId, deviceId, deviceTime, reference);

    /// <summary>
    /// Re-derives the order's state from its lines after a change: picked once every allocation is picked (and has arrived
    /// where a slot is needed), shipped once nothing remains, otherwise unchanged.
    /// </summary>
    internal static void Settle(OutboundOrder order)
    {
        if (order.State is OutboundState.Rejected or OutboundState.Cancelled)
            return;
        if (order.Lines.All(l => l.Remaining == 0))
        {
            order.State = OutboundState.Shipped;
            return;
        }
        if (order.State is OutboundState.Released or OutboundState.Picking)
        {
            var anyPicked = order.Lines.Any(l => l.Picks.Count > 0);
            if (anyPicked && order.State == OutboundState.Released)
                order.State = OutboundState.Picking;
            if (anyPicked && OutboundReadiness.Complete(order))
                order.State = OutboundState.Picked;
        }
    }
}

/// <summary>What packing may take of an order (PRD §4.7: complete, unless partial shipment is allowed).</summary>
public static class OutboundReadiness
{
    /// <summary>Whether the order's units are sorted into a slot after picking (put wall, consolidation station).</summary>
    public static bool NeedsSlot(OutboundOrder order) => order.Sortation is PickSortation.PutWall or PickSortation.Consolidation;

    /// <summary>Units of a line ready at the pack station: picked and (where a slot is needed) arrived, not shipped, not put back.</summary>
    public static decimal Packable(OutboundOrder order, OutboundLine line)
        => NeedsSlot(order)
            ? line.Picks.Sum(p => Math.Max(0, Math.Min(p.Arrived, p.Quantity - p.PutBack) - p.Shipped))
            : line.Picks.Sum(p => Math.Max(0, p.Quantity - p.PutBack - p.Shipped));

    /// <summary>Every unit still owed is picked (and arrived where a slot is needed): nothing allocated unpicked, nothing unallocated.</summary>
    public static bool Complete(OutboundOrder order)
        => order.Lines.All(l => l.Remaining == 0 || (l.OpenAllocated == 0 && l.Unallocated == 0 && l.Backordered == 0 && Packable(order, l) >= l.Remaining));

    public static PackReadiness ForPacking(OutboundOrder order)
    {
        var units = order.Lines.ToDictionary(l => l.LineId, l => Math.Min(l.Remaining, Packable(order, l)), StringComparer.Ordinal);
        if (order.IsClosed)
            return new PackReadiness(false, units, $"The order is {order.State}.");
        if (order.CancellationRequested && order.CancellationAnswer is null)
            return new PackReadiness(false, units, "A cancellation is pending.");
        if (units.Values.Sum() <= 0)
            return new PackReadiness(false, units, "Nothing is picked yet.");
        if (!Complete(order) && !order.PartialShipmentAllowed)
            return new PackReadiness(false, units, "The order ships complete and is not complete yet.");
        return new PackReadiness(true, units);
    }
}
