using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Commerce.Checkout.Models;
using MintPlayer.Spark.Commerce.Checkout.Services;
using MintPlayer.Spark.Commerce.Inventory;
using MintPlayer.Spark.Commerce.Payments;
using MintPlayer.Spark.Commerce.Payments.Models;
using MintPlayer.Spark.Services;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Commerce.Checkout.Refunds;

/// <summary>Who and what a refund belongs to (beyond its amounts).</summary>
internal sealed record RefundMeta(string RefundId, string? CreatedBy = null, string? Note = null, DateTimeOffset? DueBy = null,
    string? MassRefundId = null, string? ReturnId = null, string? ClaimId = null);

/// <summary>
/// The one place money and units go back (PRD §4.7, D20, I4). Every call runs inside <see cref="OrderLifecycle.UpdateAsync"/>'s
/// session, next to the order: the plan (<see cref="RefundMath"/>), the inventory returns (exactly once per
/// <c>{orderId}:{refundId}:{lineId}</c>), the provider refund (idempotent by its reference), the ledger, the order's line
/// statuses, a mass refund's counters, and the events (raised after the save). A retry after a crash repeats only
/// idempotent calls.
/// </summary>
internal sealed class RefundEngine(
    ISparkInventory inventory,
    ISparkPayments payments,
    ICommerceAudit audit,
    IEnumerable<IRefundTermsSource> termsSources,
    IEnumerable<IStandardDeliveryCost> deliverySources,
    CheckoutInsurance insurance,
    IOptionsMonitor<SparkCommerceOptions> commerce,
    IOptionsMonitor<SparkCommerceCheckoutOptions> options,
    TimeProvider clock,
    ILogger<RefundEngine> logger)
{
    public DateTimeOffset Now => clock.GetUtcNow();

    public CheckoutRefundOptions Options => options.CurrentValue.Refunds;

    public TimeZoneInfo Zone => SparkTimeZones.FindOrUtc(Options.TimeZone);

    /// <summary>The states in which an order has something to give back.</summary>
    public static bool IsRefundable(OrderState state)
        => state is OrderState.Confirmed or OrderState.PartiallyRefunded or OrderState.DisputeWon;

    public static string NewId(string prefix) => prefix + "-" + Guid.NewGuid().ToString("N")[..12];

    /// <summary>A stable id derived from a key (a mass refund's refund id per order).</summary>
    public static string StableId(string prefix, string key) => prefix + "-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..16];

    public static string ProviderReference(string orderId, string refundId) => orderId + ":refund:" + refundId;

    public static string InventoryReturnId(string orderId, string refundId, string lineId) => orderId + ":" + refundId + ":" + lineId;

    /// <summary>The refund id a provider reference names, when it is one of ours.</summary>
    public static bool TryParseReference(string? reference, out string orderId, out string refundId)
    {
        orderId = refundId = "";
        if (reference is null)
            return false;
        var at = reference.LastIndexOf(":refund:", StringComparison.Ordinal);
        if (at <= 0)
            return false;
        orderId = reference[..at];
        refundId = reference[(at + ":refund:".Length)..];
        return refundId.Length > 0 && CheckoutPaymentRecipient.IsOrder(orderId);
    }

    // ---- the ledger ------------------------------------------------------------------------------------------------

    /// <summary>
    /// The order's ledger in <paramref name="session"/>, created (terms resolved now) when missing. While nothing was
    /// given back yet its components are rebuilt from the order, so lines refused at confirmation never count.
    /// </summary>
    public async Task<CheckoutRefundLedger> LedgerAsync(IAsyncDocumentSession session, CheckoutOrder order, CancellationToken cancellationToken)
    {
        var id = CheckoutRefundLedger.IdFor(order.Id!);
        var ledger = await session.LoadAsync<CheckoutRefundLedger>(id, cancellationToken);
        if (ledger is null)
        {
            ledger = new CheckoutRefundLedger { Id = id, OrderId = order.Id!, TermsCapturedAt = Now };
            await session.StoreAsync(ledger, id, cancellationToken);
        }
        ledger.OrderNumber = order.Number;
        ledger.Sellers = [.. order.Sellers];
        ledger.Currency = order.Currency;
        foreach (var line in order.Lines.Where(l => ledger.Lines.All(x => x.LineId != l.LineId)))
            ledger.Lines.Add(await TermsLineAsync(line, cancellationToken));
        if (ledger.Refunds.Count == 0 && ledger.Lines.All(l => l.QuantityPending == 0))
            ledger.Components = RefundMath.Components(order, await StandardDeliveryAsync(order, cancellationToken));
        ledger.UpdatedAt = Now;
        return ledger;
    }

    /// <summary>The terms of a line: the seller policy clamped to the channel's bounds (D20) and the withdrawal right (GA1).</summary>
    public async Task<RefundLedgerLine> TermsLineAsync(CheckoutOrderLine line, CancellationToken cancellationToken)
    {
        RefundTerms? terms = null;
        if (termsSources.FirstOrDefault(s => s.Handles(line.ItemId)) is { } source)
            terms = await source.ResolveAsync(line.ItemId, line.Channel, cancellationToken);
        var bounds = commerce.CurrentValue.Channels.TryGetValue(line.Channel, out var channel) ? channel.Refunds : new RefundPolicyBounds();
        bool right;
        string? exemption;
        if (terms?.WithdrawalRight is { } explicitRight)
        {
            right = explicitRight;
            exemption = right ? null : terms.WithdrawalExemption ?? "exempt";
        }
        else if (line.IsPhysicalGoods)
        {
            right = terms?.WithdrawalExemption is null;
            exemption = terms?.WithdrawalExemption;
        }
        else if (line.ServiceDate is not null)
        {
            // CRD Art. 16(l): leisure services for a specific date or period are exempt.
            (right, exemption) = (false, "16(l)");
        }
        else
        {
            right = Options.Withdrawal.ServicesWithoutDate;
            exemption = right ? null : "service";
        }
        return new RefundLedgerLine
        {
            LineId = line.LineId, ItemId = line.ItemId, Policy = RefundMath.Clamp(terms?.Policy, bounds),
            WithdrawalRight = right, WithdrawalExemption = exemption,
        };
    }

    private async Task<Dictionary<string, Money?>?> StandardDeliveryAsync(CheckoutOrder order, CancellationToken cancellationToken)
    {
        var sources = deliverySources.ToList();
        if (sources.Count == 0)
            return null;
        var map = new Dictionary<string, Money?>(StringComparer.Ordinal);
        foreach (var extra in order.Extras.Where(e => e.Amount.ToMinorUnits() > 0))
            foreach (var source in sources)
                if (await source.GetStandardAsync(order, extra, cancellationToken) is { } standard)
                {
                    map[extra.Code] = standard;
                    break;
                }
        return map;
    }

    /// <summary>Whether the channel refunds service and platform fees with a cancellation (D20, §4.7 "Fees").</summary>
    public bool ChannelRefundsFees(CheckoutOrder order, IEnumerable<string> lineIds)
    {
        var ids = lineIds.ToHashSet(StringComparer.Ordinal);
        var channels = order.Lines.Where(l => ids.Contains(l.LineId)).Select(l => l.Channel).Distinct();
        return channels.All(c => commerce.CurrentValue.Channels.TryGetValue(c, out var channel) && channel.Refunds.RefundFees);
    }

    /// <summary>Units of a line still free for a new return: not returned, not waiting in a withdrawal or claim.</summary>
    public static int Free(CheckoutOrderLine line, RefundLedgerLine ledgerLine)
        => line.Status == OrderLineStatus.Refused ? 0 : Math.Max(0, line.Quantity - ledgerLine.QuantityReturned - ledgerLine.QuantityPending);

    // ---- the refund ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// Plans and executes a refund: inventory returns, the ledger, the provider refund, the order's line statuses and the
    /// events. Null when the plan settles nothing.
    /// </summary>
    public async Task<CheckoutRefund?> RefundAsync(IAsyncDocumentSession session, CheckoutOrder order, CheckoutRefundLedger ledger, RefundSpec spec, RefundMeta meta,
        List<OrderEvent> raised, CancellationToken cancellationToken)
    {
        if (ledger.Refunds.FirstOrDefault(r => r.RefundId == meta.RefundId) is { } existing)
            return existing; // a retried request: already done in a saved session

        var plan = RefundMath.Plan(order, ledger, spec);
        if (plan.IsEmpty)
            return null;
        var now = Now;

        // I4: units back exactly once per (order, refund, line); shipped goods keep their quota until restock.
        var inventoryReason = spec.Reason is { } reason ? ReturnReasons.For(reason).InventoryReason
            : spec.Kind == RefundKind.Withdrawal ? ReturnReason.Withdrawal : ReturnReason.Refund;
        foreach (var units in plan.Lines)
        {
            var line = order.Lines.First(l => l.LineId == units.LineId);
            var before = ledger.Lines.First(l => l.LineId == line.LineId).QuantityReturned;
            units.StockReturned = line.ShippedAt is null;
            if (line.ClaimId is null || line.Capacity.IsEmpty)
                continue;
            var result = await inventory.ReturnAsync(line.ClaimId,
                RefundMath.InventoryReturnFor(line, units, before, InventoryReturnId(order.Id!, meta.RefundId, line.LineId), inventoryReason), cancellationToken);
            if (!result.Succeeded && result.Status != ClaimStatus.NotFound)
                logger.LogWarning("Returning {Quantity} of line {LineId} (order {OrderId}) answered {Status}.", units.Quantity, line.LineId, order.Id, result.Status);
        }

        RefundMath.Apply(ledger, plan);
        foreach (var units in plan.Lines)
        {
            var line = order.Lines.First(l => l.LineId == units.LineId);
            if (ledger.Lines.First(l => l.LineId == line.LineId).QuantityReturned >= line.Quantity)
            {
                line.Status = OrderLineStatus.Returned;
                line.StatusReason = spec.Kind.ToString();
            }
        }

        var refund = new CheckoutRefund
        {
            RefundId = meta.RefundId, Kind = spec.Kind, Reason = spec.Reason, Status = RefundStatus.Pending,
            Amount = plan.Amount, Deducted = plan.Deducted, Parts = [.. plan.Parts], Lines = [.. plan.Lines],
            CreatedAt = now, CreatedBy = meta.CreatedBy, Note = meta.Note, DueBy = meta.DueBy,
            MassRefundId = meta.MassRefundId, ReturnId = meta.ReturnId, ClaimId = meta.ClaimId,
            ProviderReference = ProviderReference(order.Id!, meta.RefundId),
        };
        ledger.Refunds.Add(refund);
        order.Log.Add(new CheckoutOrderLogEntry { At = now, Event = "refund:" + spec.Kind, Detail = $"{meta.RefundId} {CheckoutMails.Format(plan.Amount)}", ActorId = meta.CreatedBy });

        await audit.RecordAsync(session, new CommerceAuditEntry
        {
            Action = RefundAuditActions.RefundCreated, SubjectId = order.Id!, OrganizationId = order.Sellers.FirstOrDefault(),
            Before = order.Total, After = plan.Amount, At = now, ActorId = meta.CreatedBy, Reason = spec.Kind + (spec.Reason is { } r ? "/" + r : ""),
            Data = new Dictionary<string, string> { ["refundId"] = meta.RefundId, ["deducted"] = CheckoutMails.Format(plan.Deducted) },
        }, cancellationToken);

        // §4.11: tickets of the units given back are revoked (Tickets, M15), exactly once per refund id.
        var revoked = plan.Lines.Where(u => u.Quantity > 0 && order.Lines.First(l => l.LineId == u.LineId) is { IsPhysicalGoods: false })
            .Select(u => new RevokedLine(u.LineId, order.Lines.First(l => l.LineId == u.LineId).ItemId, u.Quantity, u.Units)).ToList();
        if (revoked.Count > 0)
            raised.Add(new OrderEvent(order, OrderEventKind.TicketsRevoked, null, meta.RefundId) { Revocation = new OrderTicketRevocation(meta.RefundId, revoked) });

        if (plan.CompletesOrder)
            await CancelInsuranceAsync(order, cancellationToken);

        await PayAsync(session, order, ledger, refund, attempt: 0, raised, cancellationToken, fresh: true);
        return refund;
    }

    /// <summary>Asks the provider (or marks a manual settlement as needed); applies the answer.</summary>
    public async Task PayAsync(IAsyncDocumentSession session, CheckoutOrder order, CheckoutRefundLedger ledger, CheckoutRefund refund, int attempt, List<OrderEvent> raised,
        CancellationToken cancellationToken, bool fresh = false)
    {
        var from = fresh ? (RefundStatus?)null : refund.Status;
        if (refund.Amount.IsZero)
        {
            await TransitionAsync(session, order, ledger, refund, from, RefundStatus.Succeeded, null, raised, moveOrder: true, cancellationToken);
            return;
        }
        if (order.Payment?.PaymentId is not { } paymentId || order.Origin == OrderOrigin.Manual)
        {
            // A back-office order paid outside the provider: finance pays it back and settles (audited).
            await TransitionAsync(session, order, ledger, refund, from, RefundStatus.ManualRequired, null, raised, moveOrder: false, cancellationToken);
            return;
        }
        refund.PaymentId = paymentId;
        refund.Attempt = attempt;
        var answer = await payments.RefundAsync(paymentId, refund.Amount, refund.ProviderReference!, refundApplicationFee: true, attempt: attempt, cancellationToken: cancellationToken);
        await ApplyProviderAsync(session, order, ledger, refund, answer, raised, moveOrder: true, cancellationToken, from);
    }

    /// <summary>Maps a provider answer (synchronous, or a refund notice) onto the refund.</summary>
    public Task ApplyProviderAsync(IAsyncDocumentSession session, CheckoutOrder order, CheckoutRefundLedger ledger, CheckoutRefund refund, ProviderPaymentRefund answer,
        List<OrderEvent> raised, bool moveOrder, CancellationToken cancellationToken, RefundStatus? from = null)
    {
        from ??= refund.Status;
        var to = answer.Status switch
        {
            ProviderRefundStatus.Succeeded => RefundStatus.Succeeded,
            ProviderRefundStatus.Failed or ProviderRefundStatus.Canceled => RefundStatus.Failed,
            _ => RefundStatus.Pending,
        };
        return TransitionAsync(session, order, ledger, refund, from, to, answer.FailureReason, raised, moveOrder, cancellationToken, answer.FailureMessage);
    }

    /// <summary>
    /// Moves a refund between statuses, keeping a mass refund's counters exact (same session) and issuing
    /// <c>RefundIssued</c> once when it is paid or settled.
    /// </summary>
    public async Task TransitionAsync(IAsyncDocumentSession session, CheckoutOrder order, CheckoutRefundLedger ledger, CheckoutRefund refund,
        RefundStatus? from, RefundStatus to, RefundFailureReason? failure, List<OrderEvent> raised, bool moveOrder, CancellationToken cancellationToken,
        string? failureMessage = null)
    {
        if (from == to && from is not null)
            return;
        if (from is RefundStatus.Succeeded or RefundStatus.SettledManually)
            return; // paid stays paid; a late failure notice changes nothing
        var wasFailedInsufficient = from == RefundStatus.Failed && refund.FailureReason == nameof(RefundFailureReason.InsufficientBalance);
        refund.Status = to;
        if (to == RefundStatus.Failed)
        {
            refund.FailureReason = (failure ?? RefundFailureReason.Other).ToString();
            refund.FailureMessage = failureMessage;
        }
        else
        {
            refund.FailureReason = null;
            refund.FailureMessage = null;
        }

        if (refund.MassRefundId is { } massId && await session.LoadAsync<CheckoutMassRefund>(massId, cancellationToken) is { } mass)
            MassRefundCounting.Move(mass.Counters, from, to, wasFailedInsufficient, failure == RefundFailureReason.InsufficientBalance);

        if (to is RefundStatus.Succeeded or RefundStatus.SettledManually)
        {
            refund.IssuedAt = Now;
            raised.Add(new OrderEvent(order, OrderEventKind.RefundIssued, null, refund.RefundId) { Refund = Issued(order, ledger, refund) });
            if (moveOrder)
                MoveOrder(order, ledger, refund, raised);
        }
        else if (to == RefundStatus.Failed)
        {
            order.Log.Add(new CheckoutOrderLogEntry { At = Now, Event = "refund-failed", Detail = refund.RefundId + " " + refund.FailureReason });
            // From a refund notice, checkout's own payment recipient raises RefundFailed already (OrderLifecycle.OnRefundAsync).
            if (moveOrder)
                raised.Add(new OrderEvent(order, OrderEventKind.RefundFailed, null, refund.ProviderReference));
        }
    }

    /// <summary>Order state after a refund is paid: Refunded when every line is back, else PartiallyRefunded (the provider notice may set the same).</summary>
    private void MoveOrder(CheckoutOrder order, CheckoutRefundLedger ledger, CheckoutRefund refund, List<OrderEvent> raised)
    {
        order.AmountRefunded = (order.AmountRefunded ?? Money.Zero(order.Currency)) + refund.Amount;
        if (order.AmountRefunded > order.Total)
            order.AmountRefunded = order.Total;
        var everything = order.Lines.Where(l => l.Status != OrderLineStatus.Refused)
            .All(l => ledger.Lines.First(x => x.LineId == l.LineId).QuantityReturned >= l.Quantity);
        var target = everything ? OrderState.Refunded : OrderState.PartiallyRefunded;
        var previous = order.State;
        if (previous != target && OrderStateMachine.TryMove(order, target, Now, "refund " + refund.RefundId))
            raised.Add(new OrderEvent(order, OrderEventKind.Refunded, previous, refund.ProviderReference));
    }

    /// <summary>The <c>RefundIssued</c> payload (S21 strategy H inputs per line).</summary>
    public static OrderRefundIssued Issued(CheckoutOrder order, CheckoutRefundLedger ledger, CheckoutRefund refund)
    {
        var currency = order.Currency;
        var zero = Money.Zero(currency);
        var parts = refund.Parts.ToDictionary(p => p.Key, StringComparer.Ordinal);
        var lineIds = refund.Parts.Select(p => ledger.Components.FirstOrDefault(c => c.Key == p.Key)?.LineId).Where(id => id is not null)
            .Concat(refund.Lines.Select(l => l.LineId)).Distinct().ToList();
        var lines = new List<RefundedLine>();
        foreach (var lineId in lineIds)
        {
            var line = order.Lines.First(l => l.LineId == lineId);
            var components = ledger.Components.Where(c => c.LineId == lineId).ToList();
            var mine = components.Where(c => parts.ContainsKey(c.Key)).Select(c => parts[c.Key]).ToList();
            var price = parts.TryGetValue(RefundMath.PriceKey(lineId!), out var p) ? p : null;
            lines.Add(new RefundedLine(
                lineId!, line.ItemId, line.SellerOrganizationId,
                refund.Lines.Where(l => l.LineId == lineId).Sum(l => l.Quantity),
                Money.Sum(mine.Select(x => x.Gross), currency),
                price?.Gross ?? zero,
                components.Where(c => c.Part == RefundMath.Fee && parts.ContainsKey(c.Key)).Select(c => new RefundedFee(c.Code!, parts[c.Key].Gross)).ToList(),
                Money.Sum(mine.Select(x => x.Deducted), currency),
                Money.Sum(mine.Select(x => x.SettledBefore), currency),
                Money.Sum(components.Select(c => c.Gross), currency),
                line.Net, line.Vat, line.VatRateBasisPoints, line.VatCategory));
        }
        var extras = ledger.Components.Where(c => c.Part == RefundMath.Extra && parts.ContainsKey(c.Key))
            .Select(c => new RefundedExtra(c.Code!, c.ExtraKind ?? "", parts[c.Key].Gross, parts[c.Key].SettledBefore, c.Gross,
                order.Extras.FirstOrDefault(e => e.Code == c.Code)?.VatCategory))
            .ToList();
        return new OrderRefundIssued(refund.RefundId, order.Id!, currency, refund.Kind, refund.Reason, refund.Amount, refund.Deducted, lines, extras,
            refund.IssuedAt ?? DateTimeOffset.MinValue, refund.Status == RefundStatus.SettledManually);
    }

    private async Task CancelInsuranceAsync(CheckoutOrder order, CancellationToken cancellationToken)
    {
        if (order.InsurancePolicyId is not { } policyId || insurance.Provider is not { } provider)
            return;
        if (order.Lines.Any(l => l.ServiceDate is { } date && date <= Now))
            return; // after the event, the cover stays (claims are the insurer's)
        try
        {
            await provider.CancelAsync(policyId, cancellationToken);
            order.Log.Add(new CheckoutOrderLogEntry { At = Now, Event = "insurance-cancelled", Detail = policyId });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Cancelling insurance {PolicyId} of order {OrderId} failed.", policyId, order.Id);
        }
    }
}

/// <summary>Exact mass-refund counters: one status bucket per refund, moved in the refund's own transaction (S15).</summary>
internal static class MassRefundCounting
{
    public static void Move(MassRefundCounters counters, RefundStatus? from, RefundStatus to, bool fromInsufficient, bool toInsufficient)
    {
        if (from is { } f)
            Add(counters, f, -1, fromInsufficient);
        else
            counters.Refunds++;
        Add(counters, to, +1, toInsufficient);
    }

    private static void Add(MassRefundCounters counters, RefundStatus status, int delta, bool insufficient)
    {
        switch (status)
        {
            case RefundStatus.Pending: counters.Pending += delta; break;
            case RefundStatus.Succeeded: counters.Succeeded += delta; break;
            case RefundStatus.Failed:
                counters.Failed += delta;
                if (insufficient) counters.InsufficientBalance += delta;
                break;
            case RefundStatus.ManualRequired: counters.ManualRequired += delta; break;
            case RefundStatus.SettledManually: counters.SettledManually += delta; break;
        }
    }
}

/// <summary>The refund flows' audit actions (money-moving changes, default 8).</summary>
public static class RefundAuditActions
{
    public const string RefundCreated = "commerce-checkout.refund-created";
    public const string RefundSettledManually = "commerce-checkout.refund-settled-manually";
    public const string DiminishedValue = "commerce-checkout.diminished-value";
    public const string WithdrawalRegistered = "commerce-checkout.withdrawal-registered";
    public const string GuaranteeResolved = "commerce-checkout.guarantee-resolved";
    public const string MassRefundStarted = "commerce-checkout.mass-refund-started";
}
