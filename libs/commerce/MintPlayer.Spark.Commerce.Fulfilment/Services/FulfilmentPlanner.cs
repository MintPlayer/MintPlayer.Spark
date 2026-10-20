using MintPlayer.Spark.Commerce.Fulfilment.Models;
using MintPlayer.Spark.Fulfilment.Contracts;

namespace MintPlayer.Spark.Commerce.Fulfilment.Services;

/// <summary>A goods line of the shop order as Checkout has it now.</summary>
/// <param name="LineId">Checkout's line id (also the fulfilment line id).</param>
/// <param name="Quantity">The units sold.</param>
/// <param name="RefundedBeforeShipment">Units refunded before the line shipped (a cancellation): they must not ship.</param>
/// <param name="Gone">Refused at confirmation, or returned in full before it shipped (a lost dispute): nothing ships.</param>
internal sealed record PlannedLine(string LineId, int Quantity, int RefundedBeforeShipment, bool Gone)
{
    /// <summary>The units the buyer should still receive, shipped or not.</summary>
    public int Desired => Gone ? 0 : Math.Max(0, Quantity - RefundedBeforeShipment);
}

/// <summary>A published fulfilment order with what the warehouse said about it.</summary>
/// <param name="Revision">The shop's record of it.</param>
/// <param name="Published">The published document (null only if it was lost; then it counts as not answered).</param>
/// <param name="Status">The warehouse's <c>{id}/status</c> replica, or null while it has not answered.</param>
/// <param name="Shipments">The warehouse's shipment replicas of this order.</param>
internal sealed record RevisionView(FulfilmentRevision Revision, FulfilmentOrder? Published, FulfilmentOrderStatus? Status, IReadOnlyList<FulfilmentShipment> Shipments)
{
    public bool Rejected => Status?.State == FulfilmentOrderState.Rejected;

    /// <summary>The warehouse accepted the shop's cancellation (or cancelled the whole order).</summary>
    public bool CancellationAccepted => Status?.Cancellation == FulfilmentCancellationAnswer.Accepted || Status?.State == FulfilmentOrderState.Cancelled;

    public bool CancellationRefused => Status?.Cancellation == FulfilmentCancellationAnswer.Refused;

    /// <summary>Asked to cancel and not answered yet: nothing else changes until the warehouse answers (one cancellation per revision).</summary>
    public bool CancellationPending => Revision.CancellationRequestedAt is not null && Status?.Cancellation is null
        && Status?.State is not (FulfilmentOrderState.Rejected or FulfilmentOrderState.Cancelled or FulfilmentOrderState.Shipped);

    public int Asked(string lineId) => Revision.Lines.Where(l => l.LineId == lineId).Sum(l => l.Quantity);

    /// <summary>Cumulative shipped units of a line: the status's counter or the shipments', whichever is ahead (they may arrive in any order).</summary>
    public int Shipped(string lineId)
    {
        var byStatus = Status?.Lines.Where(l => l.LineId == lineId).Sum(l => l.Shipped) ?? 0m;
        var byShipments = Shipments.SelectMany(s => s.Lines).Where(l => l.LineId == lineId).Sum(l => l.Quantity);
        return Units(Math.Max(byStatus, byShipments));
    }

    public int Cancelled(string lineId) => Units(Status?.Lines.Where(l => l.LineId == lineId).Sum(l => l.Cancelled) ?? 0m);

    /// <summary>Units the warehouse may still ship: none once it rejected the order or accepted its cancellation.</summary>
    public int Outstanding(string lineId)
        => Rejected || CancellationAccepted ? 0 : Math.Max(0, Asked(lineId) - Shipped(lineId) - Cancelled(lineId));

    /// <summary>Units the warehouse cancelled without being asked (a cancelled remainder, §4.3).</summary>
    public int Undeliverable(string lineId) => Rejected || CancellationAccepted ? 0 : Cancelled(lineId);

    internal static int Units(decimal quantity) => quantity <= 0 ? 0 : (int)Math.Floor(quantity);
}

/// <summary>The figures of one line across every revision.</summary>
/// <param name="Desired">Units the buyer should receive (<see cref="PlannedLine.Desired"/>).</param>
/// <param name="Shipped">Units shipped, over every revision.</param>
/// <param name="AtRisk">Units the warehouse may still ship.</param>
/// <param name="Undeliverable">Units the warehouse cancelled on its own and nobody refunded yet.</param>
/// <param name="Needed">Units still to ship: <c>Desired − Shipped − Undeliverable</c>.</param>
/// <param name="Restockable">Units refunded before shipment that can no longer ship: their quota may go back on sale (§4.4 rule 1).</param>
internal sealed record LineFigures(int Desired, int Shipped, int AtRisk, int Undeliverable, int Needed, int Restockable);

/// <summary>What the shop does next for one shop order.</summary>
/// <param name="CancelRevisionIds">Revisions to ask the warehouse to cancel (the whole order: an edit is a cancellation plus a new order).</param>
/// <param name="NewRevision">The lines of a new fulfilment order to publish; empty for none.</param>
/// <param name="Waiting">A cancellation is unanswered: nothing is published until the warehouse answers.</param>
/// <param name="Lines">The figures per line.</param>
/// <param name="Attention">Why staff should look at it, or null.</param>
internal sealed record FulfilmentPlan(
    IReadOnlyList<string> CancelRevisionIds,
    IReadOnlyList<FulfilmentLinkLine> NewRevision,
    bool Waiting,
    IReadOnlyDictionary<string, LineFigures> Lines,
    FulfilmentAttention? Attention)
{
    /// <summary>Nothing left to ship, to cancel or to wait for.</summary>
    public bool Settled => !Waiting && CancelRevisionIds.Count == 0 && NewRevision.Count == 0 && Lines.Values.All(l => l.Needed == 0 && l.AtRisk == 0);
}

/// <summary>
/// The shop's fulfilment decisions, derived from state only (warehouse §4.3 "idempotent and order-insensitive"): what the
/// buyer should receive, what the warehouse shipped, may still ship and cancelled. Pure.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Cancellation is a status</b>, never a delete: the shop sets <c>CancellationRequested</c> on its own order and
/// the warehouse answers in its status (WD2). An edit (fewer units, another address) is a cancellation plus a new order,
/// published only once the cancellation was accepted, for what is still needed.</item>
/// <item><b>Rule 1 of §4.4</b>: units refunded before shipment go back on sale only once they can no longer ship (the
/// cancellation was accepted, or the warehouse cancelled them itself), never on the request; a unit that shipped
/// anyway stays sold (the warehouse's <c>ShippedTotal</c> counts it).</item>
/// <item>A rejected order blocks new ones until staff acknowledge it; a refused cancellation is never asked again.</item>
/// </list>
/// </remarks>
internal static class FulfilmentPlanner
{
    public static FulfilmentPlan Plan(IReadOnlyList<PlannedLine> lines, IReadOnlyList<RevisionView> revisions, string? addressKey, DateTimeOffset now)
    {
        var figures = new Dictionary<string, LineFigures>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            var shipped = revisions.Sum(r => r.Shipped(line.LineId));
            var atRisk = revisions.Sum(r => r.Outstanding(line.LineId));
            var undeliverable = Math.Max(0, revisions.Sum(r => r.Undeliverable(line.LineId)) - line.RefundedBeforeShipment);
            var desired = line.Desired;
            var needed = Math.Max(0, desired - shipped - undeliverable);
            // Refunded before shipment, minus what may still ship beyond the need, minus what shipped beyond the desire.
            var restockable = Math.Max(0, line.RefundedBeforeShipment - Math.Max(0, atRisk - needed) - Math.Max(0, shipped - desired));
            figures[line.LineId] = new LineFigures(desired, shipped, atRisk, undeliverable, needed, restockable);
        }

        // Units the warehouse holds for lines the order no longer has (cannot happen through Checkout; kept safe).
        var unknown = revisions.SelectMany(r => r.Revision.Lines.Select(l => l.LineId)).Where(id => !figures.ContainsKey(id)).Distinct(StringComparer.Ordinal)
            .Where(id => revisions.Sum(r => r.Outstanding(id)) > 0).ToList();

        var attention = Attention(revisions, figures, now);
        if (revisions.Any(r => r.CancellationPending))
            return new FulfilmentPlan([], [], Waiting: true, figures, attention);

        var live = revisions.Where(r => lines.Any(l => r.Outstanding(l.LineId) > 0) || unknown.Any(id => r.Outstanding(id) > 0)).ToList();
        var excess = figures.Values.Any(f => f.AtRisk > f.Needed) || unknown.Count > 0;
        var moved = addressKey is not null && live.Any(r => r.Published is { } p && AddressKey(p.ShipTo) != addressKey);
        if (excess || moved)
        {
            var cancel = live.Where(r => r.Revision.CancellationRequestedAt is null && !r.CancellationRefused && !r.Rejected).Select(r => r.Revision.Id).ToList();
            return new FulfilmentPlan(cancel, [], Waiting: false, figures, attention);
        }

        var blocked = revisions.Any(r => r.Rejected && !r.Revision.Acknowledged);
        var missing = lines.Select(l => (l.LineId, Delta: figures[l.LineId].Needed - figures[l.LineId].AtRisk)).Where(x => x.Delta > 0)
            .Select(x => new FulfilmentLinkLine { LineId = x.LineId, Quantity = x.Delta }).ToList();
        if (blocked || addressKey is null)
        {
            if (addressKey is null && missing.Count > 0 && attention is null)
                attention = new FulfilmentAttention { Kind = FulfilmentAttentionKind.MissingAddress, At = now };
            return new FulfilmentPlan([], [], Waiting: false, figures, attention);
        }
        return new FulfilmentPlan([], missing, Waiting: false, figures, attention);
    }

    private static FulfilmentAttention? Attention(IReadOnlyList<RevisionView> revisions, IReadOnlyDictionary<string, LineFigures> figures, DateTimeOffset now)
    {
        if (revisions.LastOrDefault(r => r.Rejected && !r.Revision.Acknowledged) is { } rejected)
            return new FulfilmentAttention
            {
                Kind = FulfilmentAttentionKind.Rejected,
                FulfilmentOrderId = rejected.Revision.Id,
                Detail = string.Join(": ", new[] { rejected.Status!.RejectionReason?.ToString(), rejected.Status.RejectionDetail }.Where(s => !string.IsNullOrEmpty(s))),
                At = now,
            };
        if (revisions.LastOrDefault(r => r.CancellationRefused && !r.Revision.Acknowledged) is { } refused)
            return new FulfilmentAttention
            {
                Kind = FulfilmentAttentionKind.CancellationRefused,
                FulfilmentOrderId = refused.Revision.Id,
                Detail = refused.Status!.CancellationRefusalReason,
                At = now,
            };
        var undeliverable = figures.Where(f => f.Value.Undeliverable > 0).Select(f => $"{f.Key} × {f.Value.Undeliverable}").ToList();
        return undeliverable.Count > 0
            ? new FulfilmentAttention { Kind = FulfilmentAttentionKind.Undeliverable, Detail = string.Join(", ", undeliverable), At = now }
            : null;
    }

    /// <summary>A comparable form of an address: an address change of a live order is an edit (warehouse §4.3).</summary>
    public static string AddressKey(FulfilmentAddress address)
        => string.Join("\u001f", address.Name, address.Company, address.Line1, address.Line2, address.PostalCode, address.City, address.Region,
            address.CountryCode, address.Phone, address.Email, address.ServicePointId);
}
