using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.Commerce.Ticketing.Tickets.Models;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Commerce.Ticketing.Tickets.Services;

/// <summary>
/// GDPR Art. 15/20 (§4.15): the tickets an account holds, in its personal-data export. Never the secret (it grants
/// entry) and never another holder's data: a transferred ticket shows only its current holder (§4.11 round 5).
/// </summary>
internal sealed class TicketHolderPersonalData<TUser>(IDocumentStore store) : ISparkPersonalDataContributor<TUser> where TUser : SparkUser
{
    public string Name => "ticketing-tickets";

    public async Task<object?> GetPersonalDataAsync(TUser user, CancellationToken cancellationToken)
    {
        var userId = user.Id!;
        using var session = store.OpenAsyncSession();
        var tickets = await session.Query<Ticket>().Where(t => t.HolderUserId == userId).Take(4096).ToListAsync(cancellationToken);
        return tickets.Count == 0 ? null : tickets.Select(t => new
        {
            t.Code,
            t.EventId,
            t.DateIds,
            t.TicketTypeId,
            t.SeatLabel,
            t.OrderNumber,
            t.HolderName,
            t.HolderEmail,
            Status = t.Status.ToString(),
            t.AcquiredThrough,
            t.IssuedAt,
            t.HolderEraseAfter,
            Scans = t.Presence.SelectMany(p => p.Events.Select(e => new { p.ListId, p.DateId, Direction = e.Direction.ToString(), e.At })).ToList(),
        }).ToList();
    }
}

/// <summary>
/// GDPR Art. 17: deleting an account erases it as the holder of its tickets. The tickets stay valid (the order is the
/// contract, §4.8): whoever has the ticket link or the PDF still gets in, and the gate log keeps the scans without a name.
/// </summary>
internal sealed class TicketHolderAccountDeletion<TUser>(IDocumentStore store, TimeProvider clock) : ISparkAccountDeletionHandler<TUser> where TUser : SparkUser
{
    public async Task OnDeletingAccountAsync(TUser user, CancellationToken cancellationToken)
    {
        var userId = user.Id!;
        while (true)
        {
            using var session = store.OpenAsyncSession();
            var tickets = await session.Query<Ticket>()
                .Customize(c => c.WaitForNonStaleResults())
                .Where(t => t.HolderUserId == userId)
                .Take(500)
                .ToListAsync(cancellationToken);
            if (tickets.Count == 0)
                return;
            var now = clock.GetUtcNow();
            foreach (var ticket in tickets)
                TicketHolders.Erase(ticket, now);
            await session.SaveChangesAsync(cancellationToken);
            if (tickets.Count < 500)
                return;
        }
    }
}
