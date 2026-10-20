using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Commerce.Checkout;
using MintPlayer.Spark.Commerce.Checkout.Models;
using MintPlayer.Spark.Commerce.Inventory;
using MintPlayer.Spark.Commerce.Ticketing.Events.Answers;
using MintPlayer.Spark.Commerce.Ticketing.Events.Models;
using MintPlayer.Spark.Commerce.Ticketing.Events.Sales;
using MintPlayer.Spark.Commerce.Ticketing.Tickets.Contracts;
using MintPlayer.Spark.Commerce.Ticketing.Tickets.Models;
using MintPlayer.Spark.Commerce.Ticketing.Tickets.Secrets;
using MintPlayer.Spark.Commerce.Ticketing.Tickets.Services;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Commerce.Ticketing.Tickets.Exchange;

/// <summary>The checkout items of the exchange: a resale listing and a transfer fee. Ids travel in bodies only.</summary>
public static class TicketExchangeItems
{
    public const string ResalePrefix = "ticket-resale:";

    public const string TransferFeePrefix = "ticket-transfer:";

    public static string Resale(string listingId) => ResalePrefix + Suffix(listingId, "TicketResaleListings/");

    public static string TransferFee(string transferId) => TransferFeePrefix + Suffix(transferId, "TicketTransfers/");

    public static bool Handles(string? itemId)
        => itemId is not null && (itemId.StartsWith(ResalePrefix, StringComparison.Ordinal) || itemId.StartsWith(TransferFeePrefix, StringComparison.Ordinal));

    /// <summary>The listing id of a resale item, or null.</summary>
    public static string? ListingIdOf(string? itemId)
        => itemId is not null && itemId.StartsWith(ResalePrefix, StringComparison.Ordinal) && itemId.Length > ResalePrefix.Length
            ? "TicketResaleListings/" + itemId[ResalePrefix.Length..] : null;

    /// <summary>The transfer id of a transfer-fee item, or null.</summary>
    public static string? TransferIdOf(string? itemId)
        => itemId is not null && itemId.StartsWith(TransferFeePrefix, StringComparison.Ordinal) && itemId.Length > TransferFeePrefix.Length
            ? "TicketTransfers/" + itemId[TransferFeePrefix.Length..] : null;

    public static string ListingQuota(string listingId) => "ticketing-resale/" + Suffix(listingId, "TicketResaleListings/");

    private static string Suffix(string id, string prefix) => id.StartsWith(prefix, StringComparison.Ordinal) ? id[prefix.Length..] : id;
}

/// <summary>
/// Pays the previous holder of a resold ticket (D30). The default (<see cref="PendingResaleSettlement"/>) records the
/// amount owed as <c>Pending</c> for the seller's <c>finance</c> members: refunding the original order through the
/// provider would bypass Checkout's refund ledger (credit note, the refunded amount, I4) and a later full refund of
/// that order would pay the same unit twice. A host with its own payout rail replaces this service.
/// </summary>
public interface ITicketResaleSettlement
{
    /// <param name="listing">The sold listing.</param>
    /// <param name="sourceOrderId">The order the previous holder's ticket belonged to.</param>
    /// <param name="sourceLineId">Its line.</param>
    /// <param name="resaleOrder">The buyer's order.</param>
    Task<TicketResaleSettlement> SettleAsync(TicketResaleListing listing, string sourceOrderId, string sourceLineId, CheckoutOrder resaleOrder, CancellationToken cancellationToken);
}

/// <summary>The default settlement: the asking price is owed to the previous holder, pending.</summary>
internal sealed class PendingResaleSettlement(TimeProvider clock) : ITicketResaleSettlement
{
    public Task<TicketResaleSettlement> SettleAsync(TicketResaleListing listing, string sourceOrderId, string sourceLineId, CheckoutOrder resaleOrder, CancellationToken cancellationToken)
        => Task.FromResult(new TicketResaleSettlement
        {
            Status = "Pending", Amount = listing.Price, OriginalOrderId = sourceOrderId, OriginalLineId = sourceLineId,
            Reference = resaleOrder.Number, At = clock.GetUtcNow(),
        });
}

/// <summary>
/// The exchange's checkout items (§4.1 "capacity sources"): a resale listing sells once over its one-unit quota, at its
/// asking price in the event's channel, under the event's per-buyer limit (D16); a transfer fee is a plain service item
/// of the giver while the transfer awaits it.
/// </summary>
internal sealed class TicketExchangeCapacitySource(IDocumentStore store, IOptionsMonitor<SparkCommerceOptions> commerce) : ICheckoutCapacitySource
{
    public bool Handles(string itemId) => TicketExchangeItems.Handles(itemId);

    public async Task<CheckoutItem?> ResolveAsync(CheckoutItemRequest request, CancellationToken cancellationToken)
    {
        if (request.Quantity != 1)
            return null;
        using var session = store.OpenAsyncSession();
        if (TicketExchangeItems.ListingIdOf(request.ItemId) is { } listingId)
        {
            var listing = await session.LoadAsync<TicketResaleListing>(listingId, cancellationToken);
            if (listing is not { Status: ETicketResaleStatus.Open })
                return null;
            var ticket = await session.LoadAsync<Ticket>(Ticket.IdFor(listing.TicketCode), cancellationToken);
            if (ticket is not { Status: ETicketStatus.Valid })
                return null;
            var context = await TicketCatalog.LoadAsync(session, ticket.ItemId, cancellationToken);
            if (context is null)
                return null;
            var channel = commerce.CurrentValue.Channels.TryGetValue(listing.Channel, out var c) ? c : null;
            if (channel is null || !context.Event.IsPublished)
                return null;
            var policy = SalePolicies.Effective(context.Event.Policy, channel);
            return new CheckoutItem
            {
                ItemId = request.ItemId,
                Channel = listing.Channel,
                OrganizationId = listing.OrganizationId,
                Price = listing.Price,
                VatRateBasisPoints = context.Type.VatRateBasisPoints,
                Title = Title(context.Type.Name, ticket.SeatLabel),
                Description = context.Event.Name,
                ServiceDate = context.Dates.Count > 0 ? context.Dates.Min(d => d.StartsAt) : null,
                MinimumAge = ticket.MinimumAge,
                LimitScope = TicketScopes.Limit(context.Event.Id!, context.Item.PhaseId, policy.LimitScope),
                PerBuyerLimit = policy.PerBuyerLimit,
                Capacity = new CheckoutCapacity { Quotas = [new QuotaLine(listing.QuotaId, 1)] },
            };
        }
        if (TicketExchangeItems.TransferIdOf(request.ItemId) is { } transferId)
        {
            var transfer = await session.LoadAsync<TicketTransfer>(transferId, cancellationToken);
            if (transfer is not { Status: ETicketTransferStatus.AwaitingFee, Fee: { } fee })
                return null;
            var ticket = await session.LoadAsync<Ticket>(Ticket.IdFor(transfer.TicketCode), cancellationToken);
            var ev = ticket is null ? null : await session.LoadAsync<TicketEvent>(ticket.EventId, cancellationToken);
            if (ticket is null || ev is null)
                return null;
            var type = await session.LoadAsync<TicketType>(ticket.TicketTypeId, cancellationToken);
            return new CheckoutItem
            {
                ItemId = request.ItemId,
                Channel = ev.Channel,
                OrganizationId = ev.OrganizationId,
                Price = fee,
                VatRateBasisPoints = type?.VatRateBasisPoints,
                Title = TranslatedString.Of(("en", "Ticket transfer fee"), ("nl", "Kosten ticketoverdracht"), ("fr", "Frais de transfert de billet"), ("de", "Gebühr für die Ticketübertragung")),
                Description = ev.Name,
            };
        }
        return null;
    }

    private static TranslatedString Title(TranslatedString typeName, string? seat)
    {
        var title = new TranslatedString();
        foreach (var language in new[] { "en", "nl", "fr", "de" })
        {
            var prefix = language switch { "nl" => "Doorverkoop", "fr" => "Revente", "de" => "Weiterverkauf", _ => "Resale" };
            title.Set(language, prefix + ": " + typeName.GetValue(language) + (seat is null ? "" : " (" + seat + ")"));
        }
        return title;
    }
}

/// <summary>
/// Transfer and the face-value resale (D30, §4.11), with the round-5 rule on personal data: the new holder's data
/// replaces the previous holder's, whose data is erased from the ticket and whose attendee answers are erased (GB3);
/// the ticket is reissued with a new secret, so the previous holder's link stops working; the audit records only that a
/// transfer happened, between which accounts or visitor ids. Both freeze at the event's freeze before doors.
/// </summary>
internal sealed class TicketExchange(
    IDocumentStore store,
    TicketAccess access,
    TicketSecretVault vault,
    TicketMails mails,
    ISparkInventory inventory,
    IAttendeeAnswers answers,
    IFinalPriceCalculator prices,
    ICommerceAudit audit,
    ITicketResaleSettlement settlement,
    IOptionsMonitor<SparkCommerceOptions> commerce,
    IOptionsMonitor<SparkTicketingTicketsOptions> options,
    TimeProvider clock,
    ILogger<TicketExchange> logger)
{
    public const string TransferredAction = "ticketing-tickets.transferred";
    public const string ResoldAction = "ticketing-tickets.resold";
    public const string ListedAction = "ticketing-tickets.listed";

    private DateTimeOffset Now => clock.GetUtcNow();

    // ---- shared checks ----------------------------------------------------------------------------------------

    private async Task<(TicketEvent Event, EffectiveSalePolicy Policy)> PolicyAsync(IAsyncDocumentSession session, Ticket ticket, CancellationToken cancellationToken)
    {
        var ev = await session.LoadAsync<TicketEvent>(ticket.EventId, cancellationToken) ?? throw TicketException.Of(TicketRefusals.NotFound);
        var channel = commerce.CurrentValue.Channels.TryGetValue(ev.Channel, out var c) ? c : new CommerceChannelOptions();
        return (ev, SalePolicies.Effective(ev.Policy, channel));
    }

    private void RequireOpen(Ticket ticket)
    {
        if (ticket.Status == ETicketStatus.Revoked)
            throw TicketException.Of(TicketRefusals.Revoked);
        if (ticket.Status == ETicketStatus.OnHold)
            throw TicketException.Of(TicketRefusals.OnHold);
        if (ticket.FreezeAt is { } freeze && Now >= freeze)
            throw TicketException.Of(TicketRefusals.Frozen);
    }

    /// <summary>What one ticket cost its first buyer, every mandatory fee included: the share of its source line's total.</summary>
    /// <summary>
    /// What this ticket cost (the resale cap, D30), in the currency the event's order was paid in: the line's charged
    /// amount (after its order-discount share, S21) split over its units by largest remainder. Null without the line.
    /// </summary>
    internal static async Task<Money?> PaidPerUnitAsync(IAsyncDocumentSession session, Ticket ticket, CancellationToken cancellationToken)
    {
        var order = await session.LoadAsync<CheckoutOrder>(ticket.SourceOrderId, cancellationToken);
        var line = order?.Lines.FirstOrDefault(l => l.LineId == ticket.SourceLineId);
        if (line is null || line.Quantity < 1)
            return null;
        var paid = line.Charged();
        if (paid.ToMinorUnits() <= 0)
            return Money.Zero(paid.Currency);
        var shares = paid.Allocate(Enumerable.Repeat(1L, line.Quantity).ToList());
        return shares[Math.Clamp(ticket.SourcePosition - 1, 0, shares.Length - 1)];
    }

    // ---- transfer ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Offers the ticket to <paramref name="request"/>'s recipient. Free transfers mail the offer now; with a seller fee
    /// (D30) the transfer waits for the giver to pay the fee item through checkout first.
    /// </summary>
    public async Task<SparkTicketTransferInfo?> StartTransferAsync(SparkTicketTransferRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var email = request.Email?.Trim();
        if (string.IsNullOrEmpty(email) || !email.Contains('@') || email.Length > 254)
            throw TicketException.Of(TicketRefusals.EmailRequired);
        string? token = null;
        TicketTransfer? created = null;
        Ticket? subject = null;
        var info = await TicketSessions.RetryAsync(store, async session =>
        {
            if (await access.HolderAsync(session, request.Code, request.Secret, cancellationToken) is not { } held)
                return (SparkTicketTransferInfo?)null;
            var ticket = held.Ticket;
            RequireOpen(ticket);
            var (_, policy) = await PolicyAsync(session, ticket, cancellationToken);
            if (!policy.Transfer)
                throw TicketException.Of(TicketRefusals.TransferNotAllowed);
            if (ticket.ResaleListingId is not null)
                throw TicketException.Of(TicketRefusals.AlreadyListed);
            if (ticket.TransferId is { } existingId && await session.LoadAsync<TicketTransfer>(existingId, cancellationToken) is { } existing)
            {
                if (existing.Status is ETicketTransferStatus.AwaitingFee or ETicketTransferStatus.Pending && existing.ExpiresAt > Now)
                    throw TicketException.Of(TicketRefusals.TransferPending);
                if (existing.Status is ETicketTransferStatus.AwaitingFee or ETicketTransferStatus.Pending)
                    existing.Status = ETicketTransferStatus.Expired;
            }

            var settings = await TicketCatalog.SettingsAsync(session, ticket.EventId, cancellationToken);
            Money? fee = null;
            if (settings?.TransferFeeBasisPoints is > 0 and var bp && await PaidPerUnitAsync(session, ticket, cancellationToken) is { } paid)
            {
                var share = paid.Share(Math.Min(bp, options.CurrentValue.Exchange.MaxTransferFeeBasisPoints), 10_000);
                fee = share.IsZero ? null : share;
            }
            var now = Now;
            var expires = now + options.CurrentValue.Exchange.TransferOfferLifetime;
            if (ticket.FreezeAt is { } freeze && freeze < expires)
                expires = freeze;
            token = fee is null ? TicketSecrets.NewToken() : null;
            created = new TicketTransfer
            {
                Id = "TicketTransfers/" + Guid.NewGuid().ToString("N"),
                TicketCode = ticket.Code,
                EventId = ticket.EventId,
                OrganizationId = ticket.OrganizationId,
                FromUserId = ticket.HolderUserId,
                FromVisitorId = ticket.HolderVisitorId,
                ToEmail = email,
                ToName = string.IsNullOrWhiteSpace(request.Name) ? null : request.Name.Trim(),
                TokenHash = token is null ? "" : TicketSecrets.TokenHash(token),
                Status = fee is null ? ETicketTransferStatus.Pending : ETicketTransferStatus.AwaitingFee,
                Fee = fee,
                CreatedAt = now,
                ExpiresAt = expires,
            };
            await session.StoreAsync(created, created.Id, cancellationToken);
            ticket.TransferId = created.Id;
            ticket.UpdatedAt = now;
            subject = ticket;
            return new SparkTicketTransferInfo(created.Id, created.Status.ToString(), created.ExpiresAt, fee, fee is null ? null : TicketExchangeItems.TransferFee(created.Id));
        }, cancellationToken);

        if (info is not null && token is not null && created is not null && subject is not null)
        {
            using var session = store.OpenAsyncSession();
            await mails.SendTransferOfferAsync(session, created, token, subject, cancellationToken);
        }
        return info;
    }

    /// <summary>The giver withdraws a pending offer (a paid fee is not returned: the seller's service was rendered).</summary>
    public async Task<bool> CancelTransferAsync(SparkTicketCredential request, CancellationToken cancellationToken)
        => await TicketSessions.RetryAsync(store, async session =>
        {
            if (await access.HolderAsync(session, request.Code, request.Secret, cancellationToken) is not { } held || held.Ticket.TransferId is not { } transferId)
                return false;
            if (await session.LoadAsync<TicketTransfer>(transferId, cancellationToken) is { Status: ETicketTransferStatus.AwaitingFee or ETicketTransferStatus.Pending } transfer)
            {
                transfer.Status = ETicketTransferStatus.Cancelled;
                transfer.ToEmail = null;
                transfer.ToName = null;
                transfer.CompletedAt = Now;
            }
            held.Ticket.TransferId = null;
            held.Ticket.UpdatedAt = Now;
            return true;
        }, cancellationToken);

    /// <summary>A transfer fee was paid (the fee item's order confirmed): the offer goes out. Idempotent.</summary>
    public async Task FeePaidAsync(CheckoutOrder order, CheckoutOrderLine line, CancellationToken cancellationToken)
    {
        if (TicketExchangeItems.TransferIdOf(line.ItemId) is not { } transferId)
            return;
        string? token = null;
        TicketTransfer? pending = null;
        Ticket? ticket = null;
        await TicketSessions.RetryAsync(store, async session =>
        {
            token = null;
            var transfer = await session.LoadAsync<TicketTransfer>(transferId, cancellationToken);
            if (transfer is not { Status: ETicketTransferStatus.AwaitingFee })
                return;
            token = TicketSecrets.NewToken();
            transfer.TokenHash = TicketSecrets.TokenHash(token);
            transfer.Status = ETicketTransferStatus.Pending;
            transfer.FeeOrderId = order.Id;
            pending = transfer;
            ticket = await session.LoadAsync<Ticket>(Ticket.IdFor(transfer.TicketCode), cancellationToken);
        }, cancellationToken);
        if (token is not null && pending is not null && ticket is not null)
        {
            using var session = store.OpenAsyncSession();
            await mails.SendTransferOfferAsync(session, pending, token, ticket, cancellationToken);
        }
    }

    /// <summary>What the recipient sees before accepting; null for an unknown, closed or expired offer or a wrong token.</summary>
    public async Task<SparkTicketTransferOffer?> GetOfferAsync(string transferId, string? token, TicketPresenter presenter, CancellationToken cancellationToken)
    {
        using var session = store.OpenAsyncSession();
        var id = transferId.StartsWith("TicketTransfers/", StringComparison.Ordinal) ? transferId : "TicketTransfers/" + transferId;
        var transfer = await session.LoadAsync<TicketTransfer>(id, cancellationToken);
        if (transfer is null || !TicketSecrets.TokenMatches(token, transfer.TokenHash))
            return null;
        var ticket = await session.LoadAsync<Ticket>(Ticket.IdFor(transfer.TicketCode), cancellationToken);
        var ev = ticket is null ? null : await session.LoadAsync<TicketEvent>(ticket.EventId, cancellationToken);
        if (ticket is null || ev is null)
            return null;
        var settings = await TicketCatalog.SettingsAsync(session, ticket.EventId, cancellationToken);
        var status = transfer.Status == ETicketTransferStatus.Pending && transfer.ExpiresAt <= Now ? ETicketTransferStatus.Expired : transfer.Status;
        return new SparkTicketTransferOffer(transfer.Id!, ev.Name, ticket.Title, await presenter.DatesAsync(session, ticket.DateIds, cancellationToken),
            ticket.SeatLabel, status.ToString(), transfer.ExpiresAt, settings?.NamedTickets == true);
    }

    /// <summary>
    /// The recipient accepts: the ticket is reissued to them (new secret, their name and address), the giver's data and
    /// attendee answers are erased, and the new generation is mailed. Returns the new holder's view.
    /// </summary>
    public async Task<SparkTicketView?> AcceptAsync(SparkTicketTransferAccept request, TicketPresenter presenter, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var id = request.TransferId.StartsWith("TicketTransfers/", StringComparison.Ordinal) ? request.TransferId : "TicketTransfers/" + request.TransferId;
        string? secret = null;
        (string OrderId, string LineId, int Position)? erase = null;
        Ticket? accepted = null;
        await TicketSessions.RetryAsync(store, async session =>
        {
            secret = null;
            var transfer = await session.LoadAsync<TicketTransfer>(id, cancellationToken);
            if (transfer is null || !TicketSecrets.TokenMatches(request.Token, transfer.TokenHash))
                return;
            if (transfer.Status != ETicketTransferStatus.Pending || transfer.ExpiresAt <= Now)
                throw TicketException.Of(TicketRefusals.TransferClosed);
            var ticket = await session.LoadAsync<Ticket>(Ticket.IdFor(transfer.TicketCode), cancellationToken) ?? throw TicketException.Of(TicketRefusals.NotFound);
            if (ticket.TransferId != transfer.Id)
                throw TicketException.Of(TicketRefusals.TransferClosed);
            RequireOpen(ticket);
            var settings = await TicketCatalog.SettingsAsync(session, ticket.EventId, cancellationToken);
            var name = string.IsNullOrWhiteSpace(request.Name) ? transfer.ToName : request.Name.Trim();
            if (settings?.NamedTickets == true && string.IsNullOrWhiteSpace(name))
                throw TicketException.Of(TicketRefusals.NameRequired);
            var email = string.IsNullOrWhiteSpace(request.Email) ? transfer.ToEmail : request.Email.Trim();

            var now = Now;
            erase = (ticket.OrderId, ticket.LineId, ticket.Position);
            await audit.RecordAsync(session, new CommerceAuditEntry
            {
                Action = TransferredAction, SubjectId = ticket.Id!, OrganizationId = ticket.OrganizationId, At = now,
                Data = new Dictionary<string, string>
                {
                    ["transferId"] = transfer.Id!,
                    ["from"] = transfer.FromUserId ?? "visitor:" + transfer.FromVisitorId,
                    ["to"] = access.UserId ?? "visitor:" + access.VisitorId,
                },
            }, cancellationToken);

            TicketHolders.Erase(ticket, now);
            ticket.HolderName = name;
            ticket.HolderEmail = email;
            ticket.HolderUserId = access.UserId;
            ticket.HolderVisitorId = access.UserId is null ? access.VisitorId : null;
            ticket.AcquiredThrough = "transfer:" + transfer.Id;
            ticket.TransferId = null;
            secret = await TicketSessions.NewGenerationAsync(session, ticket, vault, options.CurrentValue.Secrets.Iterations, now, cancellationToken);

            transfer.Status = ETicketTransferStatus.Accepted;
            transfer.ToUserId = access.UserId;
            transfer.ToVisitorId = access.UserId is null ? access.VisitorId : null;
            transfer.ToEmail = null;
            transfer.ToName = null;
            transfer.CompletedAt = now;
            accepted = ticket;
        }, cancellationToken);

        if (accepted is null || secret is null)
            return null;
        if (erase is { } position)
            await answers.EraseAsync(position.OrderId, position.LineId, position.Position, cancellationToken);
        using var view = store.OpenAsyncSession();
        var fresh = await view.LoadAsync<Ticket>(accepted.Id!, cancellationToken);
        return fresh is null ? null : await presenter.ViewAsync(view, fresh, secret, cancellationToken);
    }

    // ---- resale ----------------------------------------------------------------------------------------------

    /// <summary>
    /// Lists the ticket on the face-value exchange. The buyer's FinalPrice (the asking price plus the channel's mandatory
    /// fees, §4.4) may not exceed what the ticket cost (the cap, D30).
    /// </summary>
    public async Task<SparkTicketResaleInfo?> ListAsync(SparkTicketResaleRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        TicketResaleListing? listing = null;
        var info = await TicketSessions.RetryAsync(store, async session =>
        {
            if (await access.HolderAsync(session, request.Code, request.Secret, cancellationToken) is not { } held)
                return (SparkTicketResaleInfo?)null;
            var ticket = held.Ticket;
            RequireOpen(ticket);
            var (ev, policy) = await PolicyAsync(session, ticket, cancellationToken);
            if (!policy.FaceValueResale)
                throw TicketException.Of(TicketRefusals.ResaleNotAllowed);
            if (ticket.ResaleListingId is not null)
                throw TicketException.Of(TicketRefusals.AlreadyListed);
            if (ticket.TransferId is not null && await session.LoadAsync<TicketTransfer>(ticket.TransferId, cancellationToken) is { Status: ETicketTransferStatus.AwaitingFee or ETicketTransferStatus.Pending } open && open.ExpiresAt > Now)
                throw TicketException.Of(TicketRefusals.TransferPending);
            var cap = await PaidPerUnitAsync(session, ticket, cancellationToken) ?? throw TicketException.Of(TicketRefusals.ResaleNotAllowed);
            if (request.Price is null || request.Price.Currency != cap.Currency || request.Price.ToMinorUnits() <= 0)
                throw TicketException.Of(TicketRefusals.AboveFaceValue);
            var type = await session.LoadAsync<TicketType>(ticket.TicketTypeId, cancellationToken);
            var final = prices.Calculate(new FinalPriceRequest(ev.Channel, request.Price, PriceBasis.Gross, type?.VatRateBasisPoints)).UnitTotal;
            if (final > cap)
                throw TicketException.Of(TicketRefusals.AboveFaceValue, cap.ToString());

            var now = Now;
            var id = "TicketResaleListings/" + Guid.NewGuid().ToString("N");
            listing = new TicketResaleListing
            {
                Id = id,
                TicketCode = ticket.Code,
                EventId = ticket.EventId,
                OrganizationId = ticket.OrganizationId,
                Channel = ev.Channel,
                Price = request.Price,
                Cap = cap,
                SellerUserId = ticket.HolderUserId,
                SellerVisitorId = ticket.HolderVisitorId,
                Status = ETicketResaleStatus.Open,
                QuotaId = TicketExchangeItems.ListingQuota(id),
                CreatedAt = now,
            };
            await session.StoreAsync(listing, id, cancellationToken);
            ticket.ResaleListingId = id;
            ticket.TransferId = null;
            ticket.UpdatedAt = now;
            await audit.RecordAsync(session, new CommerceAuditEntry
            {
                Action = ListedAction, SubjectId = ticket.Id!, OrganizationId = ticket.OrganizationId, After = request.Price, At = now,
                Data = new Dictionary<string, string> { ["listingId"] = id, ["cap"] = cap.ToString() },
            }, cancellationToken);
            return new SparkTicketResaleInfo(id, listing.Status.ToString(), listing.Price, cap);
        }, cancellationToken);

        // The one-unit quota exists before any buyer can resolve the item (idempotent).
        if (listing is not null)
            await inventory.CreateQuotaAsync(listing.QuotaId, 1, 1, cancellationToken);
        return info;
    }

    /// <summary>Withdraws an open listing; refused while a buyer holds it (the quota cannot shrink below a live hold).</summary>
    public async Task<bool> WithdrawAsync(SparkTicketCredential request, CancellationToken cancellationToken)
    {
        string? quotaId;
        using (var session = store.OpenAsyncSession())
        {
            if (await access.HolderAsync(session, request.Code, request.Secret, cancellationToken) is not { } held || held.Ticket.ResaleListingId is not { } listingId)
                return false;
            var listing = await session.LoadAsync<TicketResaleListing>(listingId, cancellationToken);
            if (listing is not { Status: ETicketResaleStatus.Open })
                throw TicketException.Of(TicketRefusals.ListingClosed);
            quotaId = listing.QuotaId;
        }
        var closed = await inventory.SetCapacityAsync(quotaId, 0, cancellationToken);
        if (!closed.Succeeded && closed.Status != ClaimStatus.NotFound)
            throw TicketException.Of(TicketRefusals.ListingClosed, closed.Status.ToString());
        return await TicketSessions.RetryAsync(store, async session =>
        {
            if (await access.HolderAsync(session, request.Code, request.Secret, cancellationToken) is not { } held || held.Ticket.ResaleListingId is not { } listingId)
                return false;
            var listing = await session.LoadAsync<TicketResaleListing>(listingId, cancellationToken);
            if (listing is { Status: ETicketResaleStatus.Open })
            {
                listing.Status = ETicketResaleStatus.Withdrawn;
                listing.ClosedAt = Now;
            }
            held.Ticket.ResaleListingId = null;
            held.Ticket.UpdatedAt = Now;
            return true;
        }, cancellationToken);
    }

    /// <summary>The open listings of a published event, cheapest first.</summary>
    public async Task<SparkResaleOffers?> OffersAsync(string eventId, CancellationToken cancellationToken)
    {
        using var session = store.OpenAsyncSession();
        var ev = await session.LoadAsync<TicketEvent>(eventId, cancellationToken);
        if (ev is not { IsPublished: true })
            return null;
        var listings = await session.Query<TicketResaleListing>().Where(l => l.EventId == eventId && l.Status == ETicketResaleStatus.Open).Take(1024).ToListAsync(cancellationToken);
        var tickets = await session.LoadAsync<Ticket>(listings.Select(l => Ticket.IdFor(l.TicketCode)), cancellationToken);
        var offers = new List<SparkResaleOffer>();
        foreach (var listing in listings)
        {
            if (!tickets.TryGetValue(Ticket.IdFor(listing.TicketCode), out var ticket) || ticket is not { Status: ETicketStatus.Valid })
                continue;
            if ((await inventory.GetAvailabilityAsync(listing.QuotaId, cancellationToken)).Available < 1)
                continue;
            var type = await session.LoadAsync<TicketType>(ticket.TicketTypeId, cancellationToken);
            var final = prices.Calculate(new FinalPriceRequest(listing.Channel, listing.Price, PriceBasis.Gross, type?.VatRateBasisPoints)).UnitTotal;
            offers.Add(new SparkResaleOffer(listing.Id!, TicketExchangeItems.Resale(listing.Id!), ticket.TicketTypeId, ticket.Title, ticket.DateIds, ticket.SeatLabel, listing.Price, final));
        }
        return new SparkResaleOffers(eventId, offers.OrderBy(o => o.FinalPrice.ToMinorUnits()).ToList());
    }

    /// <summary>
    /// A resale purchase was confirmed: the ticket is reissued to the buyer (new secret, their data; the previous
    /// holder's data and answers erased), the previous holder's per-buyer count is returned exactly once keyed by the
    /// listing (I4, <see cref="ReturnReason.Resale"/>), the unit stays sold under its original claim, and the previous
    /// holder's payout is recorded. Idempotent.
    /// </summary>
    public async Task<string?> CompleteResaleAsync(CheckoutOrder order, CheckoutOrderLine line, CancellationToken cancellationToken)
    {
        if (TicketExchangeItems.ListingIdOf(line.ItemId) is not { } listingId)
            return null;
        (string OrderId, string LineId, int Position)? previous = null;
        string? previousClaim = null;
        IReadOnlyList<LimitLine> previousLimits = [];
        TicketResaleListing? sold = null;
        var code = await TicketSessions.RetryAsync(store, async session =>
        {
            previous = null;
            var listing = await session.LoadAsync<TicketResaleListing>(listingId, cancellationToken);
            if (listing is null || listing.Status == ETicketResaleStatus.Sold)
                return null;
            var ticket = await session.LoadAsync<Ticket>(Ticket.IdFor(listing.TicketCode), cancellationToken);
            if (ticket is null)
                return null;
            var now = Now;
            previous = (ticket.OrderId, ticket.LineId, ticket.Position);
            previousClaim = ticket.HolderClaimId;
            var holderOrder = await session.LoadAsync<CheckoutOrder>(ticket.OrderId, cancellationToken);
            previousLimits = holderOrder?.Lines.FirstOrDefault(l => l.LineId == ticket.LineId)?.Limits.Select(l => l with { N = 1 }).ToList() ?? [];

            await audit.RecordAsync(session, new CommerceAuditEntry
            {
                Action = ResoldAction, SubjectId = ticket.Id!, OrganizationId = ticket.OrganizationId, After = listing.Price, At = now,
                Data = new Dictionary<string, string>
                {
                    ["listingId"] = listing.Id!,
                    ["from"] = listing.SellerUserId ?? "visitor:" + listing.SellerVisitorId,
                    ["to"] = order.BuyerUserId ?? "visitor:" + order.VisitorId,
                    ["order"] = order.Number,
                },
            }, cancellationToken);

            listing.Settlement = await settlement.SettleAsync(listing, ticket.OrderId, ticket.LineId, order, cancellationToken);
            listing.Status = ETicketResaleStatus.Sold;
            listing.BuyerOrderId = order.Id;
            listing.ClosedAt = now;

            var settings = await TicketCatalog.SettingsAsync(session, ticket.EventId, cancellationToken);
            TicketHolders.Erase(ticket, now);
            ticket.HolderName = settings?.NamedTickets == true ? null : order.BuyerName;
            ticket.HolderEmail = order.BuyerEmail;
            ticket.HolderUserId = order.BuyerUserId;
            ticket.HolderVisitorId = order.BuyerUserId is null ? order.VisitorId : null;
            ticket.OrderId = order.Id!;
            ticket.OrderNumber = order.Number;
            ticket.LineId = line.LineId;
            ticket.Position = 1;
            ticket.HolderClaimId = line.ClaimId;
            ticket.AcquiredThrough = "resale:" + listing.Id;
            ticket.ResaleListingId = null;
            ticket.TransferId = null;
            if (order.State == OrderState.Disputed)
                ticket.Status = ETicketStatus.OnHold;
            await TicketSessions.NewGenerationAsync(session, ticket, vault, options.CurrentValue.Secrets.Iterations, now, cancellationToken);
            sold = listing;
            return ticket.Code;
        }, cancellationToken);

        if (code is null || previous is not { } position)
            return code;
        await answers.EraseAsync(position.OrderId, position.LineId, position.Position, cancellationToken);
        if (previousClaim is not null && previousLimits.Count > 0)
        {
            var returned = await inventory.ReturnAsync(previousClaim, new InventoryReturn("resale:" + listingId, ReturnReason.Resale, Items: [], Quotas: [], Units: [], Limits: previousLimits), cancellationToken);
            if (!returned.Succeeded)
                logger.LogWarning("Resale {ListingId}: the previous holder's per-buyer count did not return ({Status} {Subject}).", listingId, returned.Status, returned.Subject);
        }
        return code;
    }
}
