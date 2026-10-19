using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Commerce.Checkout;
using MintPlayer.Spark.Commerce.Checkout.Models;
using MintPlayer.Spark.Commerce.Inventory;
using MintPlayer.Spark.Commerce.Inventory.Jobs;
using MintPlayer.Spark.Commerce.Promotions.Models;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Commerce.Promotions.Services;

/// <summary>
/// Promotions' order-event recipient (PRD §4.1): code uses follow the order (extended to the payment deadline when
/// placed, permanent when confirmed, given back when it ends unpaid), a waiting-list offer is marked purchased, the
/// tracking attribution and commission follow the order, and freed capacity re-evaluates the waiting lists. Every step is
/// idempotent (checkout may deliver an event again).
/// </summary>
internal sealed class PromotionOrderEvents(
    IDocumentStore store,
    PromotionUsage usage,
    WaitingLists waitingLists,
    Tracking tracking,
    IOptionsMonitor<SparkCommercePromotionsOptions> options,
    ILogger<PromotionOrderEvents> logger) : IOrderEventRecipient
{
    public async Task OnOrderEventAsync(OrderEvent orderEvent, CancellationToken cancellationToken)
    {
        var order = orderEvent.Order;
        if (order.Id is null)
            return;
        switch (orderEvent.Kind)
        {
            case OrderEventKind.Placed:
                await ForEachCodeAsync(order, (code, owner, buyer) => usage.SetDeadlineAsync(code, owner, buyer, Deadline(order), cancellationToken), cancellationToken);
                await tracking.OnPlacedAsync(order, cancellationToken);
                break;

            case OrderEventKind.Confirmed:
                await ForEachCodeAsync(order, async (code, owner, buyer) =>
                {
                    await usage.SetDeadlineAsync(code, owner, buyer, null, cancellationToken);
                    if (code.WaitingListEntryId is { } entryId)
                        await waitingLists.MarkPurchasedAsync(entryId, cancellationToken);
                }, cancellationToken);
                // An order confirmed late (D3) may never have been placed through this recipient's eyes: attribute it now.
                await tracking.OnPlacedAsync(order, cancellationToken);
                await tracking.OnConfirmedAsync(order, cancellationToken);
                break;

            case OrderEventKind.Cancelled or OrderEventKind.Expired or OrderEventKind.Voided:
                await ForEachCodeAsync(order, (code, owner, buyer) => usage.ReleaseAsync(code, owner, buyer, cancellationToken), cancellationToken);
                await tracking.OnCancelledAsync(order, cancellationToken);
                await waitingLists.EvaluateForItemsAsync(order.Lines.Select(l => l.ItemId), cancellationToken);
                break;

            case OrderEventKind.Refunded:
                var full = order.State == OrderState.Refunded;
                if (full && options.CurrentValue.ReleaseUseOnRefund)
                    await ForEachCodeAsync(order, (code, owner, buyer) => usage.ReleaseAsync(code, owner, buyer, cancellationToken), cancellationToken);
                await tracking.OnReversedAsync(order, orderEvent.Reference ?? "refund", full, cancellationToken);
                await waitingLists.EvaluateForItemsAsync(order.Lines.Select(l => l.ItemId), cancellationToken);
                break;

            case OrderEventKind.LineRefused:
                await tracking.OnReversedAsync(order, orderEvent.Reference ?? "line-refused", false, cancellationToken);
                break;

            case OrderEventKind.DisputeLost:
                await tracking.OnReversedAsync(order, orderEvent.Reference ?? "dispute-lost", true, cancellationToken);
                await waitingLists.EvaluateForItemsAsync(order.Lines.Select(l => l.ItemId), cancellationToken);
                break;
        }
    }

    /// <summary>A placed order's uses live until its payment deadline (a day longer, so a late webhook still finds them).</summary>
    private static DateTimeOffset Deadline(CheckoutOrder order) => (order.PaymentPendingUntil ?? order.CreatedAt).AddDays(1);

    /// <summary>
    /// The codes the order used: the discount codes on its line adjustments and the block codes of its wrapped lines, with
    /// the usage owner the pricing step reserved them under (the placement's lowest line id) and the buyer key.
    /// </summary>
    private async Task ForEachCodeAsync(CheckoutOrder order, Func<PromotionCode, string, string?, Task> action, CancellationToken cancellationToken)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in order.Lines)
        {
            foreach (var adjustment in line.Adjustments.Where(a => a.Kind == PromotionSources.Promotions && a.Code != SalePricePricingStep.AdjustmentCode))
                names.Add(adjustment.Code);
            if (PromotionItemIds.TryParse(line.ItemId, out var blockCode, out _) && PromotionRules.NormalizeCode(blockCode) is { } normalized)
                names.Add(normalized);
        }
        if (names.Count == 0)
            return;
        Dictionary<string, PromotionCode> codes;
        using (var session = store.OpenAsyncSession())
            codes = (await session.LoadAsync<PromotionCode>(names.Select(PromotionCode.IdFor), cancellationToken))
                .Where(p => p.Value is not null).ToDictionary(p => p.Key, p => p.Value);
        var owner = PromotionUsage.OwnerForLines(order.Lines.Select(l => l.LineId));
        var buyer = PromotionUsage.BuyerKey(order.BuyerUserId);
        foreach (var code in codes.Values)
        {
            try
            {
                await action(code, owner, buyer);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Promotion code {Code} on order {OrderId} could not be updated.", code.Code, order.Id);
                throw; // checkout records the event as undelivered and redelivers it
            }
        }
    }
}

/// <summary>A lapsed hold frees capacity (the inventory sweeper, §4.3): the waiting lists of its items re-evaluate.</summary>
internal sealed class PromotionClaimLapsed(WaitingLists waitingLists) : IInventoryClaimLapsed
{
    public Task OnLapsedAsync(InventoryClaim claim, CancellationToken cancellationToken)
        => claim.Items.Count == 0 ? Task.CompletedTask : waitingLists.EvaluateForItemsAsync(claim.Items.Select(i => i.ItemId), cancellationToken);
}
