using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Commerce.Checkout;
using MintPlayer.Spark.Commerce.Inventory;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Commerce.Promotions.Services;

/// <summary>What an item's public sale has left now: item units available and units held on its quotas.</summary>
internal sealed record ItemAvailability(int Available, int Held);

/// <summary>
/// Looks items up the way checkout does: through the registered <see cref="ICheckoutCapacitySource"/>s (Shop,
/// Ticketing, the host), never by loading app documents by id. Promotions' own block source is skipped, so a wrapped id
/// never resolves to itself.
/// </summary>
internal sealed class PromotionItems(IServiceProvider services, ISparkInventory inventory, IDocumentStore store)
{
    /// <summary>The item as checkout would sell <paramref name="quantity"/> of it now, or null.</summary>
    public async Task<CheckoutItem?> ResolveAsync(string itemId, int quantity, CancellationToken cancellationToken)
    {
        if (PromotionItemIds.TryParse(itemId, out _, out _))
            return null;
        var source = services.GetServices<ICheckoutCapacitySource>()
            .FirstOrDefault(s => s is not PromotionBlockCapacitySource && s.Handles(itemId));
        return source is null ? null : await source.ResolveAsync(new CheckoutItemRequest(itemId, Math.Max(1, quantity)), cancellationToken);
    }

    /// <summary>
    /// The public availability of <paramref name="item"/> in item units (the tightest of its quotas, divided by the
    /// multiplier), and the units held on those quotas. An item with numbered units or no capacity has none to report.
    /// </summary>
    public async Task<ItemAvailability?> AvailabilityAsync(CheckoutItem item, CancellationToken cancellationToken)
    {
        if (item.HasNumberedUnits || item.Capacity.IsEmpty)
            return null;
        // The capacity of ONE unit: quota lines straight, item lines through their mapping.
        var perUnit = new List<(string QuotaId, int N)>();
        foreach (var q in item.Capacity.Quotas)
            perUnit.Add((q.QuotaId, Math.Max(1, q.N)));
        if (item.Capacity.Items.Count > 0)
        {
            using var session = store.OpenAsyncSession();
            foreach (var line in item.Capacity.Items)
            {
                var mapping = await session.LoadAsync<InventoryItem>(InventoryEngine.ItemDocumentId(line.ItemId), cancellationToken);
                if (mapping is null)
                    return null;
                perUnit.AddRange(mapping.Quotas.Select(m => (m.QuotaId, Math.Max(1, m.Multiplier))));
            }
        }
        if (perUnit.Count == 0)
            return null;

        var available = int.MaxValue;
        var held = 0;
        foreach (var (quotaId, n) in perUnit)
        {
            var a = await inventory.GetAvailabilityAsync(quotaId, cancellationToken);
            available = Math.Min(available, Math.Max(0, a.Available) / n);
            held += a.Held;
        }
        return new ItemAvailability(available, held);
    }
}
