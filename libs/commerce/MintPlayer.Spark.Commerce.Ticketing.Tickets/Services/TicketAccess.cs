using MintPlayer.Spark.Abstractions.Authentication;
using MintPlayer.Spark.Abstractions.Visitor;
using MintPlayer.Spark.Commerce.Ticketing.Tickets.Models;
using MintPlayer.Spark.Commerce.Ticketing.Tickets.Secrets;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Commerce.Ticketing.Tickets.Services;

/// <summary>
/// Who may act on a ticket as its holder: whoever proves the current secret (the ticket link's fragment or the QR
/// code), or the signed-in account (or the guest visitor, X1) the ticket is held by. A reissue changes the secret, so
/// the previous holder's link stops working at once (§4.11).
/// </summary>
internal sealed class TicketAccess(ISparkCurrentUser currentUser, IServiceProvider services)
{
    public string? UserId => currentUser.IsAuthenticated && currentUser.Id is { Length: > 0 } id ? id : null;

    public string? VisitorId => (services.GetService(typeof(ISparkVisitor)) as ISparkVisitor)?.VisitorId;

    /// <summary>The ticket when the caller holds it; null otherwise (the endpoints answer the missing-row refusal).</summary>
    public async Task<(Ticket Ticket, string? Secret)?> HolderAsync(IAsyncDocumentSession session, string? code, string? secret, CancellationToken cancellationToken)
    {
        if (!TicketSecrets.IsCode(code?.ToUpperInvariant()))
            return null;
        var ticket = await session.LoadAsync<Ticket>(Ticket.IdFor(code!.ToUpperInvariant()), cancellationToken);
        if (ticket is null)
            return null;
        if (!string.IsNullOrEmpty(secret))
            return TicketSecrets.Verify(ticket, secret) ? (ticket, secret) : null;
        return IsHolder(ticket) ? (ticket, null) : null;
    }

    /// <summary>Whether the signed-in account, or the guest visitor without an account, holds <paramref name="ticket"/>.</summary>
    public bool IsHolder(Ticket ticket)
    {
        if (UserId is { } me)
            return ticket.HolderUserId == me;
        return ticket.HolderUserId is null && VisitorId is { Length: > 0 } visitor && ticket.HolderVisitorId == visitor;
    }
}
