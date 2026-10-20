using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Commerce.Ticketing.Events.Models;
using MintPlayer.Spark.Commerce.Ticketing.Tickets.CheckIn;
using MintPlayer.Spark.Commerce.Ticketing.Tickets.Contracts;
using MintPlayer.Spark.Commerce.Ticketing.Tickets.Exchange;
using MintPlayer.Spark.Commerce.Ticketing.Tickets.Models;
using MintPlayer.Spark.Commerce.Ticketing.Tickets.Pdf;
using MintPlayer.Spark.Commerce.Ticketing.Tickets.Scanner;
using MintPlayer.Spark.Organizations;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Commerce.Ticketing.Tickets.Services;

/// <summary>
/// The tickets package's API (PRD §4.11), behind the <c>/spark/ticketing/tickets</c> endpoints. Holder operations
/// answer null when the caller does not hold the ticket (the endpoints turn that into the missing-row refusal, #453);
/// rule refusals throw <see cref="TicketException"/>.
/// </summary>
public interface ISparkTickets
{
    /// <summary>The tickets the signed-in account (or the guest visitor) holds, most recent first.</summary>
    Task<SparkTicketList> GetMineAsync(CancellationToken cancellationToken = default);

    /// <summary>One ticket for its holder: proven by the secret (the link's fragment) or by the account that holds it.</summary>
    Task<SparkTicketView?> GetAsync(SparkTicketCredential credential, CancellationToken cancellationToken = default);

    /// <summary>D30: the PDF of one ticket for its holder (GC7 template); null when not held or not available yet.</summary>
    Task<byte[]?> GetPdfAsync(SparkTicketCredential credential, CancellationToken cancellationToken = default);

    /// <summary>D30: names the holder of a ticket (named tickets), until the freeze.</summary>
    Task<SparkTicketView?> SetHolderAsync(SparkTicketHolderRequest request, CancellationToken cancellationToken = default);

    Task<SparkTicketTransferInfo?> StartTransferAsync(SparkTicketTransferRequest request, CancellationToken cancellationToken = default);

    Task<bool> CancelTransferAsync(SparkTicketCredential request, CancellationToken cancellationToken = default);

    Task<SparkTicketTransferOffer?> GetTransferOfferAsync(string transferId, string? token, CancellationToken cancellationToken = default);

    Task<SparkTicketView?> AcceptTransferAsync(SparkTicketTransferAccept request, CancellationToken cancellationToken = default);

    Task<SparkTicketResaleInfo?> ListForResaleAsync(SparkTicketResaleRequest request, CancellationToken cancellationToken = default);

    Task<bool> WithdrawResaleAsync(SparkTicketCredential request, CancellationToken cancellationToken = default);

    Task<SparkResaleOffers?> GetResaleOffersAsync(string eventId, CancellationToken cancellationToken = default);

    /// <summary>GB7: re-sends an order's tickets, or one ticket, now. The seller's members only.</summary>
    Task<SparkTicketResendResult?> ResendAsync(SparkTicketResendRequest request, CancellationToken cancellationToken = default);

    /// <summary>GC7: stores the event's ticket logo or background (JPEG). The seller's members only.</summary>
    Task<bool> SetTemplateImageAsync(string eventId, string kind, Stream content, long length, CancellationToken cancellationToken = default);

    /// <summary>GC7: an event's ticket logo or background, or null.</summary>
    Task<byte[]?> GetTemplateImageAsync(string eventId, string kind, CancellationToken cancellationToken = default);

    Task<SparkScannerDeviceRegistered?> RegisterDeviceAsync(SparkScannerDeviceRequest request, CancellationToken cancellationToken = default);

    Task<bool> RevokeDeviceAsync(string deviceId, CancellationToken cancellationToken = default);
}

internal sealed class SparkTickets(
    IDocumentStore store,
    TicketAccess access,
    TicketPresenter presenter,
    TicketExchange exchange,
    TicketDelivery delivery,
    ScannerDevices devices,
    ITicketPdfRenderer pdf,
    ISparkOrganizations organizations,
    TicketChangeFeed feed,
    IOptionsMonitor<SparkTicketingTicketsOptions> options,
    TimeProvider clock) : ISparkTickets
{
    private DateTimeOffset Now => clock.GetUtcNow();

    public async Task<SparkTicketList> GetMineAsync(CancellationToken cancellationToken = default)
    {
        using var session = store.OpenAsyncSession();
        List<Ticket> tickets;
        if (access.UserId is { } me)
            tickets = await session.Query<Ticket>().Where(t => t.HolderUserId == me).OrderByDescending(t => t.IssuedAt).Take(200).ToListAsync(cancellationToken);
        else if (access.VisitorId is { Length: > 0 } visitor)
            tickets = await session.Query<Ticket>().Where(t => t.HolderUserId == null && t.HolderVisitorId == visitor).OrderByDescending(t => t.IssuedAt).Take(200).ToListAsync(cancellationToken);
        else
            tickets = [];
        var views = new List<SparkTicketView>(tickets.Count);
        foreach (var ticket in tickets)
            views.Add(await presenter.ViewAsync(session, ticket, null, cancellationToken));
        return new SparkTicketList(views);
    }

    public async Task<SparkTicketView?> GetAsync(SparkTicketCredential credential, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credential);
        using var session = store.OpenAsyncSession();
        if (await access.HolderAsync(session, credential.Code, credential.Secret, cancellationToken) is not { } held)
            return null;
        return await presenter.ViewAsync(session, held.Ticket, held.Secret, cancellationToken);
    }

    public async Task<byte[]?> GetPdfAsync(SparkTicketCredential credential, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credential);
        using var session = store.OpenAsyncSession();
        if (await access.HolderAsync(session, credential.Code, credential.Secret, cancellationToken) is not { } held)
            return null;
        var known = held.Secret is { } secret ? new Dictionary<string, string> { [held.Ticket.Code] = secret } : null;
        var (pages, template) = await presenter.PrintAsync(session, [held.Ticket], held.Ticket.HolderUserId ?? access.UserId, known, cancellationToken);
        return pages.Count == 0 ? null : pdf.Render(pages, template);
    }

    public async Task<SparkTicketView?> SetHolderAsync(SparkTicketHolderRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var name = request.Name?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > 120)
            throw TicketException.Of(TicketRefusals.NameRequired);
        string? eventId = null;
        var done = await TicketSessions.RetryAsync(store, async session =>
        {
            if (await access.HolderAsync(session, request.Code, request.Secret, cancellationToken) is not { } held)
                return false;
            var ticket = held.Ticket;
            if (ticket.Status == ETicketStatus.Revoked)
                throw TicketException.Of(TicketRefusals.Revoked);
            if (ticket.FreezeAt is { } freeze && Now >= freeze)
                throw TicketException.Of(TicketRefusals.Frozen);
            ticket.HolderName = name;
            ticket.UpdatedAt = Now;
            eventId = ticket.EventId;
            return true;
        }, cancellationToken);
        if (!done)
            return null;
        feed.Changed(eventId);
        return await GetAsync(new SparkTicketCredential(request.Code, request.Secret), cancellationToken);
    }

    public Task<SparkTicketTransferInfo?> StartTransferAsync(SparkTicketTransferRequest request, CancellationToken cancellationToken = default)
        => exchange.StartTransferAsync(request, cancellationToken);

    public Task<bool> CancelTransferAsync(SparkTicketCredential request, CancellationToken cancellationToken = default)
        => exchange.CancelTransferAsync(request, cancellationToken);

    public Task<SparkTicketTransferOffer?> GetTransferOfferAsync(string transferId, string? token, CancellationToken cancellationToken = default)
        => exchange.GetOfferAsync(transferId, token, presenter, cancellationToken);

    public async Task<SparkTicketView?> AcceptTransferAsync(SparkTicketTransferAccept request, CancellationToken cancellationToken = default)
    {
        var view = await exchange.AcceptAsync(request, presenter, cancellationToken);
        if (view is not null)
        {
            feed.Changed(view.EventId);
            // The new holder's ticket goes out at once (or at the lead time): the previous mail's QR code no longer works.
            await delivery.DeliverDueAsync(cancellationToken);
        }
        return view;
    }

    public Task<SparkTicketResaleInfo?> ListForResaleAsync(SparkTicketResaleRequest request, CancellationToken cancellationToken = default)
        => exchange.ListAsync(request, cancellationToken);

    public Task<bool> WithdrawResaleAsync(SparkTicketCredential request, CancellationToken cancellationToken = default)
        => exchange.WithdrawAsync(request, cancellationToken);

    public Task<SparkResaleOffers?> GetResaleOffersAsync(string eventId, CancellationToken cancellationToken = default)
        => exchange.OffersAsync(eventId, cancellationToken);

    public async Task<SparkTicketResendResult?> ResendAsync(SparkTicketResendRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        string? organizationId;
        using (var session = store.OpenAsyncSession())
        {
            if (request.Code is { Length: > 0 } code)
                organizationId = (await session.LoadAsync<Ticket>(Ticket.IdFor(code.ToUpperInvariant()), cancellationToken))?.OrganizationId;
            else if (request.OrderId is { Length: > 0 } orderId)
                organizationId = (await session.Query<Ticket>().Where(t => t.OrderId == orderId).FirstOrDefaultAsync(cancellationToken))?.OrganizationId;
            else
                return null;
        }
        if (!await IsMemberAsync(organizationId, cancellationToken))
            return null;
        var sent = await delivery.ResendAsync(request.OrderId, request.Code?.ToUpperInvariant(), cancellationToken);
        return new SparkTicketResendResult(sent);
    }

    public async Task<bool> SetTemplateImageAsync(string eventId, string kind, Stream content, long length, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        var name = AttachmentName(kind) ?? throw TicketException.Of(TicketRefusals.InvalidSettings, kind);
        var max = options.CurrentValue.Delivery.MaxTemplateImageBytes;
        if (length > max)
            throw TicketException.Of(TicketRefusals.InvalidSettings, "size");
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken);
        if (buffer.Length > max || !TicketPdfWriter.TryReadJpegSize(buffer.ToArray(), out _, out _, out _))
            throw TicketException.Of(TicketRefusals.InvalidSettings, "jpeg");

        using var session = store.OpenAsyncSession();
        var ev = await session.LoadAsync<TicketEvent>(eventId, cancellationToken);
        if (ev is null || !await IsMemberAsync(ev.OrganizationId, cancellationToken))
            return false;
        var settings = await TicketCatalog.SettingsAsync(session, eventId, cancellationToken);
        if (settings is null)
        {
            settings = new TicketEventSettings { EventId = eventId, OrganizationId = ev.OrganizationId };
            await session.StoreAsync(settings, cancellationToken);
        }
        if (name == TicketEventSettings.LogoAttachment)
            settings.HasLogo = true;
        else
            settings.HasBackground = true;
        settings.UpdatedAt = Now;
        buffer.Position = 0;
        session.Advanced.Attachments.Store(settings, name, buffer, "image/jpeg");
        await session.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<byte[]?> GetTemplateImageAsync(string eventId, string kind, CancellationToken cancellationToken = default)
    {
        if (AttachmentName(kind) is not { } name)
            return null;
        using var session = store.OpenAsyncSession();
        var settings = await TicketCatalog.SettingsAsync(session, eventId, cancellationToken);
        if (settings is null || (name == TicketEventSettings.LogoAttachment ? !settings.HasLogo : !settings.HasBackground))
            return null;
        return await TicketPresenter.ImageAsync(session, settings, name, cancellationToken);
    }

    public Task<SparkScannerDeviceRegistered?> RegisterDeviceAsync(SparkScannerDeviceRequest request, CancellationToken cancellationToken = default)
        => devices.RegisterAsync(request, cancellationToken);

    public Task<bool> RevokeDeviceAsync(string deviceId, CancellationToken cancellationToken = default)
        => devices.RevokeAsync(deviceId, cancellationToken);

    private static string? AttachmentName(string? kind) => kind switch
    {
        "logo" => TicketEventSettings.LogoAttachment,
        "background" => TicketEventSettings.BackgroundAttachment,
        _ => null,
    };

    /// <summary>Whether the caller is a member (Member and up) of the seller organization.</summary>
    private async Task<bool> IsMemberAsync(string? organizationId, CancellationToken cancellationToken)
    {
        if (access.UserId is not { } me || organizationId is null)
            return false;
        return await organizations.GetRoleAsync(organizationId, me, cancellationToken) is { } role && role >= OrganizationRole.Member;
    }
}

/// <summary>
/// The door devices' nudge (S9, X2 live topics): the public topic <c>ticketing-scanner:{eventId}</c> carries only the
/// instant the event's tickets last changed, never ticket data, so an online device syncs at once instead of at its
/// next interval. Throttled to one publication per event per second (latest wins anyway).
/// </summary>
internal sealed class TicketChangeFeed(IServiceProvider services, TimeProvider clock)
{
    public const string TopicPrefix = "ticketing-scanner:";

    private readonly Dictionary<string, DateTimeOffset> last = new(StringComparer.Ordinal);

    public static string Topic(string eventId) => TopicPrefix + eventId;

    public void Changed(string? eventId)
    {
        if (string.IsNullOrEmpty(eventId) || services.GetService(typeof(Abstractions.Live.ISparkLiveTopics)) is not Abstractions.Live.ISparkLiveTopics topics)
            return;
        var now = clock.GetUtcNow();
        lock (last)
        {
            if (last.TryGetValue(eventId, out var at) && now - at < TimeSpan.FromSeconds(1))
                return;
            last[eventId] = now;
            if (last.Count > 10_000)
                last.Clear();
        }
        topics.Publish(Topic(eventId), new SparkScannerNudge(eventId, now));
    }
}

/// <summary>The policy of the scanner topic: anyone may follow it; it carries no ticket data.</summary>
internal sealed class TicketScannerTopicPolicy : Abstractions.Live.ISparkLiveTopicPolicy
{
    public string Prefix => TicketChangeFeed.TopicPrefix;

    public bool CanSubscribe(string topic, Abstractions.Live.SparkLiveViewer viewer) => topic.Length > Prefix.Length;

    public bool CanSubscribePrivate(string topic, Abstractions.Live.SparkLiveViewer viewer) => false;
}
