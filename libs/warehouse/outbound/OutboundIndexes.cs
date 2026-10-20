using System.Linq.Expressions;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Warehouse.Indexes;
using MintPlayer.Spark.Warehouse.Outbound.Models;

namespace MintPlayer.Spark.Warehouse.Outbound;

/// <summary>
/// Outbound's map of <c>Warehouse_Documents</c> (Q-W26, WX16): the application's generator composes it with Inbound's and
/// Packing's into the one multi-map index the core names, so Outbound spends no index of its own (PRD §4.5 "Index budget").
/// It serves the order grid (by state, site, owner and date), wave release (accepted orders of a site) and the backorder
/// retry (<see cref="WarehouseDocumentEntry.Flag"/> <c>Backorder</c>).
/// </summary>
public static class OutboundIndexes
{
    [SparkIndexMap("Warehouse_Documents")]
    public static Expression<Func<IEnumerable<OutboundOrder>, IEnumerable<WarehouseDocumentEntry>>> Orders { get; } =
        orders => from o in orders
                  select new WarehouseDocumentEntry
                  {
                      Kind = "Outbound",
                      SiteId = o.SiteId,
                      OrganizationId = o.OrganizationId,
                      State = o.State.ToString(),
                      Number = o.OrderNumber,
                      At = o.PlacedAt,
                      Flag = o.Lines.Any(l => l.Backordered > 0) ? "Backorder" : null,
                  };
}
