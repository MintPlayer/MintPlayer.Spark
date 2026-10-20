using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Warehouse.Indexes;
using MintPlayer.Spark.Warehouse.Outbound.Models;
using MintPlayer.Spark.Warehouse.Outbound.Services;
using MintPlayer.Spark.Warehouse.Owners;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Warehouse.Outbound.Actions;

/// <summary>
/// Outbound orders (PRD §4.5): listed through <c>Warehouse/Documents</c>, owner-filtered (WI5; raw session queries are not
/// row-filtered). Written only by the outbound services: read-only in the UI, changed through the custom actions.
/// </summary>
public partial class OutboundOrderActions : DefaultPersistentObjectActions<OutboundOrder>
{
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly IWarehouseOwnerScope ownerScope;

    public async Task<IQueryable<OutboundOrder>> OutboundOrders()
    {
        var owners = await ownerScope.VisibleOwnersAsync(nameof(OutboundOrder));
        var query = session.Query<WarehouseDocumentEntry>(WarehouseIndexes.Documents).Where(e => e.Kind == OutboundService.DocumentKind);
        if (owners is not null)
            query = query.Where(e => e.OrganizationId.In(owners));
        return query.OrderByDescending(e => e.At).OfType<OutboundOrder>();
    }

    public override Task<bool> IsAllowedAsync(string action, OutboundOrder entity)
        => Task.FromResult(action is "Read" or "Query");
}

/// <summary>Put-wall and consolidation slots: a small list per site (no index); a slot holding an order is not deleted.</summary>
public partial class ConsolidationSlotActions : DefaultPersistentObjectActions<ConsolidationSlot>
{
    [Inject] private readonly IAsyncDocumentSession session;

    public IQueryable<ConsolidationSlot> ConsolidationSlots() => session.Query<ConsolidationSlot>();

    public override Task<bool> IsAllowedAsync(string action, ConsolidationSlot entity)
        => Task.FromResult(action != "Delete" || entity.OrderId is null);
}
