using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Commerce.Checkout.Models;
using MintPlayer.Spark.Commerce.Checkout.Refunds;
using MintPlayer.Spark.Commerce.Inventory;
using MintPlayer.Spark.Commerce.Ticketing.Events.Models;
using MintPlayer.Spark.Commerce.Ticketing.Events.Sales;
using MintPlayer.Spark.Commerce.Ticketing.Tickets.Models;
using MintPlayer.Spark.Commerce.Ticketing.Tickets.Secrets;
using MintPlayer.Spark.Commerce.Ticketing.Venues.Models;
using MintPlayer.Spark.Commerce.Ticketing.Venues.Services;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;
using Raven.Client.Exceptions;

namespace MintPlayer.Spark.Commerce.Ticketing.Tickets.Services;

/// <summary>
/// The ticket lifecycle that follows an order (§4.11, §4.4): issuance on <c>Confirmed</c> (one ticket per unit of each
/// ticketing line, exactly once per line), revocation on <c>TicketsRevoked</c> (exactly once per revocation id, I4),
/// the dispute freeze (GB1), the reissue of a changed line (GB7 seat change or upgrade). Every method is idempotent:
/// Checkout may deliver an order event twice.
/// </summary>
internal sealed class TicketIssuer(
    IDocumentStore store,
    TicketSecretVault vault,
    ISparkVenues venues,
    ISparkInventory inventory,
    IOptionsMonitor<SparkTicketingTicketsOptions> options,
    TimeProvider clock,
    ILogger<TicketIssuer> logger)
{
    private DateTimeOffset Now => clock.GetUtcNow();

    // ---- issuance ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Issues the tickets of every active ticketing line of <paramref name="order"/> not issued yet. A line is issued in
    /// one session together with its <see cref="TicketIssuance"/> marker, stored "must not exist": a second delivery of
    /// the event finds the marker and issues nothing.
    /// </summary>
    /// <returns>The codes issued now.</returns>
    public async Task<IReadOnlyList<string>> IssueAsync(CheckoutOrder order, CancellationToken cancellationToken)
    {
        var issued = new List<string>();
        foreach (var line in order.Lines.Where(l => l.Status == OrderLineStatus.Active && TicketItemIds.Handles(l.ItemId)))
            issued.AddRange(await IssueLineAsync(order, line, cancellationToken));
        return issued;
    }

    private async Task<IReadOnlyList<string>> IssueLineAsync(CheckoutOrder order, CheckoutOrderLine line, CancellationToken cancellationToken)
    {
        using var session = store.OpenAsyncSession();
        var markerId = TicketIssuance.IdFor(order.Id!, line.LineId);
        if (await session.Advanced.ExistsAsync(markerId, cancellationToken))
            return [];
        var context = await TicketCatalog.LoadAsync(session, line.ItemId, cancellationToken);
        if (context is null)
        {
            logger.LogWarning("Order {OrderId} line {LineId}: the ticketing item {ItemId} no longer resolves; no tickets issued.", order.Id, line.LineId, line.ItemId);
            return [];
        }

        var now = Now;
        var o = options.CurrentValue;
        var snapshot = await SnapshotAsync(context.Dates, cancellationToken);
        var availableFrom = TicketTimes.AvailableFrom(context.Settings, context.Dates, o, now);
        var freezeAt = TicketTimes.FreezeAt(context.Settings, context.Dates, o);
        var eraseAfter = TicketTimes.HolderEraseAfter(context.Dates, o);
        var named = context.Settings?.NamedTickets == true;
        var onHold = order.State == OrderState.Disputed;
        var codes = new List<string>();
        for (var position = 1; position <= line.Quantity; position++)
        {
            var code = TicketSecrets.NewCode();
            var secret = TicketSecrets.NewSecret();
            var verifier = TicketSecrets.Hash(secret, o.Secrets.Iterations);
            var unit = position <= line.Units.Count ? line.Units[position - 1] : null;
            var ticket = new Ticket
            {
                Id = Ticket.IdFor(code),
                Code = code,
                OrganizationId = context.Event.OrganizationId,
                EventId = context.Event.Id!,
                DateIds = [.. context.DateIds],
                TicketTypeId = context.Type.Id!,
                Kind = context.Type.Kind,
                Title = context.Type.Name,
                ItemId = line.ItemId,
                OrderId = order.Id!,
                OrderNumber = order.Number,
                LineId = line.LineId,
                Position = position,
                SourceOrderId = order.Id!,
                SourceLineId = line.LineId,
                SourcePosition = position,
                SourceClaimId = line.ClaimId,
                HolderClaimId = line.ClaimId,
                Origin = order.Origin,
                GuestList = order.GuestList,
                Unit = unit,
                SeatLabel = SeatLabels.For(snapshot, unit),
                Status = onHold ? ETicketStatus.OnHold : ETicketStatus.Valid,
                StatusReason = onHold ? "dispute" : null,
                MinimumAge = line.IdCheckRequired ? line.MinimumAge : null,
                HolderName = named ? null : order.BuyerName,
                HolderEmail = order.BuyerEmail,
                HolderUserId = order.BuyerUserId,
                HolderVisitorId = order.BuyerUserId is null ? order.VisitorId : null,
                HolderEraseAfter = eraseAfter,
                SecretSalt = verifier.Salt,
                SecretHash = verifier.Hash,
                SecretIterations = verifier.Iterations,
                Generation = 1,
                AvailableFrom = availableFrom,
                DeliverAt = availableFrom ?? now,
                DeliveryDue = true,
                FreezeAt = freezeAt,
                IssuedAt = now,
                UpdatedAt = now,
            };
            if (onHold && order.Disputes.LastOrDefault(d => d.ClosedAt is null) is { } dispute)
                ticket.HoldDisputeIds.Add(dispute.DisputeId);
            await session.StoreAsync(ticket, ticket.Id, cancellationToken);
            await session.StoreAsync(vault.Protect(code, secret, 1), TicketSecret.IdFor(code), cancellationToken);
            codes.Add(code);
        }

        // "Must not exist": the empty change vector makes a concurrent second issuance of the line fail as a whole.
        await session.StoreAsync(new TicketIssuance { Id = markerId, OrderId = order.Id!, LineId = line.LineId, Codes = codes, IssuedAt = now }, string.Empty, markerId, cancellationToken);
        try
        {
            await session.SaveChangesAsync(cancellationToken);
        }
        catch (ConcurrencyException)
        {
            return []; // issued by the other delivery
        }
        return codes;
    }

    private async Task<VenueLayoutSnapshot?> SnapshotAsync(IReadOnlyList<TicketEventDate> dates, CancellationToken cancellationToken)
        => dates.FirstOrDefault(d => d.LayoutSnapshotId is not null)?.LayoutSnapshotId is { } snapshotId
            ? await venues.GetSnapshotAsync(snapshotId, cancellationToken)
            : null;

    // ---- revocation (§4.7 "every refund: … ticket revocation", I4) ---------------------------------------------

    /// <summary>
    /// Revokes the tickets of the units Checkout gave back, exactly once per <see cref="OrderTicketRevocation.RevocationId"/>.
    /// Named seats revoke their own tickets; unnumbered units revoke the line's highest positions first. A ticket that was
    /// bought on the resale exchange holds its unit under the original claim, so revoking it also returns that unit
    /// (Checkout returned only the resale listing's quota).
    /// </summary>
    public async Task<int> RevokeAsync(CheckoutOrder order, OrderTicketRevocation revocation, CancellationToken cancellationToken)
    {
        var returns = new List<(Ticket Ticket, string ReturnId)>();
        var count = await TicketSessions.RetryAsync(store, async session =>
        {
            returns.Clear();
            var revoked = 0;
            var orderId = order.Id!;
            var tickets = await session.Query<Ticket>().Customize(c => c.WaitForNonStaleResults())
                .Where(t => t.OrderId == orderId).ToListAsync(cancellationToken);
            foreach (var line in revocation.Lines)
            {
                var candidates = tickets.Where(t => t.LineId == line.LineId && !t.RevocationIds.Contains(revocation.RevocationId)).ToList();
                if (tickets.Any(t => t.LineId == line.LineId && t.RevocationIds.Contains(revocation.RevocationId)))
                    continue; // this revocation was applied to the line before
                var chosen = line.Units.Count > 0
                    ? candidates.Where(t => t.Unit is not null && line.Units.Contains(t.Unit)).ToList()
                    : candidates.Where(t => t.Status != ETicketStatus.Revoked).OrderByDescending(t => t.Position).Take(line.Quantity).ToList();
                foreach (var ticket in chosen)
                {
                    Revoke(ticket, revocation.RevocationId, "refund");
                    revoked++;
                    if (ticket.SourceOrderId != ticket.OrderId && ticket.SourceClaimId is not null)
                        returns.Add((ticket, "ticket-revoke:" + revocation.RevocationId + ":" + ticket.Code));
                }
            }
            return revoked;
        }, cancellationToken);

        foreach (var (ticket, returnId) in returns)
            await ReturnSourceUnitAsync(ticket, returnId, ReturnReason.Refund, cancellationToken);
        return count;
    }

    /// <summary>Marks a ticket revoked by <paramref name="revocationId"/> and closes its open exchange offers.</summary>
    internal void Revoke(Ticket ticket, string revocationId, string reason)
    {
        ticket.Status = ETicketStatus.Revoked;
        ticket.StatusReason = reason;
        if (!ticket.RevocationIds.Contains(revocationId))
            ticket.RevocationIds.Add(revocationId);
        ticket.DeliveryDue = false;
        ticket.ResaleListingId = null;
        ticket.TransferId = null;
        ticket.UpdatedAt = Now;
    }

    /// <summary>Returns a resold ticket's unit from the original claim, exactly once per <paramref name="returnId"/>.</summary>
    private async Task ReturnSourceUnitAsync(Ticket ticket, string returnId, ReturnReason reason, CancellationToken cancellationToken)
    {
        var result = await inventory.ReturnAsync(ticket.SourceClaimId!, new InventoryReturn(
            returnId, reason,
            Items: [new ItemLine(ticket.ItemId, 1)],
            Units: ticket.Unit is { } unit ? [unit] : [],
            Limits: []), cancellationToken);
        if (!result.Succeeded)
            logger.LogWarning("The unit of resold ticket {Code} did not return to the original claim {ClaimId}: {Status} {Subject}.", ticket.Code, ticket.SourceClaimId, result.Status, result.Subject);
    }

    // ---- disputes (GB1) ----------------------------------------------------------------------------------------

    /// <summary>
    /// A dispute opened: the order's unused tickets are frozen ("on hold, see the box office"). When the seller chose to
    /// revoke and the event starts before the provider is expected to decide, they are revoked instead (reversibly:
    /// a won dispute restores them).
    /// </summary>
    public async Task FreezeAsync(CheckoutOrder order, string disputeId, CancellationToken cancellationToken)
    {
        await TicketSessions.RetryAsync(store, async session =>
        {
            var orderId = order.Id!;
            var tickets = await session.Query<Ticket>().Customize(c => c.WaitForNonStaleResults())
                .Where(t => t.OrderId == orderId).ToListAsync(cancellationToken);
            var settingsByEvent = new Dictionary<string, TicketEventSettings?>(StringComparer.Ordinal);
            foreach (var ticket in tickets.Where(t => t.Status != ETicketStatus.Revoked && !t.HoldDisputeIds.Contains(disputeId)))
            {
                if (ticket.Presence.Any(p => p.Entries > 0))
                    continue; // used: nothing to freeze
                if (!settingsByEvent.TryGetValue(ticket.EventId, out var settings))
                    settingsByEvent[ticket.EventId] = settings = await TicketCatalog.SettingsAsync(session, ticket.EventId, cancellationToken);
                ticket.HoldDisputeIds.Add(disputeId);
                var dates = await TicketCatalog.DatesAsync(session, ticket.DateIds, cancellationToken);
                var startsBeforeDecision = dates.Count > 0 && dates.Min(d => d.StartsAt) < Now + options.CurrentValue.Disputes.DecisionHorizon;
                ticket.Status = settings?.RevokeOnDisputeBeforeDecision == true && startsBeforeDecision ? ETicketStatus.Revoked : ETicketStatus.OnHold;
                ticket.StatusReason = "dispute";
                ticket.ResaleListingId = null;
                ticket.TransferId = null;
                ticket.UpdatedAt = Now;
            }
        }, cancellationToken);
    }

    /// <summary>A dispute was won: tickets it froze (or revoked) are valid again unless another hold or revocation remains.</summary>
    public async Task UnfreezeAsync(CheckoutOrder order, string disputeId, CancellationToken cancellationToken)
    {
        await TicketSessions.RetryAsync(store, async session =>
        {
            var orderId = order.Id!;
            var tickets = await session.Query<Ticket>().Customize(c => c.WaitForNonStaleResults())
                .Where(t => t.OrderId == orderId).ToListAsync(cancellationToken);
            foreach (var ticket in tickets.Where(t => t.HoldDisputeIds.Contains(disputeId)))
            {
                ticket.HoldDisputeIds.Remove(disputeId);
                if (ticket.HoldDisputeIds.Count == 0 && ticket.RevocationIds.Count == 0)
                {
                    ticket.Status = ETicketStatus.Valid;
                    ticket.StatusReason = null;
                }
                ticket.UpdatedAt = Now;
            }
        }, cancellationToken);
    }

    /// <summary>A dispute was lost: the tickets of the lines Checkout returned are revoked, keyed by the dispute id (I4).</summary>
    public async Task RevokeLostAsync(CheckoutOrder order, string disputeId, CancellationToken cancellationToken)
    {
        var returned = order.Lines.Where(l => l.Status == OrderLineStatus.Returned).Select(l => l.LineId).ToHashSet(StringComparer.Ordinal);
        await TicketSessions.RetryAsync(store, async session =>
        {
            var orderId = order.Id!;
            var tickets = await session.Query<Ticket>().Customize(c => c.WaitForNonStaleResults())
                .Where(t => t.OrderId == orderId).ToListAsync(cancellationToken);
            foreach (var ticket in tickets.Where(t => returned.Contains(t.LineId) && !t.RevocationIds.Contains("dispute:" + disputeId)))
                Revoke(ticket, "dispute:" + disputeId, "dispute lost");
        }, cancellationToken);
    }

    // ---- back office (GB7) -------------------------------------------------------------------------------------

    /// <summary>
    /// A seat change or upgrade (<see cref="Checkout.OrderEventKind.LineChanged"/>, reference = change id): the line's
    /// tickets take the new item and seats and are reissued with new secrets (the old QR codes stop working), then
    /// delivered again. Exactly once per change id.
    /// </summary>
    public async Task<IReadOnlyList<string>> ApplyLineChangeAsync(CheckoutOrder order, string changeId, CancellationToken cancellationToken)
    {
        var line = order.Lines.FirstOrDefault(l => l.ClaimId is { } claim && claim.EndsWith("/" + changeId, StringComparison.Ordinal));
        if (line is null || !TicketItemIds.Handles(line.ItemId))
            return [];
        return await TicketSessions.RetryAsync(store, async session =>
        {
            var marker = await session.LoadAsync<TicketIssuance>(TicketIssuance.IdFor(order.Id!, line.LineId), cancellationToken);
            if (marker is null || marker.AppliedChanges.Contains(changeId))
                return (IReadOnlyList<string>)[];
            var context = await TicketCatalog.LoadAsync(session, line.ItemId, cancellationToken);
            if (context is null)
                return [];
            var snapshot = await SnapshotAsync(context.Dates, cancellationToken);
            var orderId = order.Id!;
            var lineId = line.LineId;
            var tickets = (await session.Query<Ticket>().Customize(c => c.WaitForNonStaleResults())
                .Where(t => t.SourceOrderId == orderId && t.SourceLineId == lineId).ToListAsync(cancellationToken))
                .Where(t => t.Status != ETicketStatus.Revoked).OrderBy(t => t.SourcePosition).ToList();
            var now = Now;
            var o = options.CurrentValue;
            var changed = new List<string>();
            for (var i = 0; i < tickets.Count; i++)
            {
                var ticket = tickets[i];
                ticket.ItemId = line.ItemId;
                ticket.TicketTypeId = context.Type.Id!;
                ticket.Kind = context.Type.Kind;
                ticket.Title = context.Type.Name;
                ticket.DateIds = [.. context.DateIds];
                ticket.SourceClaimId = line.ClaimId;
                if (ticket.OrderId == ticket.SourceOrderId)
                    ticket.HolderClaimId = line.ClaimId;
                ticket.Unit = i < line.Units.Count ? line.Units[i] : null;
                ticket.SeatLabel = SeatLabels.For(snapshot, ticket.Unit);
                ticket.MinimumAge = line.IdCheckRequired ? line.MinimumAge : null;
                ticket.AvailableFrom = TicketTimes.AvailableFrom(context.Settings, context.Dates, o, now);
                ticket.FreezeAt = TicketTimes.FreezeAt(context.Settings, context.Dates, o);
                ticket.HolderEraseAfter = TicketTimes.HolderEraseAfter(context.Dates, o);
                await TicketSessions.NewGenerationAsync(session, ticket, vault, o.Secrets.Iterations, now, cancellationToken);
                changed.Add(ticket.Code);
            }
            marker.AppliedChanges.Add(changeId);
            return changed;
        }, cancellationToken);
    }
}
