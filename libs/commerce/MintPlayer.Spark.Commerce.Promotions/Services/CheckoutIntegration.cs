using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authentication;
using MintPlayer.Spark.Commerce.Checkout;
using MintPlayer.Spark.Commerce.Inventory;
using MintPlayer.Spark.Commerce.Promotions.Models;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Commerce.Promotions.Services;

// Promotions plugs into checkout through checkout's own extension points only (PRD §4.1 coupling rule):
// a pricing step for sale prices (50), a pricing step for codes (100), an admission gate for unlock and presale codes,
// and a capacity source for capacity-blocking codes. Checkout references none of these types.

/// <summary>
/// GA3: an active seller sale price, applied as a line adjustment <c>sale</c> (the difference between the final price at
/// the sale price and at the regular price). Runs before the codes, so a percentage code is taken off the sale price.
/// Skipped when the source already priced the sale (<see cref="ISparkSalePrices.ApplyAsync"/>).
/// </summary>
internal sealed class SalePricePricingStep(
    IDocumentStore store,
    IFinalPriceCalculator calculator,
    TimeProvider clock,
    IOptionsMonitor<SparkCommercePromotionsOptions> options) : ICheckoutPricingStep
{
    public const string AdjustmentCode = "sale";

    public int Order => 50;

    public async Task ApplyAsync(CheckoutPricingContext context, CancellationToken cancellationToken)
    {
        if (!options.CurrentValue.SalePrices.ApplyInPricingStep)
            return;
        var now = clock.GetUtcNow();
        using var session = store.OpenAsyncSession();
        var sales = new Dictionary<string, PromotionSalePrice?>(StringComparer.Ordinal);
        foreach (var line in context.Lines)
        {
            var itemId = PromotionItemIds.Inner(line.Item.ItemId);
            if (!sales.TryGetValue(itemId, out var sale))
                sales[itemId] = sale = (await SalePrices.LoadAllAsync(session, itemId, cancellationToken)).FirstOrDefault(s => s.IsActiveAt(now));
            if (sale is null || sale.SalePrice.Currency != line.Item.Price.Currency || sale.SalePrice >= line.Item.Price)
                continue;
            var atSale = calculator.Calculate(new FinalPriceRequest(line.Item.Channel, sale.SalePrice, line.Item.PriceBasis, line.Item.VatRateBasisPoints, line.Quantity));
            var atRegular = calculator.Calculate(new FinalPriceRequest(line.Item.Channel, line.Item.Price, line.Item.PriceBasis, line.Item.VatRateBasisPoints, line.Quantity));
            var difference = atSale.Total - atRegular.Total;
            if (difference.ToMinorUnits() < 0)
                line.Adjustments.Add(new PriceAdjustment(AdjustmentCode, difference, "commercePromotions.sale", PromotionSources.Promotions));
        }
    }
}

/// <summary>Who added an adjustment: the <see cref="PriceAdjustment.Source"/> promotions writes (the order line's fee <c>Kind</c>).</summary>
internal static class PromotionSources
{
    public const string Promotions = "promotions";
}

/// <summary>
/// The codes (§4.6): the code the buyer entered (choice <c>promotionCode</c>) and the block codes of wrapped lines.
/// A percentage or fixed discount is spread pro rata over the eligible lines as line adjustments (so it defines partial
/// refunds and VAT bases, D19). Usage limits and once-per-buyer are checked on every quote and <b>reserved</b> on the
/// final pricing of a placement (<see cref="CheckoutPricingContext.Placing"/>), keyed by the placement's lowest line id
/// with a deadline, so a placement that fails later leaves a reservation that lapses on its own; the order-event
/// recipient extends it to the payment deadline and makes it permanent on confirmation.
/// </summary>
internal sealed class PromotionCodePricingStep(
    IDocumentStore store,
    PromotionUsage usage,
    TimeProvider clock,
    IOptionsMonitor<SparkCommercePromotionsOptions> options) : ICheckoutPricingStep
{
    public int Order => 100;

    public async Task ApplyAsync(CheckoutPricingContext context, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var blockLines = new List<(PricedLine Line, string Code)>();
        foreach (var line in context.Lines)
        {
            if (PromotionItemIds.TryParse(line.Item.ItemId, out var blockCode, out _))
                blockLines.Add((line, blockCode));
        }
        string? entered = null;
        if (context.Choices.TryGetValue(PromotionChoices.Code, out var choice) && !string.IsNullOrWhiteSpace(choice))
            entered = PromotionRules.NormalizeCode(choice) ?? throw CheckoutException.Of(PromotionRefusals.CodeInvalid, subject: PromotionChoices.Code);
        if (entered is null && blockLines.Count == 0)
            return;

        var ids = blockLines.Select(b => PromotionCode.IdFor(b.Code)).Append(entered is null ? null : PromotionCode.IdFor(entered))
            .Where(id => id is not null).Select(id => id!).Distinct().ToList();
        Dictionary<string, PromotionCode?> codes;
        using (var session = store.OpenAsyncSession())
            codes = (await session.LoadAsync<PromotionCode>(ids, cancellationToken)).ToDictionary(p => p.Key, p => (PromotionCode?)p.Value);

        var used = new List<PromotionCode>();

        // Block codes: the capacity source already checked them when the line was added; at "Pay" they must still hold.
        foreach (var group in blockLines.GroupBy(b => b.Code))
        {
            var code = codes.GetValueOrDefault(PromotionCode.IdFor(group.Key));
            var first = group.First().Line;
            if (code is null || !PromotionRules.IsLive(code, now) || code.Effect != PromotionEffect.CapacityBlock)
                throw CheckoutException.Of(PromotionRefusals.CodeInvalid, first.LineId, group.Key);
            if (code.AssignedToUserId is not null)
            {
                if (context.BuyerUserId is null)
                    throw CheckoutException.Of(PromotionRefusals.SignInRequired, first.LineId, group.Key);
                if (!string.Equals(code.AssignedToUserId, context.BuyerUserId, StringComparison.Ordinal))
                    throw CheckoutException.Of(PromotionRefusals.NotYours, first.LineId, group.Key);
            }
            if (code.MaxQuantityPerOrder is { } maxQuantity && group.Sum(b => b.Line.Quantity) > maxQuantity)
                throw CheckoutException.Of(PromotionRefusals.QuantityExceeded, first.LineId, group.Key);
            used.Add(code);
        }

        if (entered is not null)
        {
            var code = codes.GetValueOrDefault(PromotionCode.IdFor(entered));
            if (code is null || !PromotionRules.IsLive(code, now))
                throw CheckoutException.Of(PromotionRefusals.CodeInvalid, subject: PromotionChoices.Code);
            if (code.AssignedToUserId is not null && !string.Equals(code.AssignedToUserId, context.BuyerUserId, StringComparison.Ordinal))
                throw CheckoutException.Of(PromotionRefusals.NotYours, subject: PromotionChoices.Code);
            if (code.Effect is PromotionEffect.PercentageOff or PromotionEffect.FixedAmountOff)
            {
                ApplyDiscount(context, code);
                if (used.All(u => u.Id != code.Id))
                    used.Add(code);
            }
            // An unlock or block code entered as a choice changes no price: it acts at the gate or through its item.
        }

        await CheckUsageAsync(context, used, now, cancellationToken);
    }

    /// <summary>Takes the code's discount off the eligible lines, pro rata to their goods amounts (§4.6, D19).</summary>
    internal static void ApplyDiscount(CheckoutPricingContext context, PromotionCode code)
    {
        var eligible = context.Lines.Where(l => PromotionRules.Applies(code, l.Item.ItemId, l.Item.OrganizationId)).ToList();
        if (eligible.Count == 0)
            throw CheckoutException.Of(PromotionRefusals.CodeNotApplicable, subject: PromotionChoices.Code);
        // The goods amount of a line: its gross price plus earlier adjustments (a sale), never the mandatory fees.
        var amounts = eligible.Select(l => l.Price.Gross + Money.Sum(l.Adjustments.Select(a => a.Amount), context.Currency)).ToList();
        var goods = Money.Sum(amounts.Select(a => a.ToMinorUnits() < 0 ? Money.Zero(context.Currency) : a), context.Currency);
        if (code.MinimumOrderTotal is { } minimum && (minimum.Currency != context.Currency || goods < minimum))
            throw CheckoutException.Of(PromotionRefusals.MinimumNotReached, subject: PromotionChoices.Code);
        if (code.Effect == PromotionEffect.FixedAmountOff && code.Amount?.Currency != context.Currency)
            throw CheckoutException.Of(PromotionRefusals.CodeNotApplicable, subject: PromotionChoices.Code);

        var discount = PromotionRules.Discount(code, goods);
        var parts = PromotionRules.SpreadProRata(discount, amounts);
        for (var i = 0; i < eligible.Count; i++)
        {
            if (!parts[i].IsZero)
                eligible[i].Adjustments.Add(new PriceAdjustment(code.Code, parts[i], "commercePromotions.discount", PromotionSources.Promotions));
        }
    }

    private async Task CheckUsageAsync(CheckoutPricingContext context, List<PromotionCode> used, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (used.Count == 0)
            return;
        var owner = PromotionUsage.OwnerForLines(context.Lines.Select(l => l.LineId));
        var buyerKey = PromotionUsage.BuyerKey(context.BuyerUserId);
        foreach (var code in used.Where(c => c.MaxUses is not null || c.OncePerBuyer))
        {
            if (code.OncePerBuyer && buyerKey is null)
                throw CheckoutException.Of(PromotionRefusals.SignInRequired, subject: code.Code);
            if (!context.Placing)
            {
                if (!await usage.HasRoomAsync(code, owner, cancellationToken))
                    throw CheckoutException.Of(PromotionRefusals.CodeExhausted, subject: code.Code);
                if (code.OncePerBuyer && await usage.UsedByBuyerAsync(code, buyerKey!, owner, cancellationToken))
                    throw CheckoutException.Of(PromotionRefusals.CodeAlreadyUsed, subject: code.Code);
                continue;
            }
            var result = await usage.ReserveAsync(code, owner, buyerKey, now + options.CurrentValue.UsageReservation, cancellationToken);
            switch (result)
            {
                case UsageResult.Exhausted: throw CheckoutException.Of(PromotionRefusals.CodeExhausted, subject: code.Code);
                case UsageResult.AlreadyUsedByBuyer: throw CheckoutException.Of(PromotionRefusals.CodeAlreadyUsed, subject: code.Code);
                case UsageResult.Conflict: throw CheckoutException.Of(CheckoutRefusals.Conflict, subject: code.Code);
            }
        }
    }
}

/// <summary>
/// Unlock and presale codes (§4.6, D17) at checkout's admission gate: a capacity source (Shop for hidden products,
/// Ticketing.Events for a presale phase) gives the item the admission scope <c>promotions:{scope}</c>, and the buyer
/// presents the code as the admission token. The gate admits when the code is live, opens that scope (or the item), and
/// has a use left; with <see cref="AdmissionCheck.Consume"/> it takes the use (once per buyer and item: re-adding the
/// item does not take another).
/// </summary>
internal sealed class PromotionAdmissionGate(IDocumentStore store, PromotionUsage usage, TimeProvider clock) : ICheckoutAdmissionGate
{
    public bool Handles(string scope) => scope.StartsWith(PromotionItemIds.Prefix, StringComparison.Ordinal);

    public async Task<AdmissionDecision> CheckAsync(AdmissionCheck check, CancellationToken cancellationToken)
    {
        if (PromotionRules.NormalizeCode(check.Token) is not { } normalized)
            return AdmissionDecision.Refuse(PromotionRefusals.CodeInvalid);
        PromotionCode? code;
        using (var session = store.OpenAsyncSession())
            code = await session.LoadAsync<PromotionCode>(PromotionCode.IdFor(normalized), cancellationToken);
        var now = clock.GetUtcNow();
        if (code is null || code.Effect != PromotionEffect.Unlock || !PromotionRules.IsLive(code, now))
            return AdmissionDecision.Refuse(PromotionRefusals.CodeInvalid);
        var scope = check.Scope[PromotionItemIds.Prefix.Length..];
        if (!code.Scopes.Contains(scope, StringComparer.Ordinal) && !code.ItemIds.Contains(check.ItemId, StringComparer.Ordinal))
            return AdmissionDecision.Refuse(PromotionRefusals.CodeNotApplicable);
        if (code.AssignedToUserId is not null && !string.Equals(code.AssignedToUserId, check.UserId, StringComparison.Ordinal))
            return AdmissionDecision.Refuse(PromotionRefusals.NotYours);
        if (code.MaxUses is null && !code.OncePerBuyer)
            return AdmissionDecision.Admit;

        var buyer = check.UserId is { Length: > 0 } user ? "user:" + user : check.VisitorId is { Length: > 0 } visitor ? "visitor:" + visitor : null;
        if (buyer is null)
            return AdmissionDecision.Refuse(PromotionRefusals.SignInRequired);
        var owner = $"admission:{buyer}:{check.ItemId}";
        if (!check.Consume)
        {
            if (!await usage.HasRoomAsync(code, owner, cancellationToken))
                return AdmissionDecision.Refuse(PromotionRefusals.CodeExhausted);
            return code.OncePerBuyer && await usage.UsedByBuyerAsync(code, buyer, owner, cancellationToken)
                ? AdmissionDecision.Refuse(PromotionRefusals.CodeAlreadyUsed)
                : AdmissionDecision.Admit;
        }
        var result = await usage.ReserveAsync(code, owner, buyer, code.ValidUntil ?? now.AddYears(10), cancellationToken);
        if (result != UsageResult.Reserved)
            return AdmissionDecision.Refuse(result == UsageResult.AlreadyUsedByBuyer ? PromotionRefusals.CodeAlreadyUsed : PromotionRefusals.CodeExhausted);
        // An admission is spent when it is consumed: the use never lapses.
        await usage.SetDeadlineAsync(code, owner, buyer, null, cancellationToken);
        return AdmissionDecision.Admit;
    }
}

/// <summary>
/// Capacity-blocking codes (§4.6) and waiting-list offers: the buyer adds <c>promotions:{CODE}|{itemId}</c>; this source
/// resolves the inner item through the other sources and makes the line claim from the code's block quota instead of the
/// public quotas. Numbered units (seats) cannot be blocked this way. The code is the admission: a waiting-room scope on
/// the inner item is dropped (the list only activates once the queue closed).
/// </summary>
internal sealed class PromotionBlockCapacitySource(
    IDocumentStore store,
    PromotionItems items,
    ISparkCurrentUser currentUser,
    TimeProvider clock) : ICheckoutCapacitySource
{
    public bool Handles(string itemId) => PromotionItemIds.TryParse(itemId, out _, out _);

    public async Task<CheckoutItem?> ResolveAsync(CheckoutItemRequest request, CancellationToken cancellationToken)
    {
        if (!PromotionItemIds.TryParse(request.ItemId, out var raw, out var innerId) || PromotionRules.NormalizeCode(raw) is not { } normalized)
            return null;
        PromotionCode? code;
        PromotionBlock? block = null;
        using (var session = store.OpenAsyncSession())
        {
            code = await session.LoadAsync<PromotionCode>(PromotionCode.IdFor(normalized), cancellationToken);
            if (code?.BlockId is { } blockId)
                block = await session.LoadAsync<PromotionBlock>(blockId, cancellationToken);
        }
        if (code is null || block is null || code.Effect != PromotionEffect.CapacityBlock || !PromotionRules.IsLive(code, clock.GetUtcNow()))
            return null;
        if (block.ClosedAt is not null || !string.Equals(block.ItemId, innerId, StringComparison.Ordinal))
            return null;
        if (code.AssignedToUserId is not null && !string.Equals(code.AssignedToUserId, currentUser.Id, StringComparison.Ordinal))
            return null;
        if (code.MaxQuantityPerOrder is { } max && request.Quantity > max)
            return null;

        var inner = await items.ResolveAsync(innerId, request.Quantity, cancellationToken);
        if (inner is null || inner.HasNumberedUnits || inner.Capacity.IsEmpty)
            return null;
        return inner with
        {
            ItemId = request.ItemId,
            Capacity = new CheckoutCapacity { Quotas = [new QuotaLine(block.QuotaId, request.Quantity)] },
            AdmissionScope = null,
        };
    }
}
