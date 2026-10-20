using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Commerce.Checkout.Models;
using MintPlayer.Spark.Commerce.Inventory;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Commerce.Checkout.Refunds;

/// <summary>Units of a line a refund request names.</summary>
/// <param name="Units">Seats to give back; null takes the first ones not given back yet.</param>
public sealed record RefundLineRequest(string LineId, int Quantity, IReadOnlyList<string>? Units = null);

/// <summary>What a refund is asked to do (input of <see cref="RefundMath.Plan"/>).</summary>
/// <param name="Lines">Units to give back (cumulative-share refunds, S21).</param>
/// <param name="Amount">An amount-only refund (a price reduction, goodwill): spread over <paramref name="AmountLineIds"/> (or every line) by what each can still refund.</param>
/// <param name="RefundFees">Whether the lines' fees are refunded with their price.</param>
/// <param name="Delivery">How the delivery extra is refunded.</param>
/// <param name="FeeBasisPoints">A cancellation fee kept from the price share (D20 <c>WithFee</c>).</param>
/// <param name="Deductions">Amounts kept per line from the price share (diminished value, CRD Art. 14(2)).</param>
/// <param name="IncludeOrderExtras">Whether donation, insurance and other extras are refunded when this refund leaves nothing of the order (GC3).</param>
internal sealed record RefundSpec(
    RefundKind Kind,
    ReturnReasonCode? Reason,
    IReadOnlyList<RefundLineRequest> Lines,
    Money? Amount = null,
    IReadOnlyList<string>? AmountLineIds = null,
    bool RefundFees = false,
    DeliveryRefund Delivery = DeliveryRefund.None,
    PartialDeliveryRefund PartialDelivery = PartialDeliveryRefund.WhenComplete,
    int FeeBasisPoints = 0,
    IReadOnlyDictionary<string, Money>? Deductions = null,
    bool IncludeOrderExtras = false);

/// <summary>A planned refund: the parts per component, the units per line, the totals.</summary>
internal sealed record RefundPlan(IReadOnlyList<RefundPart> Parts, IReadOnlyList<RefundUnits> Lines, Money Amount, Money Deducted, bool CompletesOrder)
{
    public bool IsEmpty => Parts.All(p => p.Gross.IsZero && p.Deducted.IsZero) && Lines.All(l => l.Quantity == 0);
}

/// <summary>
/// The refund arithmetic (PRD §4.7, spike S21): integer cents, <see cref="Money.RoundDiv(long, long)"/> once per quotient,
/// <see cref="Money.Allocate(IReadOnlyList{long})"/> for spreading, and <b>cumulative shares</b>: returning units of a
/// line settles <c>share(gross, unitsReturnedAfter, quantity) − settledBefore</c>, so any sequence of partial refunds
/// sums exactly to the line (and a full return settles exactly what is left). Pure: the ledger is changed only by
/// <see cref="Apply"/>.
/// </summary>
internal static class RefundMath
{
    public const string Price = "price";
    public const string Fee = "fee";
    public const string Extra = "extra";
    public const string DeliveryKind = "delivery";

    public static string PriceKey(string lineId) => $"line:{lineId}:price";

    public static string FeeKey(string lineId, string code) => $"line:{lineId}:fee:{code}";

    public static string ExtraKey(string code) => "extra:" + code;

    /// <summary>Whether an extra is delivery when no <see cref="IStandardDeliveryCost"/> knows (code <c>delivery</c> or <c>delivery:…</c>).</summary>
    public static bool IsDeliveryCode(string code)
        => string.Equals(code, DeliveryKind, StringComparison.OrdinalIgnoreCase) || code.StartsWith(DeliveryKind + ":", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Every refundable component of an order. Refused lines are left out (checkout refunded them at confirmation, D40).
    /// A line's price part is what the buyer really paid for its goods: total − fees + the order-discount share stored on
    /// the line at pricing (S21, <see cref="CheckoutOrderLine.OrderDiscount"/>), so it equals the line's <c>Net + Vat</c>
    /// and the gross Invoicing put on the invoice. Any other negative extra (not a <see cref="PricedExtraKind.Discount"/>)
    /// is still allocated over the price parts by largest remainder.
    /// </summary>
    public static List<RefundComponent> Components(CheckoutOrder order, IReadOnlyDictionary<string, Money?>? standardDelivery = null)
    {
        var currency = order.Currency;
        var lines = order.Lines.Where(l => l.Status != OrderLineStatus.Refused).ToList();
        var priceParts = lines.Select(l => l.ChargedGoods().ToMinorUnits()).ToList();
        var discount = Money.Sum(order.Extras.Where(e => e.Kind != PricedExtraKind.Discount && e.Amount.ToMinorUnits() < 0).Select(e => e.Amount), currency);
        if (!discount.IsZero && priceParts.Any(p => p > 0))
        {
            var shares = Money.Allocate(discount.ToMinorUnits(), priceParts.Select(p => Math.Max(0, p)).ToList());
            for (var i = 0; i < priceParts.Count; i++)
                priceParts[i] += shares[i];
        }

        var components = new List<RefundComponent>();
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            components.Add(New(PriceKey(line.LineId), line.LineId, Price, null, null, Money.FromMinorUnits(Math.Max(0, priceParts[i]), currency)));
            foreach (var fee in line.Fees.Where(f => f.Amount.ToMinorUnits() > 0))
                components.Add(New(FeeKey(line.LineId, fee.Code), line.LineId, Fee, fee.Code, fee.Kind, fee.Amount));
        }
        foreach (var extra in order.Extras.Where(e => e.Amount.ToMinorUnits() > 0))
        {
            var isDelivery = standardDelivery?.ContainsKey(extra.Code) == true || IsDeliveryCode(extra.Code);
            var component = New(ExtraKey(extra.Code), null, Extra, extra.Code, isDelivery ? DeliveryKind : extra.Kind.ToString(), extra.Amount);
            if (isDelivery)
            {
                var standard = standardDelivery is not null && standardDelivery.TryGetValue(extra.Code, out var s) && s is { } known ? known : extra.Amount;
                component.StandardGross = standard > extra.Amount ? extra.Amount : standard;
            }
            components.Add(component);
        }
        return components;

        RefundComponent New(string key, string? lineId, string part, string? code, string? kind, Money gross) => new()
        {
            Key = key, LineId = lineId, Part = part, Code = code, ExtraKind = kind, Gross = gross,
            Settled = Money.Zero(currency), Refunded = Money.Zero(currency), Deducted = Money.Zero(currency),
        };
    }

    /// <summary>
    /// <c>gross × units / quantity</c>, rounded once; the whole gross when every unit is in (S21 cumulative share).
    /// </summary>
    public static Money CumulativeShare(Money gross, int units, int quantity)
    {
        if (quantity <= 0 || units >= quantity)
            return gross;
        if (units <= 0)
            return Money.Zero(gross.Currency);
        return gross.Share(units, quantity);
    }

    /// <summary>Plans a refund against the ledger. Throws <see cref="CheckoutException"/> for a request it cannot honour.</summary>
    public static RefundPlan Plan(CheckoutOrder order, CheckoutRefundLedger ledger, RefundSpec spec)
    {
        var currency = order.Currency;
        var zero = Money.Zero(currency);
        var parts = new Dictionary<string, RefundPart>(StringComparer.Ordinal);
        var units = new List<RefundUnits>();
        var returnedAfter = ledger.Lines.ToDictionary(l => l.LineId, l => l.QuantityReturned, StringComparer.Ordinal);

        RefundPart PartOf(RefundComponent c)
        {
            if (!parts.TryGetValue(c.Key, out var part))
                parts[c.Key] = part = new RefundPart { Key = c.Key, Gross = zero, Deducted = zero, SettledBefore = c.Settled };
            return part;
        }

        // ---- units (cumulative shares) ----
        foreach (var request in spec.Lines.GroupBy(l => l.LineId, StringComparer.Ordinal).Select(g => new RefundLineRequest(g.Key, g.Sum(x => x.Quantity), g.SelectMany(x => x.Units ?? []).ToList() is { Count: > 0 } u ? u : null)))
        {
            var line = order.Lines.FirstOrDefault(l => l.LineId == request.LineId && l.Status != OrderLineStatus.Refused)
                ?? throw CheckoutException.Of(CheckoutRefusals.LineNotFound, request.LineId);
            var ledgerLine = ledger.Lines.First(l => l.LineId == line.LineId);
            var before = ledgerLine.QuantityReturned;
            if (request.Quantity < 1 || before + request.Quantity > line.Quantity)
                throw CheckoutException.Of(CheckoutRefusals.InvalidQuantity, line.LineId);
            var after = before + request.Quantity;
            returnedAfter[line.LineId] = after;
            units.Add(new RefundUnits { LineId = line.LineId, Quantity = request.Quantity, Units = PickUnits(line, ledgerLine, request) });

            foreach (var component in ledger.Components.Where(c => c.LineId == line.LineId))
            {
                if (component.Part == Fee && !spec.RefundFees)
                    continue;
                var target = CumulativeShare(component.Gross, after, line.Quantity);
                var amount = Clamp(target - component.Settled, zero, component.Remaining());
                if (amount.IsZero)
                    continue;
                var part = PartOf(component);
                var kept = zero;
                if (component.Part == Price)
                {
                    if (spec.FeeBasisPoints > 0)
                        kept += amount.Share(spec.FeeBasisPoints, 10_000);
                    if (spec.Deductions is not null && spec.Deductions.TryGetValue(line.LineId, out var deduction) && deduction.ToMinorUnits() > 0)
                        kept += deduction;
                    if (kept > amount)
                        throw CheckoutException.Of(RefundRefusals.DeductionTooHigh, line.LineId);
                }
                part.Gross += amount - kept;
                part.Deducted += kept;
            }
        }

        // ---- an amount spread over what is left (price reduction, goodwill) ----
        if (spec.Amount is { } requested && requested.ToMinorUnits() > 0)
        {
            var targets = ledger.Components
                .Where(c => c.Part == Price && (spec.AmountLineIds is null || spec.AmountLineIds.Contains(c.LineId!)))
                .ToList();
            var remaining = targets.Select(c => (c.Remaining() - (parts.TryGetValue(c.Key, out var p) ? p.Gross + p.Deducted : zero)).ToMinorUnits()).ToList();
            if (requested.ToMinorUnits() > remaining.Sum())
                throw CheckoutException.Of(RefundRefusals.AmountTooHigh, subject: Money.FromMinorUnits(remaining.Sum(), currency).ToString());
            var shares = requested.Allocate(remaining);
            for (var i = 0; i < targets.Count; i++)
                if (!shares[i].IsZero)
                    PartOf(targets[i]).Gross += shares[i];
        }

        // ---- extras ----
        var lines = order.Lines.Where(l => l.Status != OrderLineStatus.Refused).ToList();
        var completesOrder = lines.All(l => returnedAfter.GetValueOrDefault(l.LineId) >= l.Quantity);
        var goods = lines.Where(l => l.IsPhysicalGoods).ToList();
        var completesGoods = goods.Count > 0 && goods.All(l => returnedAfter.GetValueOrDefault(l.LineId) >= l.Quantity);
        var goodsUnits = goods.Sum(l => l.Quantity);
        var goodsReturned = goods.Sum(l => Math.Min(l.Quantity, returnedAfter.GetValueOrDefault(l.LineId)));
        var touchesGoods = units.Any(u => goods.Any(g => g.LineId == u.LineId));

        foreach (var component in ledger.Components.Where(c => c.Part == Extra))
        {
            Money target;
            if (component.ExtraKind == DeliveryKind)
            {
                if (spec.Delivery == DeliveryRefund.None || !touchesGoods && !completesOrder)
                    continue;
                var basis = spec.Delivery == DeliveryRefund.Standard ? component.StandardGross ?? component.Gross : component.Gross;
                target = completesGoods || (goods.Count == 0 && completesOrder) ? basis
                    : spec.PartialDelivery == PartialDeliveryRefund.ProRata ? CumulativeShare(basis, goodsReturned, goodsUnits)
                    : zero;
            }
            else
            {
                if (!spec.IncludeOrderExtras || !completesOrder)
                    continue;
                target = component.Gross;
            }
            var amount = Clamp(target - component.Settled, zero, component.Remaining());
            if (!amount.IsZero)
                PartOf(component).Gross += amount;
        }

        var all = parts.Values.Where(p => !p.Gross.IsZero || !p.Deducted.IsZero).ToList();
        return new RefundPlan(all, units, Money.Sum(all.Select(p => p.Gross), currency), Money.Sum(all.Select(p => p.Deducted), currency), completesOrder);
    }

    /// <summary>Applies a plan to the ledger: settled amounts and returned units.</summary>
    public static void Apply(CheckoutRefundLedger ledger, RefundPlan plan)
    {
        foreach (var part in plan.Parts)
        {
            var component = ledger.Components.First(c => c.Key == part.Key);
            component.Refunded += part.Gross;
            component.Deducted += part.Deducted;
            component.Settled += part.Gross + part.Deducted;
        }
        foreach (var line in plan.Lines)
        {
            var ledgerLine = ledger.Lines.First(l => l.LineId == line.LineId);
            ledgerLine.QuantityReturned += line.Quantity;
            ledgerLine.UnitsReturned.AddRange(line.Units);
        }
    }

    private static List<string> PickUnits(CheckoutOrderLine line, RefundLedgerLine ledgerLine, RefundLineRequest request)
    {
        if (line.Units.Count == 0)
            return [];
        var free = line.Units.Where(u => !ledgerLine.UnitsReturned.Contains(u)).ToList();
        if (request.Units is { Count: > 0 } named)
        {
            if (named.Count != request.Quantity || named.Any(u => !free.Contains(u)))
                throw CheckoutException.Of(CheckoutRefusals.UnitTaken, line.LineId);
            return named.ToList();
        }
        return free.Take(request.Quantity).ToList();
    }

    private static Money Clamp(Money value, Money min, Money max) => value < min ? min : value > max ? max : value;

    // ---- the inventory side --------------------------------------------------------------------------------------

    /// <summary>
    /// The inventory return for units of a line (I4): everything when the line's last units go back in one piece, else
    /// the items and quotas scaled to the units, the seats, and the per-buyer counts (which a partial return only gives
    /// back when named). Shipped goods return no quota until restock.
    /// </summary>
    /// <param name="returnStock">Whether the quota units go back on sale now; null: unless the line shipped. False for a line a stock feed backs (<see cref="ICheckoutStockFeed"/>).</param>
    public static InventoryReturn InventoryReturnFor(CheckoutOrderLine line, RefundUnits units, int returnedBefore, string returnId, ReturnReason reason, bool? returnStock = null)
    {
        var stock = returnStock ?? line.ShippedAt is null;
        if (returnedBefore == 0 && units.Quantity >= line.Quantity)
            return new InventoryReturn(returnId, reason, ReturnStock: stock);
        int Scale(int n) => (int)Money.RoundDiv((long)n * units.Quantity, line.Quantity);
        var items = line.Capacity.Items.Select(i => new ItemLine(i.ItemId, Scale(i.N))).Where(i => i.N > 0).ToList();
        var quotas = line.Capacity.Quotas.Select(q => new QuotaLine(q.QuotaId, Scale(q.N))).Where(q => q.N > 0).ToList();
        var limits = line.Limits.Select(l => new LimitLine(l.Key, l.Max, Scale(l.N))).Where(l => l.N > 0).ToList();
        // Seats come back by unit; a seated line's capacity is its seats, not an item count.
        var seated = units.Units.Count > 0;
        return new InventoryReturn(returnId, reason,
            Items: seated ? null : items,
            Quotas: seated ? null : quotas,
            Units: seated ? units.Units : null,
            Limits: limits,
            ReturnStock: stock);
    }

    // ---- policies and periods ------------------------------------------------------------------------------------

    /// <summary>A seller policy clamped to the channel's bounds (D20): a kind not allowed falls back to the first allowed one.</summary>
    public static RefundPolicy Clamp(RefundPolicy? policy, RefundPolicyBounds bounds)
    {
        List<RefundPolicyKind> allowed = bounds.Allowed.Count == 0 ? [RefundPolicyKind.None] : bounds.Allowed;
        var kind = policy is { } p && allowed.Contains(p.Kind) ? p.Kind : allowed.Contains(RefundPolicyKind.None) ? RefundPolicyKind.None : allowed[0];
        return kind switch
        {
            RefundPolicyKind.UntilDaysBefore => new RefundPolicy(kind, DaysBefore: bounds.DaysBefore.Clamp(policy?.DaysBefore)),
            RefundPolicyKind.WithFee => new RefundPolicy(kind, DaysBefore: policy?.DaysBefore, FeeBasisPoints: Math.Clamp(policy?.FeeBasisPoints ?? bounds.MaxFeeBasisPoints, 0, bounds.MaxFeeBasisPoints)),
            _ => RefundPolicy.None,
        };
    }

    /// <summary>Whether the policy lets the buyer cancel now, and the fee kept (basis points).</summary>
    /// <returns><c>null</c> when allowed, else the refusal code.</returns>
    public static string? CancellationRefusal(RefundPolicy policy, DateTimeOffset? serviceDate, DateTimeOffset now, out int feeBasisPoints)
    {
        feeBasisPoints = 0;
        if (serviceDate is { } passed && passed <= now)
            return RefundRefusals.ServicePassed;
        switch (policy.Kind)
        {
            case RefundPolicyKind.None:
                return RefundRefusals.NoCancellation;
            case RefundPolicyKind.UntilDaysBefore:
                if (serviceDate is { } date && now > date.AddDays(-(policy.DaysBefore ?? 0)))
                    return RefundRefusals.CancellationClosed;
                return null;
            case RefundPolicyKind.WithFee:
                if (policy.DaysBefore is { } days && serviceDate is { } d && now > d.AddDays(-days))
                    return RefundRefusals.CancellationClosed;
                feeBasisPoints = policy.FeeBasisPoints ?? 0;
                return null;
            default:
                return RefundRefusals.NoCancellation;
        }
    }

    /// <summary>
    /// The end (exclusive) of a period of <paramref name="days"/> days counted from the day after <paramref name="from"/>,
    /// in <paramref name="zone"/>: Regulation (EEC, Euratom) No 1182/71 Art. 3(1) (the day of the event is not counted),
    /// 3(2)(b) (the period ends at the end of its last day) and 3(4) (a last day on a Saturday or Sunday moves to the next
    /// working day). Public holidays (also Art. 3(4)) move it too when <paramref name="isHoliday"/> is given (the optional
    /// holiday calendar, <see cref="ICheckoutHolidayCalendar"/>); without one, only weekends do.
    /// </summary>
    public static DateTimeOffset PeriodEnd(DateTimeOffset from, int days, TimeZoneInfo zone, bool moveOffWeekend = true, Func<DateOnly, bool>? isHoliday = null)
    {
        var local = SparkTimeZones.ToZone(from, zone).Date;
        var last = local.AddDays(days);
        if (moveOffWeekend)
            while (last.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday || isHoliday?.Invoke(DateOnly.FromDateTime(last)) == true)
                last = last.AddDays(1);
        return SparkTimeZones.ToInstant(DateTime.SpecifyKind(last.AddDays(1), DateTimeKind.Unspecified), zone);
    }

    /// <summary>
    /// When the withdrawal period starts: CRD Art. 9(2)(b)(i): for several goods of one order delivered separately, the day
    /// the buyer receives the last of them. Null while a goods line is undelivered (the right is live, Art. 9(3)).
    /// </summary>
    public static DateTimeOffset? WithdrawalPeriodStart(CheckoutOrder order, CheckoutRefundLedger ledger)
    {
        var goods = order.Lines.Where(l => l.Status != OrderLineStatus.Refused && l.IsPhysicalGoods).Select(l => l.LineId).ToHashSet();
        var delivered = ledger.Lines.Where(l => goods.Contains(l.LineId)).ToList();
        if (delivered.Count == 0 || delivered.Any(l => l.DeliveredAt is null))
            return null;
        return delivered.Max(l => l.DeliveredAt);
    }
}
