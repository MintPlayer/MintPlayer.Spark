using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Commerce.Fulfilment.Models;
using MintPlayer.Spark.Commerce.Shop.Models;
using MintPlayer.Spark.Fulfilment.Contracts;
using MintPlayer.Spark.Replication.Abstractions;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Commerce.Fulfilment.Services;

/// <summary>
/// Publishes <c>catalog-items</c> (warehouse §4.3, WD9): every goods variant with a SKU in a channel the warehouse ships,
/// one document per (owner, SKU), stored by the shop as its own published document and pushed through replication. A
/// discontinued variant stays published, marked so (the warehouse may still hold stock). Alongside, the shop's
/// <see cref="FulfilmentSkuLink"/> maps the warehouse's (owner, SKU) back to the variant whose quota the stock feed sets.
/// </summary>
internal sealed class FulfilmentCatalog(
    IDocumentStore store,
    IReplicaPublisher publisher,
    IOptionsMonitor<SparkCommerceFulfilmentOptions> options,
    ILogger<FulfilmentCatalog> logger)
{
    private const int PageSize = 256;

    /// <summary>Publishes the catalog items of <paramref name="variantIds"/> (changed ones only).</summary>
    public async Task<int> PublishAsync(IEnumerable<string> variantIds, CancellationToken cancellationToken)
    {
        var ids = variantIds.Where(id => !string.IsNullOrEmpty(id)).Distinct(StringComparer.Ordinal).ToList();
        if (ids.Count == 0)
            return 0;
        using var session = store.OpenAsyncSession();
        var variants = await session.Include<ShopVariant>(v => v.ProductId).LoadAsync<ShopVariant>(ids, cancellationToken);
        var changed = await PublishAsync(session, variants.Values.OfType<ShopVariant>().ToList(), cancellationToken);
        await session.SaveChangesAsync(cancellationToken);
        return changed;
    }

    /// <summary>Walks every variant (by id, a page at a time) and publishes what changed. The catalog sweep.</summary>
    public async Task<int> SweepAsync(CancellationToken cancellationToken)
    {
        var changed = 0;
        string? after = null;
        while (!cancellationToken.IsCancellationRequested)
        {
            using var session = store.OpenAsyncSession();
            // LoadStartingWith pages by id without an index; startAfter keeps the walk stable while documents change.
            var page = (await session.Advanced.LoadStartingWithAsync<ShopVariant>("ShopVariants/", pageSize: PageSize, startAfter: after, token: cancellationToken)).ToList();
            if (page.Count == 0)
                break;
            await session.LoadAsync<ShopProduct>(page.Select(v => v.ProductId).Distinct(StringComparer.Ordinal), cancellationToken);
            changed += await PublishAsync(session, page, cancellationToken);
            await session.SaveChangesAsync(cancellationToken);
            after = page[^1].Id;
            if (page.Count < PageSize)
                break;
        }
        return changed;
    }

    private async Task<int> PublishAsync(IAsyncDocumentSession session, IReadOnlyList<ShopVariant> variants, CancellationToken cancellationToken)
    {
        var o = options.CurrentValue;
        var changed = 0;
        foreach (var variant in variants)
        {
            if (string.IsNullOrWhiteSpace(variant.Sku) || variant.Id is null)
                continue;
            var product = await session.LoadAsync<ShopProduct>(variant.ProductId, cancellationToken);
            if (product is null || product.Kind != EShopProductKind.Goods || !o.Ships(product.Channel))
                continue;
            var ownerId = o.OwnerOf(product.OrganizationId);
            var itemId = FulfilmentIds.CatalogItem(o.ShopName, ownerId, variant.Sku);
            var fresh = FulfilmentContractBuilder.CatalogItem(itemId, ownerId, product, variant);
            var stored = await session.LoadAsync<FulfilmentCatalogItem>(itemId, cancellationToken);
            if (stored is null)
            {
                await session.StoreAsync(fresh, itemId, cancellationToken);
                await publisher.MarkChangedAsync(session, typeof(FulfilmentCatalogItem), itemId, cancellationToken);
                changed++;
            }
            else if (!FulfilmentContractBuilder.Same(stored, fresh))
            {
                stored.Name = fresh.Name;
                stored.Gtins = fresh.Gtins;
                stored.Variant = fresh.Variant;
                stored.Discontinued = fresh.Discontinued;
                await publisher.MarkChangedAsync(session, typeof(FulfilmentCatalogItem), itemId, cancellationToken);
                changed++;
            }

            var skuId = FulfilmentIds.Sku(ownerId, variant.Sku);
            var link = await session.LoadAsync<FulfilmentSkuLink>(skuId, cancellationToken);
            if (link is null)
                await session.StoreAsync(new FulfilmentSkuLink
                {
                    Id = skuId, OwnerId = ownerId, Sku = variant.Sku, VariantId = variant.Id, ProductId = product.Id!, Channel = product.Channel, CatalogItemId = itemId,
                }, skuId, cancellationToken);
            else if (link.VariantId != variant.Id || link.Channel != product.Channel || link.ProductId != product.Id)
            {
                if (link.VariantId != variant.Id)
                    logger.LogWarning("SKU {Sku} of owner {OwnerId} moved from variant {Previous} to {Current}; the stock feed now sets the latter.", variant.Sku, ownerId, link.VariantId, variant.Id);
                link.VariantId = variant.Id;
                link.ProductId = product.Id!;
                link.Channel = product.Channel;
                link.AppliedVersion = null; // re-apply the stock level to the variant it now belongs to
            }
        }
        return changed;
    }
}
