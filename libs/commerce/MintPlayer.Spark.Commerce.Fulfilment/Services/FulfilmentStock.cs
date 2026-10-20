using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Commerce.Fulfilment.Models;
using MintPlayer.Spark.Commerce.Inventory;
using MintPlayer.Spark.Commerce.Shop.Catalog;
using MintPlayer.Spark.Commerce.Shop.Models;
using MintPlayer.Spark.Commerce.Shop.Orders;
using MintPlayer.Spark.Fulfilment.Contracts;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;

namespace MintPlayer.Spark.Commerce.Fulfilment.Services;

/// <summary>
/// The capacity source from <c>stock-levels</c> (warehouse §4.4, commerce §4.9): each variant's quota capacity is set to
/// <c>OnHandSellable − ReservedOtherChannels − buffer + ShippedTotal + baseline</c> for the variant's channel. Because
/// <c>ShippedTotal</c> rises in the same warehouse transaction that lowers <c>OnHandSellable</c>, a late, reordered or
/// repeated snapshot can never open an oversell window: the capacity is recomputed from the snapshot, never adjusted by a
/// delta, and an older <see cref="FulfilmentStockLevel.Version"/> than the one applied is ignored.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Rule 1 (<c>sold</c> lowered only on a confirmed cancellation) is kept by Checkout's <c>ICheckoutStockFeed</c> seam and
/// <see cref="FulfilmentSync"/>.</item>
/// <item>Rule 2 (one epoch): <see cref="FulfilmentSkuLink.Baseline"/> adds back the units the shop shipped itself before the
/// warehouse took over (<see cref="ISparkCommerceFulfilment.SetBaselineAsync"/>).</item>
/// <item>Rule 3 (bundles): a bundle is an item mapped to its components' variant quotas with multipliers (commerce §4.3,
/// <c>ISparkInventory.MapItemAsync</c>); the feed sets the component quotas, so a bundle sells only what its components
/// allow.</item>
/// <item>Rule 4 (I4 exception): a restock raises <c>OnHandSellable</c> and so the capacity; Checkout restocks nothing for a
/// warehouse line.</item>
/// <item>A capacity below what is sold and held (shrinkage) cannot be set: the quota is closed at sold + held instead, so the
/// shop stops selling until the warehouse's figures rise again (§4.4 "Shrinkage adjustment").</item>
/// </list>
/// </remarks>
internal sealed class FulfilmentStock(
    IDocumentStore store,
    ISparkShopStock shopStock,
    ISparkInventory inventory,
    IOptionsMonitor<SparkCommerceFulfilmentOptions> options,
    TimeProvider clock,
    ILogger<FulfilmentStock> logger)
{
    /// <summary>
    /// The capacity a stock level gives one channel, or null when the level names other channels but not this one (the
    /// reservations of the others cannot be told apart then; the shop stops selling rather than guess). Pure.
    /// </summary>
    public static int? Capacity(FulfilmentStockLevel level, string channel, int buffer, int baseline)
    {
        decimal reserved = 0, shipped = 0;
        if (level.Channels.Count > 0)
        {
            if (level.Channels.FirstOrDefault(c => string.Equals(c.Channel, channel, StringComparison.OrdinalIgnoreCase)) is not { } mine)
                return null;
            reserved = mine.ReservedOtherChannels;
            shipped = mine.ShippedTotal;
        }
        var capacity = level.OnHandSellable - reserved - Math.Max(0, buffer) + shipped + Math.Max(0, baseline);
        return capacity <= 0 ? 0 : (int)Math.Floor(capacity);
    }

    /// <summary>Applies a stock level to its variant's quota; <paramref name="force"/> re-applies an already applied version (a baseline or buffer change).</summary>
    public async Task<bool> ApplyAsync(FulfilmentStockLevel level, bool force, CancellationToken cancellationToken)
    {
        using var session = store.OpenAsyncSession();
        session.Advanced.OptimisticConcurrencyMode = Raven.Client.Documents.Session.OptimisticConcurrencyMode.Writes;
        var link = await session.LoadAsync<FulfilmentSkuLink>(FulfilmentIds.Sku(level.OwnerId, level.Sku), cancellationToken);
        if (link is null)
        {
            logger.LogDebug("Stock level of SKU {Sku} (owner {OwnerId}) names no variant this shop published; ignored.", level.Sku, level.OwnerId);
            return false;
        }
        if (!force && link.AppliedVersion is { } applied && level.Version <= applied)
            return false; // an older (or the same) snapshot: superseded by Version (§4.4)

        var o = options.CurrentValue;
        var capacity = Capacity(level, link.Channel, o.BufferOf(link.Channel), link.Baseline);
        if (capacity is null)
            logger.LogWarning("Stock level of SKU {Sku} (owner {OwnerId}) has no figures for channel {Channel}; the variant stops selling until it has.", level.Sku, level.OwnerId, link.Channel);
        var target = capacity ?? 0;
        if (!await shopStock.SetStockAsync(link.VariantId, target, cancellationToken))
        {
            // Below what is sold and held now: close the quota there (stop selling), never below.
            var availability = await inventory.GetAvailabilityAsync(ShopQuotas.Variant(link.VariantId), cancellationToken);
            target = availability.Sold + availability.Held;
            if (!await shopStock.SetStockAsync(link.VariantId, target, cancellationToken))
                logger.LogWarning("The quota of variant {VariantId} could not be closed at {Capacity}; the next stock level retries.", link.VariantId, target);
        }
        link.AppliedVersion = level.Version;
        link.AppliedCapacity = target;
        link.AppliedAt = clock.GetUtcNow();
        await session.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>Re-applies every stock level whose version was not applied yet (the sweep; the ETL transport's only path).</summary>
    public async Task<int> SweepAsync(CancellationToken cancellationToken)
    {
        var levels = new List<FulfilmentStockLevel>();
        using (var session = store.OpenAsyncSession())
        {
            var query = session.Query<FulfilmentStockLevel>();
            await using var stream = await session.Advanced.StreamAsync(query, cancellationToken);
            while (await stream.MoveNextAsync())
                levels.Add(stream.Current.Document);
        }
        var applied = 0;
        foreach (var batch in levels.Chunk(128))
        {
            Dictionary<string, FulfilmentSkuLink?> links;
            using (var session = store.OpenAsyncSession())
                links = (await session.LoadAsync<FulfilmentSkuLink>(batch.Select(l => FulfilmentIds.Sku(l.OwnerId, l.Sku)).Distinct(StringComparer.Ordinal), cancellationToken))
                    .ToDictionary(kv => kv.Key, kv => (FulfilmentSkuLink?)kv.Value, StringComparer.Ordinal);
            foreach (var level in batch)
            {
                if (!links.TryGetValue(FulfilmentIds.Sku(level.OwnerId, level.Sku), out var link) || link is null)
                    continue;
                if (link.AppliedVersion is { } version && level.Version <= version)
                    continue;
                try
                {
                    if (await ApplyAsync(level, force: false, cancellationToken))
                        applied++;
                }
                catch (Raven.Client.Exceptions.ConcurrencyException)
                {
                    // A notification applied it meanwhile.
                }
            }
        }
        return applied;
    }

    /// <summary>Sets a variant's onboarding baseline (§4.4 rule 2) and re-applies its stock level.</summary>
    public async Task SetBaselineAsync(string variantId, int units, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(units);
        FulfilmentStockLevel? level;
        using (var session = store.OpenAsyncSession())
        {
            var variant = await session.Include<ShopVariant>(v => v.ProductId).LoadAsync<ShopVariant>(variantId, cancellationToken)
                ?? throw new InvalidOperationException($"Variant '{variantId}' does not exist.");
            var product = await session.LoadAsync<ShopProduct>(variant.ProductId, cancellationToken)
                ?? throw new InvalidOperationException($"The product of variant '{variantId}' does not exist.");
            var ownerId = options.CurrentValue.OwnerOf(product.OrganizationId);
            var link = await session.LoadAsync<FulfilmentSkuLink>(FulfilmentIds.Sku(ownerId, variant.Sku), cancellationToken)
                ?? throw new InvalidOperationException($"Variant '{variantId}' was not published to the warehouse yet (no catalog item for SKU '{variant.Sku}').");
            link.Baseline = units;
            await session.SaveChangesAsync(cancellationToken);
            // LINQ: values are query parameters, never spliced into RQL.
            level = await session.Query<FulfilmentStockLevel>()
                .Customize(c => c.WaitForNonStaleResults(TimeSpan.FromSeconds(15)))
                .Where(s => s.OwnerId == ownerId && s.Sku == variant.Sku)
                .FirstOrDefaultAsync(cancellationToken);
        }
        if (level is not null)
            await ApplyAsync(level, force: true, cancellationToken);
    }
}
