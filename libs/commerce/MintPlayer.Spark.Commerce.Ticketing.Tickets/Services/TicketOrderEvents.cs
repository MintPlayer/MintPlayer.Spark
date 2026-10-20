using MintPlayer.Spark.Commerce.Checkout;
using MintPlayer.Spark.Commerce.Checkout.Models;
using MintPlayer.Spark.Commerce.Ticketing.Tickets.Exchange;
using MintPlayer.Spark.Commerce.Ticketing.Tickets.Models;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Commerce.Ticketing.Tickets.Services;

/// <summary>
/// Tickets follow the order (§4.1 "order-event recipients", §4.4):
/// <list type="bullet">
/// <item><c>Confirmed</c>: tickets are issued (comps and guest lists too, GB7) and delivered when due (D11); a resale
/// purchase reissues the listed ticket; a paid transfer fee sends the transfer offer;</item>
/// <item><c>TicketsRevoked</c>: the returned units' tickets are revoked, once per revocation (I4);</item>
/// <item><c>Disputed</c> / <c>DisputeWon</c> / <c>DisputeLost</c>: freeze, unfreeze, revoke (GB1);</item>
/// <item><c>LineChanged</c>: a seat change or upgrade reissues the line's tickets (GB7);</item>
/// <item><c>ConfirmationResent</c>: the tickets are re-sent with the confirmation (GB7);</item>
/// <item><c>AttachedToAccount</c>: the tickets the guest visitor still holds move onto the account (D4, GB10).</item>
/// </list>
/// Every branch is idempotent: Checkout records a throwing recipient as undelivered and may deliver again.
/// </summary>
internal sealed class TicketOrderEvents(
    IDocumentStore store,
    TicketIssuer issuer,
    TicketExchange exchange,
    TicketDelivery delivery,
    TicketChangeFeed feed,
    TimeProvider clock) : IOrderEventRecipient
{
    public async Task OnOrderEventAsync(OrderEvent orderEvent, CancellationToken cancellationToken)
    {
        var order = orderEvent.Order;
        switch (orderEvent.Kind)
        {
            case OrderEventKind.Confirmed:
                await issuer.IssueAsync(order, cancellationToken);
                foreach (var line in order.Lines.Where(l => l.Status == OrderLineStatus.Active))
                {
                    if (TicketExchangeItems.ListingIdOf(line.ItemId) is not null)
                        await exchange.CompleteResaleAsync(order, line, cancellationToken);
                    else if (TicketExchangeItems.TransferIdOf(line.ItemId) is not null)
                        await exchange.FeePaidAsync(order, line, cancellationToken);
                }
                await DeliverOrderAsync(order.Id!, cancellationToken);
                break;

            case OrderEventKind.TicketsRevoked when orderEvent.Revocation is { } revocation:
                await issuer.RevokeAsync(order, revocation, cancellationToken);
                break;

            case OrderEventKind.Disputed when orderEvent.Reference is { } disputeId:
                await issuer.FreezeAsync(order, disputeId, cancellationToken);
                break;

            case OrderEventKind.DisputeWon when orderEvent.Reference is { } disputeId:
                await issuer.UnfreezeAsync(order, disputeId, cancellationToken);
                break;

            case OrderEventKind.DisputeLost when orderEvent.Reference is { } disputeId:
                await issuer.RevokeLostAsync(order, disputeId, cancellationToken);
                break;

            case OrderEventKind.LineChanged when orderEvent.Reference is { } changeId:
                if ((await issuer.ApplyLineChangeAsync(order, changeId, cancellationToken)).Count > 0)
                    await DeliverOrderAsync(order.Id!, cancellationToken);
                break;

            case OrderEventKind.ConfirmationResent:
                await delivery.ResendAsync(order.Id, null, cancellationToken);
                return;

            case OrderEventKind.AttachedToAccount when order.BuyerUserId is { } accountId:
                await AttachToAccountAsync(order.Id!, accountId, orderEvent.Reference, cancellationToken);
                return;

            default:
                return;
        }

        // The door devices of the events concerned sync at once (S9): a revocation, a freeze or a reissue must reach
        // them before the next scan of the ticket, not at their next interval.
        var orderId = order.Id!;
        using var session = store.OpenAsyncSession();
        var eventIds = await session.Query<Ticket>().Where(t => t.OrderId == orderId).Select(t => t.EventId).Take(256).ToListAsync(cancellationToken);
        foreach (var eventId in eventIds.Distinct(StringComparer.Ordinal))
            feed.Changed(eventId);
    }

    /// <summary>
    /// D4, GB10: a guest order moved onto an account. The order's tickets the guest visitor still holds follow it; a
    /// ticket transferred or resold since then belongs to its new holder and stays where it is.
    /// </summary>
    private async Task AttachToAccountAsync(string orderId, string accountId, string? guestVisitorId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(guestVisitorId))
            return;
        using var session = store.OpenAsyncSession();
        var tickets = await session.Query<Ticket>()
            .Where(t => t.OrderId == orderId && t.HolderUserId == null && t.HolderVisitorId == guestVisitorId)
            .Take(4096)
            .ToListAsync(cancellationToken);
        if (tickets.Count == 0)
            return;
        foreach (var ticket in tickets)
        {
            ticket.HolderUserId = accountId;
            ticket.HolderVisitorId = null;
        }
        await session.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Delivers the order's tickets that are due now (the rest wait for their lead time, D11).</summary>
    private async Task DeliverOrderAsync(string orderId, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        List<Ticket> due;
        using (var session = store.OpenAsyncSession())
        {
            due = await session.Query<Ticket>()
                .Customize(c => c.WaitForNonStaleResults())
                .Where(t => t.OrderId == orderId && t.DeliveryDue == true && t.DeliverAt <= now)
                .ToListAsync(cancellationToken);
        }
        foreach (var group in due.GroupBy(t => t.HolderEmail ?? ""))
            await delivery.DeliverAsync(group.ToList(), "delivery", cancellationToken);
    }
}
