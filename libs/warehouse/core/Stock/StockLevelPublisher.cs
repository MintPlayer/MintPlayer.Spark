using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Fulfilment.Contracts;
using MintPlayer.Spark.Replication.Abstractions;
using MintPlayer.Spark.Warehouse.Models;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Warehouse.Stock;

/// <summary>
/// The <c>stock-levels</c> publication (PRD §4.4, §4.3): a changed <see cref="StockLevel"/> is copied, in the movement's own
/// session (WI6 holds across the boundary), into one <see cref="FulfilmentStockLevel"/> per owner link of its organization,
/// named with the owner id that shop uses. Replication's dirty set flushes them every 1–2 s, and its row filter
/// (<c>WarehouseOwnerLinkRowFilter</c>) sends each shop only the copies of its own owner ids.
/// </summary>
public interface IStockLevelPublisher
{
    Task PublishAsync(IAsyncDocumentSession session, StockLevel level, CancellationToken cancellationToken);
}

internal sealed partial class StockLevelPublisher : IStockLevelPublisher
{
    [Inject] private readonly IReplicaPublisher replicaPublisher;

    public static string PublishedIdFor(string externalOwnerId, string sku)
        => $"{FulfilmentCollections.StockLevels}/{Uri.EscapeDataString(externalOwnerId)}/{Uri.EscapeDataString(sku)}";

    public async Task PublishAsync(IAsyncDocumentSession session, StockLevel level, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(level.OrganizationId) || string.IsNullOrEmpty(level.Sku))
            return;

        // Owner links are few (one per shop and owner): an id-prefix load, no index.
        var links = (await session.Advanced.LoadStartingWithAsync<WarehouseOwnerLink>(WarehouseOwnerLink.IdPrefix, pageSize: 1024, token: cancellationToken))
            .Where(l => l.Active && l.OrganizationId == level.OrganizationId)
            .ToList();

        foreach (var link in links)
        {
            var id = PublishedIdFor(link.ExternalOwnerId, level.Sku);
            var published = await session.LoadAsync<FulfilmentStockLevel>(id, cancellationToken);
            if (published is null)
            {
                published = new FulfilmentStockLevel { Id = id };
                await session.StoreAsync(published, id, cancellationToken);
            }
            published.OwnerId = link.ExternalOwnerId;
            published.Sku = level.Sku;
            published.OnHandSellable = level.OnHandSellable;
            published.Version = level.Version;
            published.Channels = level.Channels
                .Select(c => new FulfilmentChannelStock
                {
                    Channel = c.Channel,
                    ReservedOtherChannels = level.Channels.Where(o => o.Channel != c.Channel).Sum(o => o.Open),
                    ShippedTotal = c.ShippedTotal,
                })
                .ToList();
            // A raw session write: tell replication in the same commit (no pipeline interceptor runs).
            await replicaPublisher.MarkChangedAsync(session, typeof(FulfilmentStockLevel), id, cancellationToken);
        }
    }
}

/// <summary>
/// Hears a lot status change after it committed (round 5, commerce GA7): Outbound re-allocates or short-picks open
/// allocations on a recalled or quarantined lot. Register in DI (scoped); the core has no allocations of its own to move.
/// </summary>
public interface ILotStatusObserver
{
    Task OnLotStatusChangedAsync(StockLot lot, CancellationToken cancellationToken);
}
