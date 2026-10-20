using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Commerce.Ticketing.Tickets.Models;
using MintPlayer.Spark.Cron;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;
using Raven.Client.Exceptions;

namespace MintPlayer.Spark.Commerce.Ticketing.Tickets.Services;

/// <summary>
/// Delivery (D11): a ticket is mailed once its <see cref="Ticket.DeliverAt"/> has come (the seller's lead time) and again
/// after every reissue. Grouped per holder and order, so a buyer of four tickets gets one mail with one PDF.
/// </summary>
internal sealed class TicketDelivery(IDocumentStore store, TicketMails mails, TimeProvider clock, ILogger<TicketDelivery> logger)
{
    /// <summary>Mails every due ticket (in batches); returns how many tickets were delivered.</summary>
    public async Task<int> DeliverDueAsync(CancellationToken cancellationToken)
    {
        var delivered = 0;
        var now = clock.GetUtcNow();
        for (var round = 0; round < 50 && !cancellationToken.IsCancellationRequested; round++)
        {
            List<Ticket> due;
            using (var session = store.OpenAsyncSession())
            {
                due = await session.Query<Ticket>()
                    .Customize(c => c.WaitForNonStaleResults())
                    .Where(t => t.DeliveryDue == true && t.DeliverAt <= now)
                    .OrderBy(t => t.DeliverAt)
                    .Take(200)
                    .ToListAsync(cancellationToken);
            }
            if (due.Count == 0)
                break;
            foreach (var group in due.GroupBy(t => (t.HolderEmail ?? "", t.OrderId)))
                delivered += await DeliverAsync(group.ToList(), "delivery", cancellationToken);
            if (due.Count < 200)
                break;
        }
        return delivered;
    }

    /// <summary>
    /// Mails <paramref name="tickets"/> (one holder) and marks the generations mailed. A ticket reissued meanwhile stays
    /// due (its generation moved on). Tickets without an address are marked delivered: their holder opens the page.
    /// </summary>
    public async Task<int> DeliverAsync(IReadOnlyList<Ticket> tickets, string occasion, CancellationToken cancellationToken, IReadOnlyDictionary<string, string>? knownSecrets = null)
    {
        using var session = store.OpenAsyncSession();
        session.Advanced.UseOptimisticConcurrency = true;
        var fresh = (await session.LoadAsync<Ticket>(tickets.Select(t => t.Id!), cancellationToken)).Values
            .Where(t => t is not null && t.Status != ETicketStatus.Revoked).Select(t => t!).ToList();
        if (fresh.Count == 0)
            return 0;
        var sent = fresh.Any(t => t.HolderEmail is { Length: > 0 }) && await mails.SendTicketsAsync(session, fresh, occasion, knownSecrets, cancellationToken);
        var now = clock.GetUtcNow();
        foreach (var ticket in fresh)
        {
            ticket.DeliveryDue = false;
            ticket.DeliveredGeneration = ticket.Generation;
            if (sent)
                ticket.DeliveredAt = now;
        }
        try
        {
            await session.SaveChangesAsync(cancellationToken);
        }
        catch (ConcurrencyException)
        {
            logger.LogDebug("Tickets {Codes} changed while being delivered; the next run delivers the new generation.", string.Join(",", fresh.Select(t => t.Code)));
        }
        return sent ? fresh.Count : 0;
    }

    /// <summary>GB7: re-sends the tickets of an order (or one ticket) to their holders now, whatever was sent before.</summary>
    public async Task<int> ResendAsync(string? orderId, string? code, CancellationToken cancellationToken)
    {
        List<Ticket> tickets;
        using (var session = store.OpenAsyncSession())
        {
            if (code is { Length: > 0 })
                tickets = await session.LoadAsync<Ticket>(Ticket.IdFor(code), cancellationToken) is { } one ? [one] : [];
            else if (orderId is { Length: > 0 })
                tickets = await session.Query<Ticket>().Customize(c => c.WaitForNonStaleResults()).Where(t => t.OrderId == orderId).ToListAsync(cancellationToken);
            else
                tickets = [];
        }
        var now = clock.GetUtcNow();
        var sent = 0;
        foreach (var group in tickets.Where(t => t.Status != ETicketStatus.Revoked && (t.AvailableFrom is not { } from || from <= now)).GroupBy(t => t.HolderEmail ?? ""))
            sent += await DeliverAsync(group.ToList(), "resend-" + now.ToUnixTimeSeconds(), cancellationToken);
        return sent;
    }
}

/// <summary>Delivers due tickets (D11). Every minute by default (<c>Delivery:Schedule</c>).</summary>
internal sealed class TicketDeliveryJob(TicketDelivery delivery, ILogger<TicketDeliveryJob> logger) : ISparkCronJob
{
    public static string CronSchedule => "0 * * * * *";

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var count = await delivery.DeliverDueAsync(cancellationToken);
        if (count > 0)
            logger.LogInformation("Delivered {Count} tickets (D11).", count);
    }
}

/// <summary>
/// Erases holder names and addresses from tickets past their retention (the last date plus <c>HolderRetention</c>), and
/// purges expired, revoked devices' token hashes. The tickets stay (the gate log and the order refer to them).
/// </summary>
internal sealed class TicketHolderRetentionJob(IDocumentStore store, TimeProvider clock, ILogger<TicketHolderRetentionJob> logger) : ISparkCronJob
{
    public static string CronSchedule => "0 15 * * * *";

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var erased = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            using var session = store.OpenAsyncSession();
            var due = await session.Query<Ticket>()
                .Customize(c => c.WaitForNonStaleResults())
                .Where(t => t.HolderEraseAfter < now && (t.HolderName != null || t.HolderEmail != null || t.HolderUserId != null || t.HolderVisitorId != null))
                .Take(500)
                .ToListAsync(cancellationToken);
            if (due.Count == 0)
                break;
            foreach (var ticket in due)
                TicketHolders.Erase(ticket, now);
            await session.SaveChangesAsync(cancellationToken);
            erased += due.Count;
            if (due.Count < 500)
                break;
        }
        if (erased > 0)
            logger.LogInformation("Erased the holder data of {Count} tickets past their retention.", erased);
    }
}

/// <summary>The holder's personal data on a ticket.</summary>
internal static class TicketHolders
{
    /// <summary>Erases the holder's name, address and identities from the ticket (a transfer, resale, retention or account deletion).</summary>
    public static void Erase(Ticket ticket, DateTimeOffset now)
    {
        ticket.HolderName = null;
        ticket.HolderEmail = null;
        ticket.HolderUserId = null;
        ticket.HolderVisitorId = null;
        ticket.UpdatedAt = now;
    }
}
