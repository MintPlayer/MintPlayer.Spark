using MintPlayer.Spark.Commerce.Checkout.Models;
using MintPlayer.Spark.Commerce.Inventory;

namespace MintPlayer.Spark.Commerce.Checkout.Services;

/// <summary>
/// D1 OnPayment: "soft availability check only, nothing claimed" (PRD §4.4). At "Pay", a line of an OnPayment channel is
/// refused when its capacity visibly is not there any more, so the buyer is not sent to pay for something that will be
/// refused at confirmation. Nothing is held, so it can still lose the race at confirmation (the refused-line rule
/// applies there). Unknown quotas or items and chain/pick capacity are not judged (the confirmation decides).
/// </summary>
internal static class SoftAvailability
{
    /// <summary>Whether <paramref name="capacity"/> looks available now.</summary>
    public static async Task<bool> LooksAvailableAsync(ISparkInventory inventory, CheckoutCapacitySnapshot capacity, CancellationToken cancellationToken)
    {
        var need = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var quota in capacity.Quotas)
            need[quota.QuotaId] = need.GetValueOrDefault(quota.QuotaId) + quota.N;
        foreach (var item in capacity.Items)
        {
            if (await inventory.GetItemMappingAsync(item.ItemId, cancellationToken) is not { } mapping)
                continue;
            foreach (var quota in mapping)
                need[quota.QuotaId] = need.GetValueOrDefault(quota.QuotaId) + (long)item.N * quota.Multiplier;
        }
        foreach (var (quotaId, n) in need)
        {
            QuotaAvailability availability;
            try { availability = await inventory.GetAvailabilityAsync(quotaId, cancellationToken); }
            catch (InvalidOperationException) { continue; } // an unknown quota: the confirmation decides
            if (availability.Available < n)
                return false;
        }
        if (capacity.Units.Count > 0)
        {
            var states = await inventory.GetUnitStatesAsync(capacity.Units, cancellationToken);
            if (states.Values.Any(s => s != UnitState.Free))
                return false;
        }
        return true;
    }
}
