using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Commerce.Checkout.Contracts;
using MintPlayer.Spark.Commerce.Checkout.Models;
using MintPlayer.Spark.Commerce.Checkout.Services;
using MintPlayer.Spark.Commerce.Inventory;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Commerce.Checkout.Refunds;

/// <summary>
/// The refund flows (PRD §4.7): the buyer's withdrawal (GA1), cancellation within the seller's policy (D20), guarantee
/// claims (GA8) and reschedule choices; staff's mailed withdrawals, goods back, return refunds, claim remedies, retries and
/// manual settlements; delivery and goods-back reports from Shipping, Shop and the warehouse. Every write goes through
/// <see cref="OrderLifecycle.UpdateAsync"/>: the order and its ledger are saved together.
/// </summary>
internal sealed class RefundService(
    IDocumentStore store,
    OrderLifecycle lifecycle,
    RefundEngine engine,
    RefundMails mails,
    OrderAccess access,
    OrderLinks links,
    ISparkCheckout checkout,
    ISparkInventory inventory,
    ICommerceAudit audit,
    IOptionsMonitor<SparkCommerceCheckoutOptions> options,
    CheckoutHolidays holidays,
    ILogger<RefundService> logger) : ISparkCheckoutRefunds
{
    public const string WithdrawButtonKey = "commerceCheckout.withdrawal.button";
    public const string WithdrawConfirmKey = "commerceCheckout.withdrawal.confirm";

    private CheckoutRefundOptions Options => options.CurrentValue.Refunds;

    private DateTimeOffset Now => engine.Now;

    // ---- access ----------------------------------------------------------------------------------------------------

    /// <summary>The buyer's order: by the signed guest link (<paramref name="accessToken"/>), else the account's or visitor's own.</summary>
    internal async Task<CheckoutOrder?> BuyerOrderAsync(string number, string? accessToken, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(number))
            return null;
        if (links.Read(accessToken) is { } orderId)
        {
            using var session = store.OpenAsyncSession();
            var order = await session.LoadAsync<CheckoutOrder>(orderId, cancellationToken);
            return order is not null && order.Number == number ? order : null;
        }
        return await checkout.GetOwnOrderAsync(number, cancellationToken);
    }

    /// <summary>An order staff may handle in <paramref name="role"/> (box-office or finance of one of its sellers, or the platform's).</summary>
    internal async Task<CheckoutOrder> StaffOrderAsync(string orderId, string role, CancellationToken cancellationToken)
    {
        using var session = store.OpenAsyncSession();
        var order = await session.LoadAsync<CheckoutOrder>(orderId, cancellationToken) ?? throw CheckoutException.Of(CheckoutRefusals.OrderNotFound);
        if (!await access.MayHandleAsync(order, role, cancellationToken))
            throw CheckoutException.Of(CheckoutRefusals.Forbidden);
        return order;
    }

    internal string BackOfficeRole => options.CurrentValue.BackOfficeRole;

    internal string FinanceRole => options.CurrentValue.MarkPaidRole;

    internal string? CallerId => access.CallerId;

    private async Task<CheckoutOrder> UpdateAsync(string orderId, Func<IAsyncDocumentSession, CheckoutOrder, CheckoutRefundLedger, List<OrderEvent>, Task> change, CancellationToken cancellationToken)
        => await lifecycle.UpdateAsync(orderId, async (session, order, raised) =>
        {
            var ledger = await engine.LedgerAsync(session, order, cancellationToken);
            await change(session, order, ledger, raised);
        }, cancellationToken) ?? throw CheckoutException.Of(CheckoutRefusals.OrderNotFound);

    /// <summary>The ledger as it would be loaded (nothing saved): for views and previews.</summary>
    private async Task<CheckoutRefundLedger> ReadLedgerAsync(CheckoutOrder order, CancellationToken cancellationToken)
    {
        using var session = store.OpenAsyncSession();
        return await engine.LedgerAsync(session, order, cancellationToken);
    }

    // ---- the view -----------------------------------------------------------------------------------------------------

    internal async Task<SparkOrderAftercare> ViewAsync(CheckoutOrder order, CancellationToken cancellationToken)
    {
        var ledger = await ReadLedgerAsync(order, cancellationToken);
        var now = Now;
        var holidayDays = await WithdrawalHolidaysAsync(order, ledger, cancellationToken);
        var deadline = WithdrawalDeadline(order, ledger, holidayDays);
        var lines = new List<SparkAftercareLine>();
        foreach (var line in order.Lines.Where(l => l.Status != OrderLineStatus.Refused))
        {
            var ledgerLine = ledger.Lines.First(l => l.LineId == line.LineId);
            var free = RefundEngine.Free(line, ledgerLine);
            var (withdrawable, _) = WithdrawalEligibility(order, line, ledgerLine, deadline, now, holidayDays);
            var refusal = RefundEngine.IsRefundable(order.State)
                ? RefundMath.CancellationRefusal(ledgerLine.Policy, line.ServiceDate, now, out var fee) : CheckoutRefusals.InvalidState;
            var guaranteeUntil = GuaranteeEnd(line, ledgerLine);
            lines.Add(new SparkAftercareLine(line.LineId, line.Title, line.Quantity, ledgerLine.QuantityReturned, free,
                ledgerLine.WithdrawalRight, withdrawable && free > 0, ledgerLine.WithdrawalExemption,
                free > 0 ? refusal : RefundRefusals.NothingLeft,
                refusal is null && ledgerLine.Policy.Kind == RefundPolicyKind.WithFee ? ledgerLine.Policy.FeeBasisPoints : null,
                line.IsPhysicalGoods && guaranteeUntil is { } until && now < until && free > 0 && RefundEngine.IsRefundable(order.State),
                guaranteeUntil));
        }
        var windows = await OpenWindowsAsync(order, cancellationToken);
        return new SparkOrderAftercare(
            order.Number, order.State.ToString(), order.Currency,
            new SparkWithdrawalInfo(lines.Any(l => l.WithdrawalLive), deadline, WithdrawButtonKey, WithdrawConfirmKey, Options.Withdrawal.SellerPaysReturnShipping),
            lines,
            ledger.Returns.Select(r => new SparkAftercareReturn(r.ReturnId, r.Reason.ToString(), r.Status.ToString(), r.ReceivedAt, LineViews(r.Lines), r.RefundDueBy)).ToList(),
            ledger.Claims.Select(c => new SparkAftercareClaim(c.ClaimId, c.Status.ToString(), c.Remedy?.ToString(), c.OpenedAt, LineViews(c.Lines))).ToList(),
            ledger.Refunds.Select(r => new SparkAftercareRefund(r.RefundId, r.Kind.ToString(), r.Status.ToString(), r.Amount, r.CreatedAt, r.IssuedAt)).ToList(),
            windows.Select(w => new SparkRescheduleOffer(w.Id!, w.ClosesAt, w.NewServiceDate, ledger.RescheduleChoices.GetValueOrDefault(w.Id!))).ToList());
    }

    private static List<SparkReturnLineView> LineViews(IEnumerable<CheckoutReturnLine> lines)
        => lines.Select(l => new SparkReturnLineView(l.LineId, l.Quantity, l.Eligible, l.IneligibleReason)).ToList();

    private async Task<List<CheckoutRescheduleWindow>> OpenWindowsAsync(CheckoutOrder order, CancellationToken cancellationToken)
    {
        var items = order.Lines.Select(l => l.ItemId).Distinct().ToList();
        if (items.Count == 0)
            return [];
        var now = Now;
        using var session = store.OpenAsyncSession();
        // LINQ: the item ids and the time are query parameters, never spliced into RQL.
        return await Raven.Client.Documents.LinqExtensions.ToListAsync(
            session.Query<CheckoutRescheduleWindow>().Where(w => w.ClosesAt > now && w.ItemIds.Any(i => items.Contains(i))), cancellationToken);
    }

    // ---- periods ------------------------------------------------------------------------------------------------------

    /// <summary>The end of the withdrawal period for goods (CRD Art. 9(2)(b)); null while a goods line is undelivered.</summary>
    internal DateTimeOffset? WithdrawalDeadline(CheckoutOrder order, CheckoutRefundLedger ledger, Func<DateOnly, bool>? isHoliday = null)
        => RefundMath.WithdrawalPeriodStart(order, ledger) is { } start ? RefundMath.PeriodEnd(start, Math.Max(14, Options.Withdrawal.PeriodDays), engine.Zone, isHoliday: isHoliday) : null;

    /// <summary>
    /// The public holidays (Reg. 1182/71 Art. 3(4)) that can end a withdrawal period of <paramref name="order"/>: from the
    /// goods' delivery and from the conclusion of the contract. Null without a holiday calendar (weekend-only counting).
    /// </summary>
    internal Task<Func<DateOnly, bool>?> WithdrawalHolidaysAsync(CheckoutOrder order, CheckoutRefundLedger ledger, CancellationToken cancellationToken)
        => holidays.ForPeriodsAsync([RefundMath.WithdrawalPeriodStart(order, ledger), order.ConfirmedAt ?? order.CreatedAt],
            Math.Max(14, Options.Withdrawal.PeriodDays), engine.Zone, cancellationToken);

    /// <summary>Whether a line can be withdrawn now, and why not.</summary>
    internal (bool Eligible, string? Reason) WithdrawalEligibility(CheckoutOrder order, CheckoutOrderLine line, RefundLedgerLine ledgerLine, DateTimeOffset? goodsDeadline, DateTimeOffset now, Func<DateOnly, bool>? isHoliday = null)
    {
        if (!RefundEngine.IsRefundable(order.State))
            return (false, "state:" + order.State);
        if (!ledgerLine.WithdrawalRight)
            return (false, "exempt:" + (ledgerLine.WithdrawalExemption ?? "none"));
        // Goods: from the (last) delivery, Art. 9(2)(b); a service: from the conclusion of the contract, Art. 9(2)(a).
        var deadline = line.IsPhysicalGoods ? goodsDeadline
            : RefundMath.PeriodEnd(order.ConfirmedAt ?? order.CreatedAt, Math.Max(14, Options.Withdrawal.PeriodDays), engine.Zone, isHoliday: isHoliday);
        if (deadline is { } end && now >= end)
            return (false, "periodEnded");
        return (true, null);
    }

    /// <summary>The end of the legal guarantee (Directive 2019/771 Art. 10(1)): from delivery (or shipment when no delivery was reported).</summary>
    internal DateTimeOffset? GuaranteeEnd(CheckoutOrderLine line, RefundLedgerLine ledgerLine)
    {
        if ((ledgerLine.DeliveredAt ?? line.ShippedAt) is not { } from)
            return null;
        var days = Options.Guarantee.ChannelPeriodDays.TryGetValue(line.Channel, out var d) ? d : Options.Guarantee.PeriodDays;
        return RefundMath.PeriodEnd(from, days, engine.Zone, moveOffWeekend: false);
    }

    // ---- withdrawal (GA1) -----------------------------------------------------------------------------------------

    /// <summary>
    /// GA1: records a withdrawal at once (no approval step), refunds what is not shipped, withholds the rest until the goods
    /// are back or dispatch is proven (CRD Art. 13(3)), and mails the receipt. A preview (<paramref name="confirm"/> false)
    /// answers what is and is not within the right, before the buyer confirms.
    /// </summary>
    internal async Task<SparkWithdrawalResult> WithdrawAsync(CheckoutOrder order, IReadOnlyList<SparkRefundLine>? lines, string? note, string? name, string? email,
        ReturnSource source, DateTimeOffset receivedAt, string? actorId, bool confirm, CancellationToken cancellationToken)
    {
        if (!confirm)
        {
            var previewLedger = await ReadLedgerAsync(order, cancellationToken);
            var preview = BuildWithdrawal(order, previewLedger, lines, note, name, email, source, receivedAt, actorId, "preview",
                await WithdrawalHolidaysAsync(order, previewLedger, cancellationToken));
            return new SparkWithdrawalResult(false, null, null, LineViews(preview.Lines), null, preview.Lines.Any(l => l.Eligible && l.Shipped), null);
        }
        // CRD Art. 11a(2): the name and the address the acknowledgement goes to (an account has both).
        if (source == ReturnSource.Online && order.BuyerUserId is null
            && (string.IsNullOrWhiteSpace(name ?? order.BuyerName) || string.IsNullOrWhiteSpace(email ?? order.BuyerEmail)))
            throw CheckoutException.Of(RefundRefusals.ContactRequired);

        CheckoutReturn? recorded = null;
        CheckoutRefund? refunded = null;
        var updated = await UpdateAsync(order.Id!, async (session, o, ledger, raised) =>
        {
            var withdrawal = BuildWithdrawal(o, ledger, lines, note, name, email, source, receivedAt, actorId, RefundEngine.NewId("w"),
                await WithdrawalHolidaysAsync(o, ledger, cancellationToken));
            if (withdrawal.Lines.Count == 0)
                throw CheckoutException.Of(RefundRefusals.NothingLeft);
            ledger.Returns.Add(withdrawal);
            recorded = withdrawal;
            foreach (var line in withdrawal.Lines.Where(l => l.Eligible))
                ledger.Lines.First(l => l.LineId == line.LineId).QuantityPending += line.Quantity;

            var now = Now;
            withdrawal.RefundDueBy = withdrawal.ReceivedAt.AddDays(Options.Withdrawal.RefundWithinDays);
            var immediate = withdrawal.Lines.Where(l => l.Eligible && !l.Shipped).ToList();
            if (immediate.Count > 0)
                refunded = await RefundReturnLinesAsync(session, o, ledger, withdrawal, immediate, deductions: null, raised, actorId, cancellationToken);
            withdrawal.Status = withdrawal.Lines.Any(l => l.Eligible && l.Shipped) ? ReturnStatus.AwaitingGoods
                : immediate.Count > 0 ? ReturnStatus.Refunded : ReturnStatus.Closed;

            o.Log.Add(new CheckoutOrderLogEntry { At = now, Event = "withdrawal", Detail = $"{withdrawal.ReturnId} {source} received {withdrawal.ReceivedAt:O}", ActorId = actorId });
            if (source != ReturnSource.Online)
                await audit.RecordAsync(session, new CommerceAuditEntry
                {
                    Action = RefundAuditActions.WithdrawalRegistered, SubjectId = o.Id!, OrganizationId = o.Sellers.FirstOrDefault(), ActorId = actorId, At = now,
                    Reason = source.ToString(), Data = new Dictionary<string, string> { ["returnId"] = withdrawal.ReturnId, ["receivedAt"] = withdrawal.ReceivedAt.ToString("O") },
                }, cancellationToken);
            raised.Add(new OrderEvent(o, OrderEventKind.WithdrawalRequested, null, withdrawal.ReturnId));
            RaiseReturnExpected(o, withdrawal, raised);
            withdrawal.ReceiptSentAt = now;
        }, cancellationToken);

        // After the save: a retried session never mails a receipt for a withdrawal that was not stored.
        await mails.SendWithdrawalReceiptAsync(updated, recorded!, cancellationToken);
        await MarkIssuedDeliveredAsync(updated, cancellationToken);
        return new SparkWithdrawalResult(true, recorded!.ReturnId, recorded.ReceivedAt, LineViews(recorded.Lines), refunded?.Amount,
            recorded.Status == ReturnStatus.AwaitingGoods, recorded.RefundDueBy);
    }

    private CheckoutReturn BuildWithdrawal(CheckoutOrder order, CheckoutRefundLedger ledger, IReadOnlyList<SparkRefundLine>? lines, string? note, string? name, string? email,
        ReturnSource source, DateTimeOffset receivedAt, string? actorId, string returnId, Func<DateOnly, bool>? isHoliday)
    {
        var now = Now;
        var deadline = WithdrawalDeadline(order, ledger, isHoliday);
        var requested = lines?.ToList();
        if (requested is null)
        {
            var free = order.Lines.Where(l => l.Status != OrderLineStatus.Refused)
                .Select(l => (Line: l, Ledger: ledger.Lines.First(x => x.LineId == l.LineId)))
                .Where(x => RefundEngine.Free(x.Line, x.Ledger) > 0).ToList();
            var withRight = free.Where(x => x.Ledger.WithdrawalRight).ToList();
            requested = (withRight.Count > 0 ? withRight : free).Select(x => new SparkRefundLine(x.Line.LineId, RefundEngine.Free(x.Line, x.Ledger))).ToList();
        }
        var rules = ReturnReasons.For(ReturnReasonCode.Withdrawal);
        var result = new CheckoutReturn
        {
            ReturnId = returnId, Reason = ReturnReasonCode.Withdrawal, Status = ReturnStatus.Requested, Source = source,
            // A buyer's online withdrawal is received now; staff enter a letter's or call's receipt time (never in the future).
            ReceivedAt = source == ReturnSource.Online || receivedAt > now ? now : receivedAt,
            RecordedAt = now, RecordedBy = actorId, Note = note, Name = name ?? order.BuyerName, Email = email ?? order.BuyerEmail,
            DefaultDisposition = rules.DefaultDisposition,
            SellerPaysReturnShipping = rules.SellerPaysReturnShipping ?? Options.Withdrawal.SellerPaysReturnShipping,
        };
        foreach (var request in requested.Where(r => r.Quantity > 0))
        {
            var line = order.Lines.FirstOrDefault(l => l.LineId == request.LineId && l.Status != OrderLineStatus.Refused)
                ?? throw CheckoutException.Of(CheckoutRefusals.LineNotFound, request.LineId);
            var ledgerLine = ledger.Lines.First(l => l.LineId == line.LineId);
            var free = RefundEngine.Free(line, ledgerLine);
            if (free == 0)
                continue;
            var (eligible, reason) = WithdrawalEligibility(order, line, ledgerLine, deadline, result.ReceivedAt, isHoliday);
            result.Lines.Add(new CheckoutReturnLine
            {
                LineId = line.LineId, Quantity = Math.Min(free, request.Quantity), Units = request.Units?.ToList() ?? [],
                Eligible = eligible, IneligibleReason = reason, Shipped = line.ShippedAt is not null,
            });
        }
        return result;
    }

    /// <summary>Refunds the given lines of a return (withdrawal or guarantee termination) and releases their pending units.</summary>
    private async Task<CheckoutRefund?> RefundReturnLinesAsync(IAsyncDocumentSession session, CheckoutOrder order, CheckoutRefundLedger ledger, CheckoutReturn ret,
        IReadOnlyList<CheckoutReturnLine> lines, IReadOnlyDictionary<string, Money>? deductions, List<OrderEvent> raised, string? actorId, CancellationToken cancellationToken)
    {
        foreach (var line in lines)
            ledger.Lines.First(l => l.LineId == line.LineId).QuantityPending -= line.Quantity;
        var rules = ReturnReasons.For(ret.Reason);
        var withdrawal = ret.Reason == ReturnReasonCode.Withdrawal;
        var spec = new RefundSpec(
            withdrawal ? RefundKind.Withdrawal : RefundKind.Guarantee, ret.Reason,
            lines.Select(l => new RefundLineRequest(l.LineId, l.Quantity, l.Units.Count > 0 ? l.Units : null)).ToList(),
            RefundFees: rules.RefundsFees, Delivery: rules.Delivery, PartialDelivery: Options.Withdrawal.PartialDelivery,
            Deductions: deductions, IncludeOrderExtras: true);
        var refund = await engine.RefundAsync(session, order, ledger, spec,
            new RefundMeta(RefundEngine.StableId("r", ret.ReturnId + ":" + string.Join(",", lines.Select(l => l.LineId))), actorId,
                DueBy: ret.RefundDueBy, ReturnId: ret.ReturnId, ClaimId: ret.ClaimId), raised, cancellationToken);
        if (refund is not null && !ret.RefundIds.Contains(refund.RefundId))
            ret.RefundIds.Add(refund.RefundId);
        return refund;
    }

    private static void RaiseReturnExpected(CheckoutOrder order, CheckoutReturn ret, List<OrderEvent> raised)
    {
        var back = ret.Lines.Where(l => l.Eligible && l.Shipped).Select(l => new ExpectedReturnLine(l.LineId, order.Lines.First(x => x.LineId == l.LineId).ItemId, l.Quantity)).ToList();
        if (back.Count > 0)
            raised.Add(new OrderEvent(order, OrderEventKind.ReturnExpected, null, ret.ReturnId)
            {
                Return = new OrderReturnExpected(ret.ReturnId, ret.Reason, ret.DefaultDisposition, ret.SellerPaysReturnShipping, back),
            });
    }

    // ---- goods back (CRD Art. 13(3), I4) ---------------------------------------------------------------------------

    public async Task RegisterGoodsBackAsync(GoodsBackRegistration registration, CancellationToken cancellationToken = default)
    {
        var updated = await UpdateAsync(registration.OrderId, async (session, order, ledger, raised) =>
        {
            var ret = ledger.Returns.FirstOrDefault(r => r.ReturnId == registration.ReturnId) ?? throw CheckoutException.Of(CheckoutRefusals.LineNotFound, subject: registration.ReturnId);
            var now = Now;
            var at = registration.At > now ? now : registration.At;
            var deductions = registration.Deductions?.Where(d => d.Amount.ToMinorUnits() > 0).ToList() ?? [];
            if (deductions.Count > 0 && (registration.Kind != GoodsBackKind.Received || ret.Status != ReturnStatus.AwaitingGoods))
                throw CheckoutException.Of(RefundRefusals.RefundState); // only on receipt, before the withheld refund is paid

            if (registration.Kind == GoodsBackKind.Received)
                ret.GoodsReceivedAt ??= at;
            else
                ret.ProofOfDispatchAt ??= at;

            if (ret.Status == ReturnStatus.AwaitingGoods)
            {
                foreach (var d in deductions)
                {
                    ret.Deductions.Add(new CheckoutDeduction { LineId = d.LineId, Amount = d.Amount, Reason = d.Reason, By = registration.ActorId, At = now });
                    await audit.RecordAsync(session, new CommerceAuditEntry
                    {
                        Action = RefundAuditActions.DiminishedValue, SubjectId = order.Id!, OrganizationId = order.Sellers.FirstOrDefault(), After = d.Amount,
                        Reason = d.Reason, ActorId = registration.ActorId, At = now, Data = new Dictionary<string, string> { ["returnId"] = ret.ReturnId, ["lineId"] = d.LineId },
                    }, cancellationToken);
                }
                var waiting = ret.Lines.Where(l => l.Eligible && l.Shipped).ToList();
                var byLine = deductions.GroupBy(d => d.LineId).ToDictionary(g => g.Key, g => Money.Sum(g.Select(d => d.Amount), order.Currency));
                await RefundReturnLinesAsync(session, order, ledger, ret, waiting, byLine, raised, registration.ActorId, cancellationToken);
                ret.Status = ReturnStatus.Refunded;
                if (ret.ClaimId is { } claimId && ledger.Claims.FirstOrDefault(c => c.ClaimId == claimId) is { } claim)
                {
                    claim.Status = GuaranteeClaimStatus.Resolved;
                    claim.ResolvedAt ??= now;
                    claim.RefundId = ret.RefundIds.LastOrDefault();
                    raised.Add(new OrderEvent(order, OrderEventKind.GuaranteeClaimResolved, null, claim.ClaimId));
                }
            }

            if (registration.Kind == GoodsBackKind.Received)
            {
                ret.Disposition = registration.Disposition ?? ret.Disposition ?? ret.DefaultDisposition;
                if (ret.Disposition == ReturnDisposition.Restock)
                    await RestockAsync(order, ledger, ret, cancellationToken);
            }
            order.Log.Add(new CheckoutOrderLogEntry { At = now, Event = "goods-back:" + registration.Kind, Detail = ret.ReturnId, ActorId = registration.ActorId });
        }, cancellationToken);
        await MarkIssuedDeliveredAsync(updated, cancellationToken);
    }

    /// <summary>I4: shipped units of a return go back on sale exactly once (the engine keys restock by the return id).</summary>
    private async Task RestockAsync(CheckoutOrder order, CheckoutRefundLedger ledger, CheckoutReturn ret, CancellationToken cancellationToken)
    {
        if (ret.Restocked)
            return;
        foreach (var refund in ledger.Refunds.Where(r => ret.RefundIds.Contains(r.RefundId)))
            foreach (var units in refund.Lines.Where(u => !u.StockReturned))
            {
                var line = order.Lines.First(l => l.LineId == units.LineId);
                if (line.ClaimId is null || line.Capacity.IsEmpty)
                    continue;
                var result = await inventory.RestockAsync(line.ClaimId, RefundEngine.InventoryReturnId(order.Id!, refund.RefundId, line.LineId), cancellationToken);
                if (!result.Succeeded)
                    logger.LogWarning("Restocking line {LineId} of order {OrderId} answered {Status}.", line.LineId, order.Id, result.Status);
            }
        ret.Restocked = true;
    }

    // ---- delivery --------------------------------------------------------------------------------------------------

    public async Task MarkDeliveredAsync(string orderId, string lineId, DateTimeOffset deliveredAt, CancellationToken cancellationToken = default)
        => await UpdateAsync(orderId, (session, order, ledger, raised) =>
        {
            var line = ledger.Lines.FirstOrDefault(l => l.LineId == lineId) ?? throw CheckoutException.Of(CheckoutRefusals.LineNotFound, lineId);
            if (line.DeliveredAt is null)
            {
                line.DeliveredAt = deliveredAt;
                order.Log.Add(new CheckoutOrderLogEntry { At = Now, Event = "delivered", Detail = lineId });
            }
            return Task.CompletedTask;
        }, cancellationToken);

    // ---- cancellation within the seller's policy (D20) -------------------------------------------------------------

    internal async Task<SparkRefundResult> CancelAsync(CheckoutOrder order, IReadOnlyList<SparkRefundLine>? lines, bool confirm, string? actorId, CancellationToken cancellationToken)
    {
        if (!RefundEngine.IsRefundable(order.State))
            throw CheckoutException.Of(CheckoutRefusals.InvalidState, subject: order.State.ToString());
        if (!confirm)
        {
            var ledger = await ReadLedgerAsync(order, cancellationToken);
            var plans = CancellationGroups(order, ledger, lines).Select(g => RefundMath.Plan(order, ledger, g.Spec)).ToList();
            return Result(false, order, plans.Select(p => ((RefundPlan?)p, (string?)null)).ToList());
        }
        var done = new List<(RefundPlan? Plan, string? Id)>();
        var updated = await UpdateAsync(order.Id!, async (session, o, ledger, raised) =>
        {
            done.Clear();
            foreach (var group in CancellationGroups(o, ledger, lines))
            {
                var refund = await engine.RefundAsync(session, o, ledger, group.Spec, new RefundMeta(RefundEngine.NewId("c"), actorId), raised, cancellationToken);
                if (refund is not null)
                    done.Add((new RefundPlan(refund.Parts, refund.Lines, refund.Amount, refund.Deducted, false), refund.RefundId));
            }
        }, cancellationToken);
        await MarkIssuedDeliveredAsync(updated, cancellationToken);
        return Result(true, order, done);
    }

    private IEnumerable<(int Fee, RefundSpec Spec)> CancellationGroups(CheckoutOrder order, CheckoutRefundLedger ledger, IReadOnlyList<SparkRefundLine>? lines)
    {
        var now = Now;
        var requested = lines?.ToList() ?? order.Lines.Where(l => l.Status != OrderLineStatus.Refused)
            .Select(l => new SparkRefundLine(l.LineId, RefundEngine.Free(l, ledger.Lines.First(x => x.LineId == l.LineId)))).Where(l => l.Quantity > 0).ToList();
        var byFee = new Dictionary<int, List<RefundLineRequest>>();
        foreach (var request in requested.Where(r => r.Quantity > 0))
        {
            var line = order.Lines.FirstOrDefault(l => l.LineId == request.LineId && l.Status != OrderLineStatus.Refused)
                ?? throw CheckoutException.Of(CheckoutRefusals.LineNotFound, request.LineId);
            var ledgerLine = ledger.Lines.First(l => l.LineId == line.LineId);
            if (RefundEngine.Free(line, ledgerLine) < request.Quantity)
                throw CheckoutException.Of(RefundRefusals.NothingLeft, line.LineId);
            if (RefundMath.CancellationRefusal(ledgerLine.Policy, line.ServiceDate, now, out var fee) is { } refusal)
                throw CheckoutException.Of(refusal, line.LineId);
            (byFee.TryGetValue(fee, out var list) ? list : byFee[fee] = []).Add(new RefundLineRequest(line.LineId, request.Quantity, request.Units));
        }
        if (byFee.Count == 0)
            throw CheckoutException.Of(RefundRefusals.NothingLeft);
        foreach (var (fee, group) in byFee)
            yield return (fee, new RefundSpec(RefundKind.Cancellation, ReturnReasonCode.SellerAllowed, group,
                RefundFees: engine.ChannelRefundsFees(order, group.Select(g => g.LineId)), FeeBasisPoints: fee));
    }

    private static SparkRefundResult Result(bool confirmed, CheckoutOrder order, List<(RefundPlan? Plan, string? Id)> plans)
    {
        var zero = Money.Zero(order.Currency);
        var real = plans.Where(p => p.Plan is not null).Select(p => p.Plan!).ToList();
        return new SparkRefundResult(confirmed,
            real.Aggregate(zero, (sum, p) => sum + p.Amount), real.Aggregate(zero, (sum, p) => sum + p.Deducted),
            real.SelectMany(p => p.Lines).Select(l => new SparkRefundLine(l.LineId, l.Quantity, l.Units)).ToList(),
            plans.Where(p => p.Id is not null).Select(p => p.Id!).ToList());
    }

    // ---- staff refunds by return reason -------------------------------------------------------------------------------

    internal async Task<SparkRefundResult> StaffRefundAsync(CheckoutOrder order, ReturnReasonCode reason, IReadOnlyList<SparkRefundLine>? lines, Money? amount, string? note,
        string actorId, CancellationToken cancellationToken)
    {
        if (reason is ReturnReasonCode.Withdrawal or ReturnReasonCode.NonConformity)
            throw CheckoutException.Of(CheckoutRefusals.InvalidState, subject: reason.ToString()); // the withdrawal and claim flows own these
        if (!RefundEngine.IsRefundable(order.State))
            throw CheckoutException.Of(CheckoutRefusals.InvalidState, subject: order.State.ToString());
        var rules = ReturnReasons.For(reason);
        var sellerFault = reason is ReturnReasonCode.WrongItem or ReturnReasonCode.DamagedInTransit;
        var requests = lines?.Where(l => l.Quantity > 0).Select(l => new RefundLineRequest(l.LineId, l.Quantity, l.Units)).ToList() ?? [];
        var done = new List<(RefundPlan? Plan, string? Id)>();
        var updated = await UpdateAsync(order.Id!, async (session, o, ledger, raised) =>
        {
            done.Clear();
            foreach (var request in requests)
                if (o.Lines.FirstOrDefault(l => l.LineId == request.LineId) is not { } line || RefundEngine.Free(line, ledger.Lines.First(x => x.LineId == line.LineId)) < request.Quantity)
                    throw CheckoutException.Of(RefundRefusals.NothingLeft, request.LineId);
            var lineIds = requests.Count > 0 ? requests.Select(r => r.LineId).ToList() : o.Lines.Select(l => l.LineId).ToList();
            var spec = new RefundSpec(RefundKind.Return, reason, requests,
                Amount: requests.Count == 0 ? amount : null, AmountLineIds: lines is { Count: > 0 } ? lines.Select(l => l.LineId).ToList() : null,
                RefundFees: rules.RefundsFees || engine.ChannelRefundsFees(o, lineIds), Delivery: rules.Delivery, IncludeOrderExtras: sellerFault);
            CheckoutReturn? ret = null;
            if (requests.Count > 0)
            {
                ret = new CheckoutReturn
                {
                    ReturnId = RefundEngine.NewId("s"), Reason = reason, Status = ReturnStatus.Refunded, Source = ReturnSource.Staff,
                    ReceivedAt = Now, RecordedAt = Now, RecordedBy = actorId, Note = note, DefaultDisposition = rules.DefaultDisposition,
                    SellerPaysReturnShipping = rules.SellerPaysReturnShipping ?? false,
                    Lines = requests.Select(r => new CheckoutReturnLine { LineId = r.LineId, Quantity = r.Quantity, Units = r.Units?.ToList() ?? [], Eligible = true,
                        Shipped = o.Lines.First(l => l.LineId == r.LineId).ShippedAt is not null }).ToList(),
                };
                ledger.Returns.Add(ret);
            }
            var refund = await engine.RefundAsync(session, o, ledger, spec, new RefundMeta(RefundEngine.NewId("s"), actorId, note, ReturnId: ret?.ReturnId), raised, cancellationToken);
            if (refund is not null)
            {
                ret?.RefundIds.Add(refund.RefundId);
                done.Add((new RefundPlan(refund.Parts, refund.Lines, refund.Amount, refund.Deducted, false), refund.RefundId));
            }
            if (ret is not null)
                RaiseReturnExpected(o, ret, raised);
        }, cancellationToken);
        await MarkIssuedDeliveredAsync(updated, cancellationToken);
        return Result(true, order, done);
    }

    // ---- guarantee claims (GA8) ---------------------------------------------------------------------------------------

    internal async Task<SparkOrderAftercare> OpenClaimAsync(CheckoutOrder order, IReadOnlyList<SparkRefundLine> lines, string description, GuaranteeRemedy? preferred,
        ReturnSource source, string? actorId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(description) || description.Length > 4000)
            throw CheckoutException.Of(CheckoutRefusals.Conflict, subject: "description");
        if (preferred is GuaranteeRemedy.PriceReduction or GuaranteeRemedy.Refund)
            preferred = null; // Art. 13(2): the consumer chooses between repair and replacement; 13(4) opens the others
        var updated = await UpdateAsync(order.Id!, (session, o, ledger, raised) =>
        {
            var now = Now;
            var claim = new CheckoutGuaranteeClaim
            {
                ClaimId = RefundEngine.NewId("g"), Status = GuaranteeClaimStatus.Open, Description = description.Trim(), PreferredRemedy = preferred,
                Source = source, OpenedAt = now, OpenedBy = actorId,
            };
            foreach (var request in lines.Where(l => l.Quantity > 0))
            {
                var line = o.Lines.FirstOrDefault(l => l.LineId == request.LineId && l.Status != OrderLineStatus.Refused)
                    ?? throw CheckoutException.Of(CheckoutRefusals.LineNotFound, request.LineId);
                var ledgerLine = ledger.Lines.First(l => l.LineId == line.LineId);
                var until = GuaranteeEnd(line, ledgerLine);
                var reason = !line.IsPhysicalGoods ? "notGoods" : until is null ? "notDelivered" : now >= until ? "periodEnded" : null;
                var quantity = Math.Min(request.Quantity, line.Quantity - ledgerLine.QuantityReturned);
                if (quantity <= 0)
                    continue;
                claim.Lines.Add(new CheckoutReturnLine
                {
                    LineId = line.LineId, Quantity = quantity,
                    Units = request.Units?.ToList() ?? [], Eligible = reason is null, IneligibleReason = reason, Shipped = line.ShippedAt is not null,
                });
            }
            if (claim.Lines.Count == 0)
                throw CheckoutException.Of(RefundRefusals.NothingLeft);
            ledger.Claims.Add(claim);
            o.Log.Add(new CheckoutOrderLogEntry { At = now, Event = "guarantee-claim", Detail = claim.ClaimId, ActorId = actorId });
            raised.Add(new OrderEvent(o, OrderEventKind.GuaranteeClaimOpened, null, claim.ClaimId));
            return Task.CompletedTask;
        }, cancellationToken);
        return await ViewAsync(updated, cancellationToken);
    }

    /// <summary>
    /// GA8: the seller's remedy. Repair and replacement need no money; price reduction (Art. 15) and termination (Art. 16)
    /// need one of the Art. 13(4) grounds. Termination refunds once the goods are back or dispatch is proven (Art. 16(3)).
    /// ⚠️ Art. 13(5) (no termination for a minor lack of conformity) is the staff member's judgement: it is not checked.
    /// </summary>
    internal async Task<SparkOrderAftercare> ResolveClaimAsync(CheckoutOrder order, string claimId, GuaranteeRemedy remedy, GuaranteeGround? ground, Money? amount, string? note,
        string actorId, CancellationToken cancellationToken)
    {
        if (remedy is GuaranteeRemedy.PriceReduction or GuaranteeRemedy.Refund && ground is null)
            throw CheckoutException.Of(RefundRefusals.GroundRequired);
        var updated = await UpdateAsync(order.Id!, async (session, o, ledger, raised) =>
        {
            var claim = ledger.Claims.FirstOrDefault(c => c.ClaimId == claimId) ?? throw CheckoutException.Of(CheckoutRefusals.LineNotFound, subject: claimId);
            if (claim.Status != GuaranteeClaimStatus.Open)
                throw CheckoutException.Of(CheckoutRefusals.InvalidState, subject: claim.Status.ToString());
            var now = Now;
            claim.Remedy = remedy;
            claim.Ground = ground;
            claim.ResolutionNote = note;
            claim.ResolvedBy = actorId;
            var rules = ReturnReasons.For(ReturnReasonCode.NonConformity);
            switch (remedy)
            {
                case GuaranteeRemedy.Repair or GuaranteeRemedy.Replacement:
                {
                    // Art. 14(2): the seller takes the goods back at its own expense.
                    var ret = ClaimReturn(o, claim, rules, now, actorId, ReturnStatus.Closed);
                    ledger.Returns.Add(ret);
                    claim.ReturnId = ret.ReturnId;
                    claim.Status = GuaranteeClaimStatus.Resolved;
                    claim.ResolvedAt = now;
                    RaiseReturnExpected(o, ret, raised);
                    raised.Add(new OrderEvent(o, OrderEventKind.GuaranteeClaimResolved, null, claim.ClaimId));
                    break;
                }
                case GuaranteeRemedy.PriceReduction:
                {
                    if (amount is not { } value || value.ToMinorUnits() <= 0)
                        throw CheckoutException.Of(RefundRefusals.AmountTooHigh);
                    var spec = new RefundSpec(RefundKind.Guarantee, ReturnReasonCode.NonConformity, [], Amount: value, AmountLineIds: claim.Lines.Select(l => l.LineId).ToList());
                    var refund = await engine.RefundAsync(session, o, ledger, spec, new RefundMeta(RefundEngine.StableId("p", claim.ClaimId), actorId, note, ClaimId: claim.ClaimId), raised, cancellationToken);
                    claim.RefundId = refund?.RefundId;
                    claim.Status = GuaranteeClaimStatus.Resolved;
                    claim.ResolvedAt = now;
                    raised.Add(new OrderEvent(o, OrderEventKind.GuaranteeClaimResolved, null, claim.ClaimId));
                    break;
                }
                case GuaranteeRemedy.Refund:
                {
                    var ret = ClaimReturn(o, claim, rules, now, actorId, ReturnStatus.Requested);
                    foreach (var line in ret.Lines)
                    {
                        var ledgerLine = ledger.Lines.First(l => l.LineId == line.LineId);
                        line.Quantity = Math.Min(line.Quantity, RefundEngine.Free(o.Lines.First(l => l.LineId == line.LineId), ledgerLine));
                        ledgerLine.QuantityPending += line.Quantity;
                    }
                    ret.Lines.RemoveAll(l => l.Quantity == 0);
                    if (ret.Lines.Count == 0)
                        throw CheckoutException.Of(RefundRefusals.NothingLeft);
                    ledger.Returns.Add(ret);
                    claim.ReturnId = ret.ReturnId;
                    var now2 = ret.Lines.Where(l => !l.Shipped).ToList();
                    if (now2.Count > 0)
                        await RefundReturnLinesAsync(session, o, ledger, ret, now2, null, raised, actorId, cancellationToken);
                    if (ret.Lines.Any(l => l.Shipped))
                    {
                        ret.Status = ReturnStatus.AwaitingGoods;
                        claim.Status = GuaranteeClaimStatus.AwaitingGoods;
                        RaiseReturnExpected(o, ret, raised);
                    }
                    else
                    {
                        ret.Status = ReturnStatus.Refunded;
                        claim.Status = GuaranteeClaimStatus.Resolved;
                        claim.ResolvedAt = now;
                        claim.RefundId = ret.RefundIds.LastOrDefault();
                        raised.Add(new OrderEvent(o, OrderEventKind.GuaranteeClaimResolved, null, claim.ClaimId));
                    }
                    break;
                }
            }
            await audit.RecordAsync(session, new CommerceAuditEntry
            {
                Action = RefundAuditActions.GuaranteeResolved, SubjectId = o.Id!, OrganizationId = o.Sellers.FirstOrDefault(), ActorId = actorId, At = now,
                Reason = remedy + (ground is { } g ? "/" + g : ""), After = amount, Data = new Dictionary<string, string> { ["claimId"] = claim.ClaimId },
            }, cancellationToken);
        }, cancellationToken);
        await MarkIssuedDeliveredAsync(updated, cancellationToken);
        return await ViewAsync(updated, cancellationToken);
    }

    private static CheckoutReturn ClaimReturn(CheckoutOrder order, CheckoutGuaranteeClaim claim, ReturnReasonRules rules, DateTimeOffset now, string? actorId, ReturnStatus status) => new()
    {
        ReturnId = RefundEngine.StableId("t", claim.ClaimId), Reason = ReturnReasonCode.NonConformity, Status = status, Source = ReturnSource.Staff,
        ReceivedAt = now, RecordedAt = now, RecordedBy = actorId, ClaimId = claim.ClaimId, DefaultDisposition = rules.DefaultDisposition,
        SellerPaysReturnShipping = true,
        Lines = claim.Lines.Select(l => new CheckoutReturnLine
        {
            LineId = l.LineId, Quantity = l.Quantity, Units = [.. l.Units], Eligible = true, Shipped = order.Lines.First(x => x.LineId == l.LineId).ShippedAt is not null,
        }).ToList(),
    };

    // ---- reschedule (D20) ---------------------------------------------------------------------------------------------

    internal async Task<SparkOrderAftercare> ChooseRescheduleAsync(CheckoutOrder order, string windowId, string choice, string? actorId, CancellationToken cancellationToken)
    {
        CheckoutRescheduleWindow? window;
        using (var session = store.OpenAsyncSession())
            window = await session.LoadAsync<CheckoutRescheduleWindow>(windowId, cancellationToken);
        var now = Now;
        if (window is null || now >= window.ClosesAt || now < window.OpensAt || !order.Lines.Any(l => window.ItemIds.Contains(l.ItemId)))
            throw CheckoutException.Of(RefundRefusals.WindowClosed);
        var refund = string.Equals(choice, "refund", StringComparison.OrdinalIgnoreCase);
        var updated = await UpdateAsync(order.Id!, async (session, o, ledger, raised) =>
        {
            if (ledger.RescheduleChoices.TryGetValue(windowId, out var made) && made == "refund")
                return; // a refund already chosen stands
            ledger.RescheduleChoices[windowId] = refund ? "refund" : "keep";
            if (!refund)
                return;
            var lines = o.Lines.Where(l => l.Status != OrderLineStatus.Refused && window.ItemIds.Contains(l.ItemId))
                .Select(l => new RefundLineRequest(l.LineId, RefundEngine.Free(l, ledger.Lines.First(x => x.LineId == l.LineId)))).Where(l => l.Quantity > 0).ToList();
            if (lines.Count == 0)
                return;
            // The seller changed the date: everything paid for those lines comes back, fees included.
            await engine.RefundAsync(session, o, ledger, new RefundSpec(RefundKind.Rescheduled, null, lines, RefundFees: true, Delivery: DeliveryRefund.Full, IncludeOrderExtras: true),
                new RefundMeta(RefundEngine.StableId("r", windowId), actorId), raised, cancellationToken);
        }, cancellationToken);
        await MarkIssuedDeliveredAsync(updated, cancellationToken);
        return await ViewAsync(updated, cancellationToken);
    }

    // ---- retries and manual settlement (D20) --------------------------------------------------------------------------

    internal async Task<SparkOrderAftercare> RetryAsync(string orderId, string refundId, CancellationToken cancellationToken)
    {
        var updated = await UpdateAsync(orderId, async (session, order, ledger, raised) =>
        {
            var refund = ledger.Refunds.FirstOrDefault(r => r.RefundId == refundId) ?? throw CheckoutException.Of(RefundRefusals.RefundState, subject: refundId);
            if (refund.Status != RefundStatus.Failed)
                throw CheckoutException.Of(RefundRefusals.RefundState, subject: refund.Status.ToString());
            await engine.PayAsync(session, order, ledger, refund, refund.Attempt + 1, raised, cancellationToken);
        }, cancellationToken);
        await MarkIssuedDeliveredAsync(updated, cancellationToken);
        return await ViewAsync(updated, cancellationToken);
    }

    /// <summary>Finance paid it back outside the provider (a bank transfer): the manual fallback of D20, audited.</summary>
    internal async Task<SparkOrderAftercare> SettleManuallyAsync(string orderId, string refundId, string? note, string actorId, CancellationToken cancellationToken)
    {
        var updated = await UpdateAsync(orderId, async (session, order, ledger, raised) =>
        {
            var refund = ledger.Refunds.FirstOrDefault(r => r.RefundId == refundId) ?? throw CheckoutException.Of(RefundRefusals.RefundState, subject: refundId);
            if (refund.Status is not (RefundStatus.Failed or RefundStatus.ManualRequired))
                throw CheckoutException.Of(RefundRefusals.RefundState, subject: refund.Status.ToString());
            refund.SettledBy = actorId;
            refund.Note = note ?? refund.Note;
            await engine.TransitionAsync(session, order, ledger, refund, refund.Status, RefundStatus.SettledManually, null, raised, moveOrder: true, cancellationToken);
            await audit.RecordAsync(session, new CommerceAuditEntry
            {
                Action = RefundAuditActions.RefundSettledManually, SubjectId = order.Id!, OrganizationId = order.Sellers.FirstOrDefault(), After = refund.Amount,
                ActorId = actorId, At = Now, Reason = note, Data = new Dictionary<string, string> { ["refundId"] = refund.RefundId },
            }, cancellationToken);
        }, cancellationToken);
        await MarkIssuedDeliveredAsync(updated, cancellationToken);
        return await ViewAsync(updated, cancellationToken);
    }

    /// <summary>
    /// <c>RefundIssued</c> was published after the save: mark it delivered (the sweep re-delivers what is not). A crash in
    /// between makes a recipient hear it twice; recipients are idempotent by the refund id.
    /// </summary>
    internal async Task MarkIssuedDeliveredAsync(CheckoutOrder? order, CancellationToken cancellationToken)
    {
        if (order?.Id is not { } orderId)
            return;
        using var session = store.OpenAsyncSession();
        session.Advanced.OptimisticConcurrencyMode = OptimisticConcurrencyMode.Writes;
        var ledger = await session.LoadAsync<CheckoutRefundLedger>(CheckoutRefundLedger.IdFor(orderId), cancellationToken);
        if (ledger is null)
            return;
        var issued = ledger.Refunds.Where(r => r.IssuedAt is not null && r.IssuedDeliveredAt is null).ToList();
        if (issued.Count == 0)
            return;
        foreach (var refund in issued)
        {
            refund.IssuedDeliveredAt = Now;
            await mails.SendRefundIssuedAsync(order, refund, cancellationToken); // deduplicated per refund
        }
        try
        {
            await session.SaveChangesAsync(cancellationToken);
        }
        catch (Raven.Client.Exceptions.ConcurrencyException)
        {
            // The ledger moved on meanwhile: the sweep marks it next time (a recipient may hear the event twice).
        }
    }
}
