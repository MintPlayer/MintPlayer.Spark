using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Commerce.Checkout.Models;
using MintPlayer.Spark.Commerce.Inventory;
using MintPlayer.Spark.Commerce.Payments;
using MintPlayer.Spark.Commerce.Payments.Models;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;
using Raven.Client.Exceptions;

namespace MintPlayer.Spark.Commerce.Checkout.Services;

/// <summary>
/// The order state machine's driver (§4.4): confirmation for all three allocation modes, late payments (D3), race losers
/// and <c>Basket:OnLineFailure</c> (D1, D40), card-fingerprint limits (D29), unpaid ends, refunds and disputes (GB1).
/// Every handler is idempotent: payment webhooks are redelivered, and every engine call is keyed by claim or return id.
/// </summary>
internal sealed class OrderLifecycle(
    IDocumentStore store,
    ISparkInventory inventory,
    ISparkPayments payments,
    OrderEvents events,
    CheckoutMails mails,
    CheckoutInsurance insurance,
    ICommerceAudit audit,
    BasketStore baskets,
    IOptionsMonitor<SparkCommerceOptions> commerce,
    IOptionsMonitor<SparkCommerceCheckoutOptions> options,
    TimeProvider clock,
    ILogger<OrderLifecycle> logger)
{
    private const int MaxAttempts = 5;

    // ---- the unit of work ----------------------------------------------------------------------------------------

    /// <summary>
    /// Loads the order, applies <paramref name="change"/> and saves with optimistic concurrency, retrying on a conflict
    /// (engine calls inside are idempotent, so a retry repeats them harmlessly). Events are published after the save.
    /// </summary>
    public async Task<CheckoutOrder?> UpdateAsync(string orderId, Func<IAsyncDocumentSession, CheckoutOrder, List<OrderEvent>, Task> change, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            using var session = store.OpenAsyncSession();
            session.Advanced.OptimisticConcurrencyMode = OptimisticConcurrencyMode.Writes;
            var order = await session.LoadAsync<CheckoutOrder>(orderId, cancellationToken);
            if (order is null)
                return null;
            var raised = new List<OrderEvent>();
            await change(session, order, raised);
            try
            {
                await session.SaveChangesAsync(cancellationToken);
            }
            catch (ConcurrencyException) when (attempt < MaxAttempts)
            {
                continue;
            }
            foreach (var e in raised)
                await events.PublishAsync(e, cancellationToken);
            return order;
        }
    }

    private DateTimeOffset Now => clock.GetUtcNow();

    /// <summary>A read session (orders are written by ordinary sessions only).</summary>
    public IAsyncDocumentSession OpenSession() => store.OpenAsyncSession();

    // ---- payments ------------------------------------------------------------------------------------------------

    /// <summary>A payment changed (the webhook queue, a poll, the sweep). The payment's reference is the order id.</summary>
    public Task OnPaymentAsync(ProviderPayment payment, CancellationToken cancellationToken)
        => UpdateAsync(payment.Reference, async (session, order, raised) =>
        {
            var record = order.Payments.FirstOrDefault(p => p.PaymentId == payment.Id);
            if (record is null)
                return; // not a payment of ours for this order
            record.Status = payment.Status.ToString();
            if (record.Supplement)
            {
                order.Log.Add(new CheckoutOrderLogEntry { At = Now, Event = "supplement:" + payment.Status, Detail = payment.Id });
                return;
            }

            var isCurrent = order.Payment?.PaymentId == payment.Id;
            if (isCurrent && order.Payment is not null)
                order.Payment.Status = record.Status;

            switch (payment.Status)
            {
                case ProviderPaymentStatus.Paid or ProviderPaymentStatus.Authorized:
                    if (!isCurrent && (OrderStateMachine.IsSold(order.State) || order.State is OrderState.Refunding or OrderState.Refunded))
                    {
                        await RefundSuperfluousAsync(order, record, payment, cancellationToken);
                        return;
                    }
                    if (order.State is OrderState.PaymentPending or OrderState.Expired or OrderState.Cancelled)
                    {
                        // A payment for an order already ended by another attempt becomes the current one (late payment, D3).
                        order.Payment = record;
                        await ConfirmAsync(session, order, payment, raised, cancellationToken);
                    }
                    else if (OrderStateMachine.IsSold(order.State) && payment.Status == ProviderPaymentStatus.Paid && record.ManualCapture)
                    {
                        // The capture we asked for landed: nothing to do.
                    }
                    break;

                case ProviderPaymentStatus.Canceled or ProviderPaymentStatus.Expired or ProviderPaymentStatus.Failed:
                    if (isCurrent && order.State == OrderState.PaymentPending)
                        await EndUnpaidAsync(order, payment.Status == ProviderPaymentStatus.Expired ? OrderState.Expired : OrderState.Cancelled,
                            "payment " + payment.Status, raised, cancelProvider: false, cancellationToken);
                    break;
            }
        }, cancellationToken);

    /// <summary>
    /// Converts every line of a paid order. Held lines convert their claim; a lapsed one re-claims the <b>same</b> units
    /// (late payment, D3/D16); OnPayment lines claim and convert now (D1). The card fingerprint, known only now, is
    /// checked as a late limit (D29). What cannot be sold follows <c>Basket:OnLineFailure</c> (D40).
    /// </summary>
    internal async Task ConfirmAsync(IAsyncDocumentSession session, CheckoutOrder order, ProviderPayment? payment, List<OrderEvent> raised, CancellationToken cancellationToken)
    {
        var now = Now;
        var late = order.State != OrderState.PaymentPending;
        var lost = new List<CheckoutOrderLine>();
        var cardRefused = false;

        foreach (var line in order.Lines.Where(l => l.Status == OrderLineStatus.Active))
        {
            var lateLimits = payment?.PayerFingerprint is { Length: > 0 } fingerprint && line.LimitMax is { } max && line.LimitScope is { } scope
                ? InventoryLimitKeys.For(scope, max, line.Quantity, new BuyerIdentifiers(CardFingerprint: fingerprint))
                : null;
            var result = await ConvertLineAsync(order, line, lateLimits, now, cancellationToken);
            if (result.Succeeded)
                continue;
            if (OrderClaims.IsCardLimit(result))
            {
                cardRefused = true;
                break;
            }
            line.StatusReason = (late ? "late payment: " : "") + result.Status;
            lost.Add(line);
        }

        // GB2: an add-on goes with its parent.
        foreach (var child in order.Lines.Where(l => l.Status == OrderLineStatus.Active && l.ParentLineId is not null && lost.Any(p => p.LineId == l.ParentLineId) && !lost.Contains(l)).ToList())
        {
            child.StatusReason = "parent refused";
            lost.Add(child);
        }

        if (cardRefused)
        {
            order.Log.Add(new CheckoutOrderLogEntry { At = now, Event = "card-limit", Detail = "The card fingerprint exceeds a per-buyer limit (D29)." });
            await UnwindAsync(order, payment, ReturnReason.Cancelled, "card fingerprint limit", raised, cancellationToken);
            return;
        }

        var policy = commerce.CurrentValue.Basket.OnLineFailure;
        var active = order.Lines.Where(l => l.Status == OrderLineStatus.Active).ToList();
        if (lost.Count > 0 && (lost.Count == active.Count || policy == BasketLineFailurePolicy.VoidOrder))
        {
            foreach (var line in lost)
                line.Status = OrderLineStatus.Refused;
            await UnwindAsync(order, payment, ReturnReason.RefusedLine, lost.Count == active.Count ? "nothing could be sold" : "VoidOrder", raised, cancellationToken);
            return;
        }

        Money refundForLines = Money.Zero(order.Currency);
        foreach (var line in lost)
        {
            line.Status = OrderLineStatus.Refused;
            if (line.ClaimId is { } claimId)
                await inventory.ReturnAsync(claimId, new InventoryReturn(order.Id + ":refused:" + line.LineId, ReturnReason.RefusedLine), cancellationToken);
            refundForLines += line.Total;
        }

        // Capture what is sold, or refund the refused lines (RefundLine, the default; donations stay: GC3).
        if (payment is not null)
        {
            if (payment.Status == ProviderPaymentStatus.Authorized)
                await payments.CaptureAsync(payment.Id!, refundForLines.IsZero ? null : payment.Amount - refundForLines, cancellationToken);
            else if (!refundForLines.IsZero)
                await payments.RefundAsync(payment.Id!, refundForLines, order.Id + ":refused-lines", cancellationToken: cancellationToken);
        }

        var previous = OrderStateMachine.Move(order, OrderState.Confirmed, now, late ? "late payment re-claimed" : null);
        order.ConfirmedAt = now;
        order.PaymentPendingUntil = null;
        await audit.RecordAsync(session, new CommerceAuditEntry
        {
            Action = CheckoutAuditActions.OrderConfirmed, SubjectId = order.Id!, OrganizationId = order.Sellers.FirstOrDefault(),
            After = order.Total, At = now, Reason = late ? "late payment" : null,
        }, cancellationToken);
        foreach (var line in lost)
            raised.Add(new OrderEvent(order, OrderEventKind.LineRefused, null, line.LineId));
        raised.Add(new OrderEvent(order, OrderEventKind.Confirmed, previous));

        await BindInsuranceAsync(order, cancellationToken);
        await ClearBasketAsync(order, cancellationToken);
        await mails.SendConfirmationAsync(order, cancellationToken);
        order.ConfirmationSentAt ??= now;
    }

    /// <summary>Converts one line; answers the engine's result (Held = sold).</summary>
    private async Task<ClaimResult> ConvertLineAsync(CheckoutOrder order, CheckoutOrderLine line, IReadOnlyList<LimitLine>? lateLimits, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (line.Capacity.IsEmpty)
            return ClaimResult.Ok;
        var hold = now + options.CurrentValue.ConfirmationHold;

        if (line.ClaimId is null)
        {
            // OnPayment (D1): claim and convert now.
            line.ClaimId = OrderClaims.ClaimIdFor(line.LineId);
            var claimed = await inventory.TryClaimAsync(OrderClaims.Request(line.ClaimId, order.Id!, hold, line.Capacity, line.Limits), cancellationToken);
            if (!claimed.Succeeded)
            {
                line.ClaimId = null;
                return claimed;
            }
            line.Units = claimed.Units?.ToList() ?? [];
        }

        var converted = await inventory.ConvertAsync(line.ClaimId, lateLimits, cancellationToken);
        if (converted.Succeeded || converted.Status == ClaimStatus.LimitExceeded)
            return converted;

        // A lapsed or released hold: re-claim the same units (late payment, D3/D16), then convert.
        if (converted.Status is ClaimStatus.Expired or ClaimStatus.InvalidState or ClaimStatus.NotFound)
        {
            var target = OrderClaims.Request(line.ClaimId, order.Id!, hold, OrderClaims.SameUnits(line.Capacity, line.Units), line.Limits);
            var reclaimed = converted.Status == ClaimStatus.Expired
                ? await inventory.AdjustAsync(target, cancellationToken)
                : await ReclaimReleasedAsync(line, target, cancellationToken);
            if (!reclaimed.Succeeded)
                return reclaimed;
            return await inventory.ConvertAsync(line.ClaimId!, lateLimits, cancellationToken);
        }
        return converted;
    }

    /// <summary>A released claim cannot be adjusted: a fresh claim id takes the same units (still exactly once per id).</summary>
    private async Task<ClaimResult> ReclaimReleasedAsync(CheckoutOrderLine line, ClaimRequest target, CancellationToken cancellationToken)
    {
        var claim = await inventory.GetClaimAsync(line.ClaimId!, cancellationToken);
        if (claim is not null && claim.State == ClaimState.Converted)
            return ClaimResult.Ok;
        var fresh = OrderClaims.ClaimIdFor(line.LineId) + "/late";
        var result = await inventory.TryClaimAsync(target with { ClaimId = fresh }, cancellationToken);
        if (result.Succeeded)
            line.ClaimId = fresh;
        return result;
    }

    /// <summary>Gives everything back and voids (authorized) or refunds (captured) the whole payment.</summary>
    private async Task UnwindAsync(CheckoutOrder order, ProviderPayment? payment, ReturnReason reason, string detail, List<OrderEvent> raised, CancellationToken cancellationToken)
    {
        foreach (var line in order.Lines.Where(l => l.ClaimId is not null))
            await inventory.ReleaseAsync(line.ClaimId!, reason, cancellationToken: cancellationToken);

        OrderState target;
        if (payment is null)
            target = OrderState.Voided;
        else if (payment.Status == ProviderPaymentStatus.Authorized)
        {
            await payments.VoidAsync(payment.Id!, cancellationToken);
            target = OrderState.Voided;
        }
        else
        {
            await payments.RefundAsync(payment.Id!, payment.Amount, order.Id + ":unwind", cancellationToken: cancellationToken);
            target = OrderState.Refunding;
        }
        var previous = OrderStateMachine.Move(order, target, Now, detail);
        order.PaymentPendingUntil = null;
        raised.Add(new OrderEvent(order, target == OrderState.Voided ? OrderEventKind.Voided : OrderEventKind.RefundRequested, previous));
    }

    /// <summary>A second payment for an order already paid: refunded (or voided), never kept (research §4).</summary>
    private async Task RefundSuperfluousAsync(CheckoutOrder order, CheckoutOrderPayment record, ProviderPayment payment, CancellationToken cancellationToken)
    {
        if (record.Superfluous)
            return;
        record.Superfluous = true;
        if (payment.Status == ProviderPaymentStatus.Authorized)
            await payments.VoidAsync(payment.Id!, cancellationToken);
        else
            await payments.RefundAsync(payment.Id!, payment.Amount, order.Id + ":superfluous:" + payment.Id, cancellationToken: cancellationToken);
        order.Log.Add(new CheckoutOrderLogEntry { At = Now, Event = "superfluous-payment", Detail = payment.Id });
    }

    /// <summary>
    /// The order ends without payment: holds go back (a HoldFromSelection hold still alive returns to the basket,
    /// "basket kept while its own holds live", §4.4), the provider payment is cancelled actively (D3).
    /// </summary>
    internal async Task EndUnpaidAsync(CheckoutOrder order, OrderState target, string detail, List<OrderEvent> raised, bool cancelProvider, CancellationToken cancellationToken)
    {
        if (order.State != OrderState.PaymentPending)
            return;
        var now = Now;
        foreach (var line in order.Lines.Where(l => l.ClaimId is not null && l.Status == OrderLineStatus.Active))
        {
            if (order.Origin == OrderOrigin.Online && line.Allocation == AllocationMode.HoldFromSelection && line.BasketHoldUntil is { } until && until > now)
            {
                var back = await inventory.ExtendAsync(line.ClaimId!, until, cancellationToken);
                if (back.Succeeded)
                    continue;
            }
            await inventory.ReleaseAsync(line.ClaimId!, target == OrderState.Expired ? ReturnReason.Expired : ReturnReason.PaymentFailed, cancellationToken: cancellationToken);
        }

        if (cancelProvider && order.Payment?.PaymentId is { } paymentId)
        {
            try { await payments.CancelAsync(paymentId, cancellationToken); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Cancel is best effort: a payment that completes anyway lands as a late payment (D3).
                logger.LogWarning(ex, "Cancelling payment {PaymentId} of order {OrderId} failed.", paymentId, order.Id);
            }
        }

        var previous = OrderStateMachine.Move(order, target, now, detail);
        order.PaymentPendingUntil = null;
        raised.Add(new OrderEvent(order, target == OrderState.Expired ? OrderEventKind.Expired : OrderEventKind.Cancelled, previous));
        if (BasketIdOf(order) is { } basketId)
            await baskets.TouchAsync(basketId, b =>
            {
                if (b.PendingOrderId == order.Id)
                    b.PendingOrderId = null;
                // Lines whose hold did not survive keep their place but lose the claim.
                foreach (var line in b.Lines)
                {
                    var orderLine = order.Lines.FirstOrDefault(l => l.LineId == line.LineId);
                    if (orderLine is not null && line.HeldUntil is { } held && held <= now)
                    {
                        line.ClaimId = null;
                        line.HeldUntil = null;
                    }
                }
            }, cancellationToken);
    }

    // ---- refunds -------------------------------------------------------------------------------------------------

    public Task OnRefundAsync(RefundChangeNotice notice, CancellationToken cancellationToken)
        => UpdateAsync(notice.Payment.Reference, (session, order, raised) =>
        {
            if (order.Payment?.PaymentId != notice.Payment.Id && !order.Payments.Any(p => p.PaymentId == notice.Payment.Id && p.Superfluous))
                return Task.CompletedTask;
            if (order.Payments.Any(p => p.PaymentId == notice.Payment.Id && p.Superfluous))
                return Task.CompletedTask; // a superfluous payment's refund changes nothing on the order

            var refund = notice.Refund;
            if (refund.Status == ProviderRefundStatus.Failed)
            {
                // RefundFailed is raised once per refund: a ledger refund (M9) raises it on its own transition into
                // Failed (RefundPaymentRecipient → RefundEngine); any other refund here, once, however often the notice comes.
                var ledgerRefund = Refunds.RefundEngine.TryParseReference(refund.Reference, out _, out _);
                var known = order.Log.Any(l => l.Event == "refund-failed" && l.Detail is { } d && d.StartsWith(refund.Reference + " ", StringComparison.Ordinal));
                if (!ledgerRefund && !known)
                {
                    order.Log.Add(new CheckoutOrderLogEntry { At = Now, Event = "refund-failed", Detail = refund.Reference + " " + refund.FailureReason });
                    raised.Add(new OrderEvent(order, OrderEventKind.RefundFailed, null, refund.Reference));
                }
                if (order.State == OrderState.Refunding && notice.Payment.AmountRefunded is { IsZero: false })
                    OrderStateMachine.TryMove(order, OrderState.PartiallyRefunded, Now, "refund failed (D20)");
                return Task.CompletedTask;
            }
            if (refund.Status != ProviderRefundStatus.Succeeded)
                return Task.CompletedTask;

            order.AmountRefunded = notice.Payment.AmountRefunded;
            var full = order.AmountRefunded is { } refunded && refunded >= order.Total;
            var previous = order.State;
            var target = full ? OrderState.Refunded : OrderState.PartiallyRefunded;
            if (order.State is OrderState.Disputed or OrderState.DisputeLost or OrderState.Voided)
                return Task.CompletedTask;
            if (OrderStateMachine.TryMove(order, target, Now, "refund " + refund.Reference))
                raised.Add(new OrderEvent(order, OrderEventKind.Refunded, previous, refund.Reference));
            return Task.CompletedTask;
        }, cancellationToken);

    // ---- disputes (GB1) ------------------------------------------------------------------------------------------

    /// <summary>
    /// GB1: an open dispute moves the order to Disputed (Tickets freezes its tickets on the event); won → DisputeWon
    /// (unfrozen); lost → what is still releasable goes back, exactly once, keyed by the dispute id (I4): lines whose
    /// service date passed return nothing, shipped goods return no quota until restock.
    /// </summary>
    public Task OnDisputeAsync(DisputeChangeNotice notice, CancellationToken cancellationToken)
    {
        var dispute = notice.Dispute;
        if (dispute.Reference is not { Length: > 0 } reference)
            return Task.CompletedTask;
        return UpdateAsync(reference, async (session, order, raised) =>
        {
            var now = Now;
            var record = order.Disputes.FirstOrDefault(d => d.DisputeId == dispute.Id);
            if (record is null)
            {
                record = new CheckoutOrderDispute { DisputeId = dispute.Id!, Amount = dispute.Amount, OpenedAt = now };
                order.Disputes.Add(record);
            }
            record.Status = dispute.Status.ToString();

            switch (dispute.Status)
            {
                case DisputeStatus.NeedsResponse or DisputeStatus.UnderReview:
                    if (order.State != OrderState.Disputed)
                    {
                        var before = order.State;
                        if (OrderStateMachine.TryMove(order, OrderState.Disputed, now, "dispute " + dispute.Id))
                        {
                            order.StateBeforeDispute = before;
                            raised.Add(new OrderEvent(order, OrderEventKind.Disputed, before, dispute.Id));
                        }
                    }
                    break;

                case DisputeStatus.Won:
                    record.ClosedAt ??= now;
                    if (OrderStateMachine.TryMove(order, OrderState.DisputeWon, now, "dispute won " + dispute.Id))
                        raised.Add(new OrderEvent(order, OrderEventKind.DisputeWon, OrderState.Disputed, dispute.Id));
                    break;

                case DisputeStatus.Lost:
                    record.ClosedAt ??= now;
                    if (order.State != OrderState.Disputed)
                        OrderStateMachine.TryMove(order, OrderState.Disputed, now, "dispute " + dispute.Id);
                    if (!record.Returned)
                    {
                        foreach (var line in order.Lines.Where(l => l.Status == OrderLineStatus.Active && l.ClaimId is not null))
                        {
                            if (line.ServiceDate is { } service && service <= now)
                                continue; // a past event: nothing is releasable
                            await inventory.ReturnAsync(line.ClaimId!, new InventoryReturn(dispute.Id + ":" + line.LineId, ReturnReason.DisputeLost,
                                ReturnStock: line.ShippedAt is null), cancellationToken);
                            line.Status = OrderLineStatus.Returned;
                            line.StatusReason = "dispute lost";
                        }
                        record.Returned = true;
                    }
                    if (OrderStateMachine.TryMove(order, OrderState.DisputeLost, now, "dispute lost " + dispute.Id))
                    {
                        await audit.RecordAsync(session, new CommerceAuditEntry
                        {
                            Action = CheckoutAuditActions.DisputeLost, SubjectId = order.Id!, OrganizationId = dispute.SellerOrganizationId,
                            Before = order.Total, After = order.Total - dispute.Amount, At = now, Reason = dispute.Reason,
                        }, cancellationToken);
                        raised.Add(new OrderEvent(order, OrderEventKind.DisputeLost, OrderState.Disputed, dispute.Id));
                    }
                    break;
            }
        }, cancellationToken);
    }

    // ---- helpers -------------------------------------------------------------------------------------------------

    private async Task BindInsuranceAsync(CheckoutOrder order, CancellationToken cancellationToken)
    {
        if (order.InsurancePolicyId is not null || order.Extras.FirstOrDefault(e => e.Kind == PricedExtraKind.Insurance) is not { Reference: { } quoteId } extra)
            return;
        if (insurance.Provider is not { } provider)
            return;
        try
        {
            var policy = await provider.BindAsync(new InsuranceBindRequest(quoteId, order.Id!, order.BuyerEmail, order.BuyerName, extra.Amount), cancellationToken);
            order.InsurancePolicyId = policy.PolicyId;
            order.Log.Add(new CheckoutOrderLogEntry { At = Now, Event = "insurance-bound", Detail = policy.Provider + " " + policy.PolicyId });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Binding insurance quote {QuoteId} for order {OrderId} failed.", quoteId, order.Id);
            order.Log.Add(new CheckoutOrderLogEntry { At = Now, Event = "insurance-bind-failed", Detail = quoteId });
        }
    }

    private async Task ClearBasketAsync(CheckoutOrder order, CancellationToken cancellationToken)
    {
        if (BasketIdOf(order) is not { } basketId)
            return;
        var sold = order.Lines.Select(l => l.LineId).ToHashSet();
        await baskets.TouchAsync(basketId, b =>
        {
            b.Lines.RemoveAll(l => sold.Contains(l.LineId));
            if (b.PendingOrderId == order.Id)
                b.PendingOrderId = null;
            b.Choices.Clear();
        }, cancellationToken);
    }

    internal static string? BasketIdOf(CheckoutOrder order)
        => order.Origin != OrderOrigin.Online ? null
            : order.BuyerUserId is { } user ? CheckoutBasket.IdForUser(user)
            : order.VisitorId is { } visitor ? CheckoutBasket.IdForVisitor(visitor) : null;
}

/// <summary>Checkout's audit actions (money-moving changes, default 8), prefixed with the alias.</summary>
public static class CheckoutAuditActions
{
    public const string OrderPlaced = "commerce-checkout.order-placed";
    public const string OrderConfirmed = "commerce-checkout.order-confirmed";
    public const string MarkedPaid = "commerce-checkout.order-marked-paid";
    public const string CompIssued = "commerce-checkout.comp-issued";
    public const string LineChanged = "commerce-checkout.line-changed";
    public const string DisputeLost = "commerce-checkout.dispute-lost";
}
