using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Commerce.Checkout.Contracts;
using MintPlayer.Spark.Commerce.Checkout.Models;
using MintPlayer.Spark.Commerce.Inventory;
using MintPlayer.Spark.Commerce.Payments;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Commerce.Checkout.Services;

/// <summary>
/// GB7: orders staff make without the buyer's checkout. Members of the seller with the back-office role
/// (<c>box-office</c>) create phone orders (a payment link is mailed), manual orders (marked paid by <c>finance</c>,
/// audited), comps and guest-list entries (zero total, counted against quotas and per-buyer limits like any order,
/// excluded from revenue), re-send confirmations, and change a seat or upgrade a line.
/// </summary>
internal sealed class BackOfficeOrders(
    IDocumentStore store,
    OrderAccess access,
    CheckoutItems items,
    CheckoutPricing pricing,
    OrderLifecycle lifecycle,
    OrderEvents events,
    CheckoutMails mails,
    ISparkInventory inventory,
    ISparkPayments payments,
    ICommerceAudit audit,
    IOptionsMonitor<SparkCommerceOptions> commerce,
    IOptionsMonitor<SparkCommerceCheckoutOptions> options,
    TimeProvider clock)
{
    private DateTimeOffset Now => clock.GetUtcNow();

    public async Task<SparkBackOfficeResult> CreateAsync(SparkBackOfficeOrderRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (access.CallerId is not { } me || !await access.HasRoleAsync(request.OrganizationId, options.CurrentValue.BackOfficeRole, cancellationToken))
            throw CheckoutException.Of(CheckoutRefusals.Forbidden);
        var origin = request.Origin?.ToLowerInvariant() switch
        {
            "phone" => OrderOrigin.Phone,
            "manual" => OrderOrigin.Manual,
            "comp" => OrderOrigin.Comp,
            "guestlist" => OrderOrigin.GuestList,
            _ => throw CheckoutException.Of(CheckoutRefusals.InvalidState, subject: request.Origin),
        };
        if (request.Lines is not { Count: > 0 })
            throw CheckoutException.Of(CheckoutRefusals.BasketEmpty);
        var email = request.BuyerEmail?.Trim() is { Length: > 0 } given ? given : null;
        if (email is null && origin is OrderOrigin.Phone or OrderOrigin.Manual)
            throw CheckoutException.Of(CheckoutRefusals.EmailRequired);
        var now = Now;

        // Resolve and price.
        var resolved = new List<(string LineId, CheckoutItem Item, CommerceChannelOptions Channel, int Quantity)>();
        foreach (var line in request.Lines)
        {
            var lineId = Guid.NewGuid().ToString("N");
            var item = await items.ResolveAsync(new CheckoutItemRequest(line.ItemId, line.Quantity, line.Units, Options: line.Options), lineId, cancellationToken);
            if (item.OrganizationId != request.OrganizationId)
                throw CheckoutException.Of(CheckoutRefusals.SellerMismatch, lineId);
            var channel = commerce.CurrentValue.Channels.TryGetValue(item.Channel, out var c) ? c : throw CheckoutException.Of(CheckoutRefusals.UnknownChannel, lineId, item.Channel);
            resolved.Add((lineId, item, channel, line.Quantity));
        }
        var minimumAge = resolved.Max(r => r.Item.MinimumAge);
        if (minimumAge is { } age && (request.AttestedMinimumAge is not { } attested || attested < age))
            throw CheckoutException.Of(CheckoutRefusals.AgeAttestationRequired);

        var priced = await pricing.PriceAsync(resolved.Select(r => new PricingInput(r.LineId, r.Item, r.Quantity)).ToList(),
            new Dictionary<string, string>(), null, placing: true, cancellationToken, buyerEmail: email);
        var identifiers = new BuyerIdentifiers(Email: email);
        var free = origin is OrderOrigin.Comp or OrderOrigin.GuestList;

        var order = new CheckoutOrder
        {
            Id = "CheckoutOrders/" + Guid.NewGuid().ToString("N"),
            Number = CheckoutService.NewNumber(),
            State = OrderState.PaymentPending,
            Origin = origin,
            BuyerEmail = email,
            BuyerName = request.BuyerName,
            Currency = priced.Currency,
            Sellers = [request.OrganizationId],
            ExcludedFromRevenue = free,
            GuestList = free ? request.GuestList : null,
            BackOffice = new CheckoutBackOffice { CreatedBy = me, Note = request.Note },
            CreatedAt = now,
            UpdatedAt = now,
        };
        foreach (var p in priced.Lines)
        {
            var r = resolved.First(x => x.LineId == p.LineId);
            var line = CheckoutService.ToOrderLine(p, r.Item, r.Channel, null, identifiers);
            if (free)
            {
                // Comps keep the face value for reference but cost nothing (D6); they still count against quotas and limits.
                line.Total = Money.Zero(order.Currency);
                line.Net = Money.Zero(order.Currency);
                line.Vat = Money.Zero(order.Currency);
                line.OrderDiscount = Money.Zero(order.Currency);
                line.Fees.Clear();
                line.Adjustments.Clear();
            }
            order.Lines.Add(line);
        }
        order.Total = free ? Money.Zero(order.Currency) : priced.Total;
        if (minimumAge is { } min)
            order.AgeAttestation = new CheckoutAgeAttestation { MinimumAge = min, AttestedAt = now, AttestedBy = me };
        order.Log.Add(new CheckoutOrderLogEntry { At = now, Event = "back-office:" + origin, ActorId = me, Detail = request.Note });

        // Claims: comps convert at once; phone and manual orders hold for the payment window.
        var window = resolved.Min(r => r.Channel.MaxPaymentWindow);
        var expiresAt = free ? now + options.CurrentValue.ConfirmationHold : now + window;
        var claimed = new List<string>();
        try
        {
            foreach (var line in order.Lines.Where(l => !l.Capacity.IsEmpty))
            {
                var claimId = OrderClaims.ClaimIdFor(line.LineId);
                var result = await inventory.TryClaimAsync(OrderClaims.Request(claimId, order.Id, expiresAt, line.Capacity, line.Limits), cancellationToken);
                if (!result.Succeeded)
                    throw OrderClaims.Refusal(result, line.LineId);
                claimed.Add(claimId);
                line.ClaimId = claimId;
                line.Units = result.Units?.ToList() ?? line.Units;
            }

            string? paymentUrl = null;
            if (!free)
            {
                order.PaymentPendingUntil = expiresAt;
                if (origin == OrderOrigin.Phone && !order.Total.IsZero)
                {
                    var methods = payments.GetOfferableMethods(new PaymentMethodQuery { Held = true, MaxPaymentWindow = window });
                    if (methods.Count == 0)
                        throw CheckoutException.Of(CheckoutRefusals.NoMethod);
                    var payment = await payments.CreatePaymentAsync(new PaymentCreation
                    {
                        Reference = order.Id, SellerOrganizationId = request.OrganizationId, Amount = order.Total,
                        Description = "Order " + order.Number, AllowedMethods = methods.Select(m => m.Id).ToList(),
                        ExpiresAt = expiresAt, RedirectUrl = mails.OrderPath(order), CustomerEmail = email,
                    }, cancellationToken);
                    order.Payment = new CheckoutOrderPayment
                    {
                        PaymentId = payment.Id, Provider = payment.Provider, Status = payment.Status.ToString(), Amount = order.Total,
                        CheckoutUrl = payment.CheckoutUrl, CreatedAt = now,
                    };
                    order.Payments.Add(order.Payment);
                    paymentUrl = payment.CheckoutUrl;
                }
            }

            var raised = new List<OrderEvent>();
            using (var session = store.OpenAsyncSession())
            {
                await session.StoreAsync(order, order.Id, cancellationToken);
                raised.Add(new OrderEvent(order, OrderEventKind.Placed, null));
                if (free || order.Total.IsZero)
                {
                    await lifecycle.ConfirmAsync(session, order, payment: null, raised, cancellationToken);
                    await audit.RecordAsync(session, new CommerceAuditEntry
                    {
                        Action = CheckoutAuditActions.CompIssued, SubjectId = order.Id, OrganizationId = request.OrganizationId,
                        After = order.Total, ActorId = me, At = now, Reason = request.GuestList ?? origin.ToString(),
                    }, cancellationToken);
                }
                await session.SaveChangesAsync(cancellationToken);
            }
            if (paymentUrl is not null)
            {
                await mails.SendPaymentLinkAsync(order, paymentUrl, cancellationToken);
                order.BackOffice!.PaymentLinkSentAt = now;
            }
            foreach (var e in raised)
                await events.PublishAsync(e, cancellationToken);
            return new SparkBackOfficeResult(order.Id, order.Number, order.State.ToString(), null, paymentUrl);
        }
        catch
        {
            foreach (var claimId in claimed)
                await inventory.ReleaseAsync(claimId, ReturnReason.Cancelled, cancellationToken: CancellationToken.None);
            throw;
        }
    }

    /// <summary>Finance marks a manual order paid (outside the provider): confirmed, with an audit entry naming who and the payment reference.</summary>
    public async Task<SparkBackOfficeResult> MarkPaidAsync(SparkMarkPaidRequest request, CancellationToken cancellationToken)
    {
        if (access.CallerId is not { } me)
            throw CheckoutException.Of(CheckoutRefusals.Forbidden);
        if (string.IsNullOrWhiteSpace(request.PaymentReference))
            throw CheckoutException.Of(CheckoutRefusals.InvalidState, subject: "paymentReference");
        CheckoutOrder? result = null;
        var allowed = true;
        result = await lifecycle.UpdateAsync(request.OrderId, async (session, order, raised) =>
        {
            if (!await access.MayHandleAsync(order, options.CurrentValue.MarkPaidRole, cancellationToken))
            {
                allowed = false;
                return;
            }
            if (order.Origin != OrderOrigin.Manual || order.State is not (OrderState.PaymentPending or OrderState.Expired))
                throw CheckoutException.Of(CheckoutRefusals.InvalidState, subject: order.State.ToString());
            order.BackOffice ??= new CheckoutBackOffice { CreatedBy = me };
            order.BackOffice.MarkedPaidBy = me;
            order.BackOffice.MarkedPaidAt = Now;
            order.BackOffice.PaymentReference = request.PaymentReference;
            order.Log.Add(new CheckoutOrderLogEntry { At = Now, Event = "marked-paid", ActorId = me, Detail = request.PaymentReference + (request.Note is null ? "" : " " + request.Note) });
            await audit.RecordAsync(session, new CommerceAuditEntry
            {
                Action = CheckoutAuditActions.MarkedPaid, SubjectId = order.Id!, OrganizationId = order.Sellers.FirstOrDefault(),
                After = order.Total, ActorId = me, At = Now, Reason = request.Note,
                Data = new Dictionary<string, string> { ["paymentReference"] = request.PaymentReference },
            }, cancellationToken);
            await lifecycle.ConfirmAsync(session, order, payment: null, raised, cancellationToken);
        }, cancellationToken);
        if (result is null || !allowed)
            throw CheckoutException.Of(CheckoutRefusals.OrderNotFound);
        return new SparkBackOfficeResult(result.Id!, result.Number, result.State.ToString(), null, null);
    }

    /// <summary>Re-sends the confirmation (Tickets re-sends its tickets on the event).</summary>
    public async Task<SparkBackOfficeResult> ResendAsync(string orderId, CancellationToken cancellationToken)
    {
        var allowed = true;
        var result = await lifecycle.UpdateAsync(orderId, async (session, order, raised) =>
        {
            if (!await access.MayHandleAsync(order, options.CurrentValue.BackOfficeRole, cancellationToken))
            {
                allowed = false;
                return;
            }
            if (!OrderStateMachine.IsSold(order.State))
                throw CheckoutException.Of(CheckoutRefusals.InvalidState, subject: order.State.ToString());
            order.BackOffice ??= new CheckoutBackOffice { CreatedBy = access.CallerId! };
            order.BackOffice.ResendCount++;
            order.Log.Add(new CheckoutOrderLogEntry { At = Now, Event = "resent", ActorId = access.CallerId });
            await mails.SendConfirmationAsync(order, cancellationToken);
            raised.Add(new OrderEvent(order, OrderEventKind.ConfirmationResent, null, order.BackOffice.ResendCount.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }, cancellationToken);
        if (result is null || !allowed)
            throw CheckoutException.Of(CheckoutRefusals.OrderNotFound);
        return new SparkBackOfficeResult(result.Id!, result.Number, result.State.ToString(), null, null);
    }

    /// <summary>
    /// A seat change or upgrade (GB7): one inventory exchange (<see cref="ISparkInventory.ExchangeAsync"/>) returns the
    /// old units (keyed by the change id, I4), claims the new ones and sells them in a single cluster-wide transaction
    /// (§4.4), so a refused change leaves the line exactly as it was. A seat kept by an upgrade is handed over, not
    /// re-claimed. The price difference is refunded, or charged through a supplement payment link.
    /// </summary>
    public async Task<SparkBackOfficeResult> ChangeLineAsync(SparkLineChangeRequest request, CancellationToken cancellationToken)
    {
        var allowed = true;
        Money? difference = null;
        string? paymentUrl = null;
        var changeId = Guid.NewGuid().ToString("N")[..12]; // outside the retried unit of work: the engine steps are keyed by it
        var result = await lifecycle.UpdateAsync(request.OrderId, async (session, order, raised) =>
        {
            if (!await access.MayHandleAsync(order, options.CurrentValue.BackOfficeRole, cancellationToken))
            {
                allowed = false;
                return;
            }
            if (!OrderStateMachine.IsSold(order.State) || order.State == OrderState.Disputed)
                throw CheckoutException.Of(CheckoutRefusals.InvalidState, subject: order.State.ToString());
            var line = order.Lines.FirstOrDefault(l => l.LineId == request.LineId && l.Status == OrderLineStatus.Active)
                ?? throw CheckoutException.Of(CheckoutRefusals.LineNotFound, request.LineId);

            var item = await items.ResolveAsync(new CheckoutItemRequest(request.NewItemId ?? line.ItemId, line.Quantity, request.Units), line.LineId, cancellationToken);
            if (item.OrganizationId != line.SellerOrganizationId)
                throw CheckoutException.Of(CheckoutRefusals.SellerMismatch, line.LineId);
            var channel = commerce.CurrentValue.GetChannel(item.Channel);

            var newClaim = OrderClaims.ClaimIdFor(line.LineId) + "/" + changeId;
            var capacity = CheckoutCapacitySnapshot.From(item.Capacity);

            var target = OrderClaims.Request(newClaim, order.Id!, Now + options.CurrentValue.ConfirmationHold, capacity, line.Limits);
            ClaimResult converted;
            if (line.ClaimId is { } oldClaim)
            {
                // One cluster-wide transaction (§4.4): the old units back, the new ones claimed and sold, or nothing.
                converted = await inventory.ExchangeAsync(new InventoryExchange(oldClaim, new InventoryReturn("change:" + changeId, ReturnReason.Other), target), cancellationToken);
            }
            else
            {
                // A line without a claim (nothing to give back): claim and sell the new units.
                converted = await inventory.TryClaimAsync(target, cancellationToken);
                if (converted.Succeeded)
                    converted = await inventory.ConvertAsync(newClaim, cancellationToken: cancellationToken);
            }
            if (!converted.Succeeded)
                throw OrderClaims.Refusal(converted, line.LineId);

            var repriced = (await pricing.PriceAsync([new PricingInput(line.LineId, item, line.Quantity)], new Dictionary<string, string>(), null, true, cancellationToken)).Lines[0];
            var comp = order.ExcludedFromRevenue;
            var newTotal = comp ? Money.Zero(order.Currency) : repriced.Total;
            // S21: the line keeps its share of the order discount (the order's discount extra does not change), capped at
            // the new goods amount; the difference is between what was charged and what is charged now.
            var newGoods = newTotal.ToMinorUnits() - (comp ? 0 : repriced.Price.MandatoryFees.Sum(f => f.Amount.ToMinorUnits()));
            var newShare = comp ? 0 : Math.Max(line.OrderDiscount.ToMinorUnits(), -Math.Max(0, newGoods));
            var newDiscount = Money.FromMinorUnits(newShare, order.Currency);
            var diff = newTotal + newDiscount - line.Charged();
            difference = diff;

            var oldUnits = string.Join(" ", line.Units);
            line.ItemId = item.ItemId;
            line.Title = item.Title;
            line.Channel = item.Channel;
            line.Allocation = channel.Allocation;
            line.UnitPrice = item.Price;
            line.ClaimId = newClaim;
            line.Units = converted.Units?.ToList() ?? [.. item.Capacity.Units];
            line.Capacity = capacity;
            line.Total = newTotal;
            line.OrderDiscount = newDiscount;
            if (!comp)
            {
                line.Net = repriced.Net ?? repriced.Price.Net;
                line.Vat = repriced.Vat ?? repriced.Price.Vat;
                if (newShare != 0)
                {
                    // The VAT base is the new goods amount plus the kept share (the same rule CheckoutLineVat applies).
                    var vat = repriced.Item.VatRateBasisPoints is { } rate ? CheckoutLineVat.Vat(newGoods + newShare, rate, repriced.Item.PriceBasis) : 0;
                    line.Net = Money.FromMinorUnits(newGoods + newShare - vat, order.Currency);
                    line.Vat = Money.FromMinorUnits(vat, order.Currency);
                }
                line.Fees = repriced.Price.MandatoryFees.Select(f => new CheckoutOrderFee { Code = f.Code, Kind = f.Kind.ToString(), Amount = f.Amount, LabelKey = f.LabelKey, Mandatory = true }).ToList();
            }
            line.MinimumAge = item.MinimumAge;
            line.IdCheckRequired = item.MinimumAge is not null;
            order.Total += diff;
            order.Log.Add(new CheckoutOrderLogEntry { At = Now, Event = "line-changed", ActorId = access.CallerId, Detail = $"{line.LineId}: {oldUnits} → {string.Join(" ", line.Units)}, difference {CheckoutMails.Format(diff)}" });

            if (diff.ToMinorUnits() < 0 && order.Payment?.PaymentId is { } paid)
                await payments.RefundAsync(paid, -diff, order.Id + ":change:" + changeId, cancellationToken: cancellationToken);
            else if (diff.ToMinorUnits() > 0)
            {
                var supplement = await payments.CreatePaymentAsync(new PaymentCreation
                {
                    Reference = order.Id!, Attempt = order.Payments.Count, SellerOrganizationId = line.SellerOrganizationId, Amount = diff,
                    Description = "Order " + order.Number + " change", RedirectUrl = mails.OrderPath(order), CustomerEmail = order.BuyerEmail,
                }, cancellationToken);
                order.Payments.Add(new CheckoutOrderPayment
                {
                    PaymentId = supplement.Id, Provider = supplement.Provider, Attempt = order.Payments.Count, Status = supplement.Status.ToString(),
                    Amount = diff, CheckoutUrl = supplement.CheckoutUrl, CreatedAt = Now, Supplement = true,
                });
                paymentUrl = supplement.CheckoutUrl;
            }

            await audit.RecordAsync(session, new CommerceAuditEntry
            {
                Action = CheckoutAuditActions.LineChanged, SubjectId = order.Id!, OrganizationId = line.SellerOrganizationId,
                Before = order.Total - diff, After = order.Total, ActorId = access.CallerId, At = Now, Reason = changeId,
            }, cancellationToken);
            raised.Add(new OrderEvent(order, OrderEventKind.LineChanged, null, changeId));
        }, cancellationToken);
        if (result is null || !allowed)
            throw CheckoutException.Of(CheckoutRefusals.OrderNotFound);
        if (paymentUrl is not null)
            await mails.SendPaymentLinkAsync(result, paymentUrl, cancellationToken);
        return new SparkBackOfficeResult(result.Id!, result.Number, result.State.ToString(), difference, paymentUrl);
    }
}
