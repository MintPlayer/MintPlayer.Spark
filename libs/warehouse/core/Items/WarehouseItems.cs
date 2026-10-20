using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Fulfilment.Contracts;
using MintPlayer.Spark.Gs1;
using MintPlayer.Spark.Replication.Abstractions;
using MintPlayer.Spark.Warehouse.Models;
using MintPlayer.Spark.Warehouse.Owners;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Warehouse.Items;

/// <summary>Why an item operation was refused.</summary>
public enum ItemRefusal
{
    /// <summary>(owner, SKU) is already held by another item.</summary>
    DuplicateSku,
    UnknownItem,
    /// <summary>Only a pre-item can be linked, and only to a catalog item of the same owner.</summary>
    NotLinkable,
}

public sealed record ItemResult(bool Succeeded, string? ItemId, ItemRefusal? Refusal = null, string? Detail = null);

/// <summary>
/// Items (PRD §4.5 "Items", WD9): warehouse items with (owner, SKU) as a unique key, pre-items and their link to the shop's
/// item (Q-W12), and the consumption of <c>catalog-items</c> replicas.
/// </summary>
public interface IWarehouseItems
{
    /// <summary>Creates an item; refuses a (owner, SKU) another item holds. <paramref name="item"/>'s id is assigned.</summary>
    Task<ItemResult> CreateAsync(WarehouseItem item, CancellationToken cancellationToken = default);

    /// <summary>The item of (owner, SKU), through the unique key; null when none.</summary>
    Task<WarehouseItem?> FindAsync(string organizationId, string sku, CancellationToken cancellationToken = default);

    /// <summary>
    /// Links a pre-item to a catalog item (Q-W12): the SKU may change, which re-keys the unique key; balances and the
    /// ledger keep pointing at the unchanged item id, so no stock moves and no history splits. Audited by the caller.
    /// </summary>
    Task<ItemResult> LinkAsync(string itemId, string catalogItemId, string sku, CancellationToken cancellationToken = default);
}

internal sealed partial class WarehouseItems : IWarehouseItems
{
    [Inject] private readonly IDocumentStore documentStore;

    /// <summary>The compare-exchange key that holds (owner, SKU) → item id.</summary>
    internal static string KeyFor(string organizationId, string sku)
        => $"warehouse/items/{Uri.EscapeDataString(organizationId)}/{Uri.EscapeDataString(sku.Trim().ToUpperInvariant())}";

    public async Task<ItemResult> CreateAsync(WarehouseItem item, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentException.ThrowIfNullOrWhiteSpace(item.OrganizationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(item.Sku);
        item.Id ??= WarehouseItem.IdPrefix + Guid.NewGuid().ToString("N");

        using var session = documentStore.OpenAsyncSession(new Raven.Client.Documents.Session.SessionOptions { TransactionMode = TransactionMode.ClusterWide });
        var key = KeyFor(item.OrganizationId, item.Sku);
        var held = await session.Advanced.ClusterTransaction.GetCompareExchangeValueAsync<string>(key, cancellationToken);
        if (held is not null)
            return new ItemResult(false, held.Value, ItemRefusal.DuplicateSku, item.Sku);

        session.Advanced.ClusterTransaction.CreateCompareExchangeValue(key, item.Id);
        await session.StoreAsync(item, item.Id, cancellationToken);
        try
        {
            await session.SaveChangesAsync(cancellationToken);
        }
        catch (Raven.Client.Exceptions.ConcurrencyException)
        {
            return new ItemResult(false, null, ItemRefusal.DuplicateSku, item.Sku);
        }
        return new ItemResult(true, item.Id);
    }

    public async Task<WarehouseItem?> FindAsync(string organizationId, string sku, CancellationToken cancellationToken = default)
    {
        using var session = documentStore.OpenAsyncSession(new Raven.Client.Documents.Session.SessionOptions { TransactionMode = TransactionMode.ClusterWide });
        var held = await session.Advanced.ClusterTransaction.GetCompareExchangeValueAsync<string>(KeyFor(organizationId, sku), cancellationToken);
        return held is null ? null : await session.LoadAsync<WarehouseItem>(held.Value, cancellationToken);
    }

    public async Task<ItemResult> LinkAsync(string itemId, string catalogItemId, string sku, CancellationToken cancellationToken = default)
    {
        using var session = documentStore.OpenAsyncSession(new Raven.Client.Documents.Session.SessionOptions { TransactionMode = TransactionMode.ClusterWide });
        var item = await session.LoadAsync<WarehouseItem>(itemId, cancellationToken);
        if (item is null)
            return new ItemResult(false, itemId, ItemRefusal.UnknownItem);
        if (!item.IsPreItem)
            return new ItemResult(false, itemId, ItemRefusal.NotLinkable, "Only a pre-item can be linked.");

        var oldKey = KeyFor(item.OrganizationId!, item.Sku);
        var newKey = KeyFor(item.OrganizationId!, sku);
        if (oldKey != newKey)
        {
            var taken = await session.Advanced.ClusterTransaction.GetCompareExchangeValueAsync<string>(newKey, cancellationToken);
            if (taken is not null && taken.Value != item.Id)
                return new ItemResult(false, itemId, ItemRefusal.DuplicateSku, sku);
            var old = await session.Advanced.ClusterTransaction.GetCompareExchangeValueAsync<string>(oldKey, cancellationToken);
            if (old is not null)
                session.Advanced.ClusterTransaction.DeleteCompareExchangeValue(old);
            if (taken is null)
                session.Advanced.ClusterTransaction.CreateCompareExchangeValue(newKey, item.Id!);
        }

        item.Sku = sku.Trim();
        item.CatalogItemId = catalogItemId;
        item.IsPreItem = false;
        try
        {
            await session.SaveChangesAsync(cancellationToken);
        }
        catch (Raven.Client.Exceptions.ConcurrencyException)
        {
            return new ItemResult(false, itemId, ItemRefusal.DuplicateSku, sku);
        }
        return new ItemResult(true, itemId);
    }
}

/// <summary>
/// Consumes the shops' <c>catalog-items</c> (PRD §4.5, WD9): a new catalog item of a linked owner gets its warehouse item
/// (or links the pre-item of the same SKU), its GTINs are registered as barcodes, and a deleted one only marks the item
/// (<see cref="WarehouseItem.CatalogItemRemoved"/>): the stock stays. An unlinked owner's items are ignored until a link
/// exists (the next change, or the reconcile, brings them again).
/// </summary>
internal sealed partial class CatalogItemConsumer : IReplicaChanged<FulfilmentCatalogItem>
{
    [Inject] private readonly IDocumentStore documentStore;
    [Inject] private readonly IWarehouseOwnerLinks links;
    [Inject] private readonly IWarehouseItems items;

    public async Task OnReplicaChangedAsync(ReplicaChange<FulfilmentCatalogItem> change, CancellationToken cancellationToken)
    {
        var shape = change.Current ?? change.Previous;
        if (shape is null)
            return;
        var link = await links.ResolveAsync(change.SourceModule, shape.OwnerId, cancellationToken);
        if (link is null)
            return;

        var existing = await items.FindAsync(link.OrganizationId, shape.Sku, cancellationToken);
        if (change.IsDeleted)
        {
            if (existing is null)
                return;
            using var session = documentStore.OpenAsyncSession();
            var item = await session.LoadAsync<WarehouseItem>(existing.Id!, cancellationToken);
            item.CatalogItemRemoved = true;
            await session.SaveChangesAsync(cancellationToken);
            return;
        }

        var current = change.Current!;
        string itemId;
        if (existing is null)
        {
            var created = await items.CreateAsync(new WarehouseItem
            {
                OrganizationId = link.OrganizationId,
                Sku = current.Sku,
                Name = current.Name,
                CatalogItemId = change.Id,
            }, cancellationToken);
            if (!created.Succeeded)
                return;
            itemId = created.ItemId!;
        }
        else
        {
            if (existing.IsPreItem)
                await items.LinkAsync(existing.Id!, change.Id, current.Sku, cancellationToken);
            itemId = existing.Id!;
        }

        using (var session = documentStore.OpenAsyncSession())
        {
            var item = await session.LoadAsync<WarehouseItem>(itemId, cancellationToken);
            if (item is null)
                return;
            item.Name = current.Name;
            item.CatalogItemId = change.Id;
            item.CatalogItemRemoved = false;
            foreach (var gtin in current.Gtins.Select(Gs1Gtin.Normalize).Where(g => g is not null).Distinct())
            {
                var id = BarcodeRegistration.IdFor(gtin!, link.OrganizationId, itemId);
                if (await session.LoadAsync<BarcodeRegistration>(id, cancellationToken) is null)
                    await session.StoreAsync(new BarcodeRegistration
                    {
                        Id = id,
                        Code = gtin!,
                        CodeType = BarcodeCodeType.Gtin14,
                        Level = PackagingLevel.Each,
                        Quantity = 1,
                        OrganizationId = link.OrganizationId,
                        ItemId = itemId,
                    }, id, cancellationToken);
            }
            await session.SaveChangesAsync(cancellationToken);
        }
    }
}
