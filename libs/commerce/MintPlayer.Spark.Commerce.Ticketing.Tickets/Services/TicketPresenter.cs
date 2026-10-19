using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.DateTimes;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.Commerce.Ticketing.Events.Models;
using MintPlayer.Spark.Commerce.Ticketing.Events.Sales;
using MintPlayer.Spark.Commerce.Ticketing.Tickets.Contracts;
using MintPlayer.Spark.Commerce.Ticketing.Tickets.Models;
using MintPlayer.Spark.Commerce.Ticketing.Tickets.Pdf;
using MintPlayer.Spark.Commerce.Ticketing.Tickets.Secrets;
using MintPlayer.Spark.Commerce.Ticketing.Venues.Models;
using MintPlayer.Spark.Commerce.Ticketing.Venues.Services;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Commerce.Ticketing.Tickets.Services;

/// <summary>
/// Turns tickets into what the holder sees (the page's <see cref="SparkTicketView"/>) and what is printed (the PDF's
/// <see cref="TicketPrintPage"/>). Server-rendered dates use the core formatter with the recipient's stored zone and
/// culture and the venue-local hint (X14, D33, K10): no zone logic here.
/// </summary>
internal sealed class TicketPresenter(
    IServiceProvider services,
    TicketSecretVault vault,
    ISparkVenues venues,
    IOptionsMonitor<SparkCommerceOptions> commerce,
    TimeProvider clock)
{
    public const string TemplateImagePath = "/spark/ticketing/tickets/template-image";

    private readonly SparkQrCodeRenderer qr = new();

    /// <summary>
    /// The holder's view of <paramref name="ticket"/>. <paramref name="secret"/> is the secret the caller proved (or the
    /// vault's copy for a holder signed in); without it, or before the lead time, no QR code is included.
    /// </summary>
    public async Task<SparkTicketView> ViewAsync(IAsyncDocumentSession session, Ticket ticket, string? secret, CancellationToken cancellationToken)
    {
        var ev = await session.LoadAsync<TicketEvent>(ticket.EventId, cancellationToken);
        var settings = await TicketCatalog.SettingsAsync(session, ticket.EventId, cancellationToken);
        var dates = await DatesAsync(session, ticket.DateIds, cancellationToken);
        var now = clock.GetUtcNow();
        var available = ticket.Status != ETicketStatus.Revoked && (ticket.AvailableFrom is not { } from || from <= now);
        secret ??= await RecoverSecretAsync(session, ticket, cancellationToken);
        var payload = available && secret is not null ? TicketSecrets.Payload(ticket.Code, secret) : null;

        var policy = ev is null ? null : SalePolicies.Effective(ev.Policy, commerce.CurrentValue.Channels.TryGetValue(ev.Channel, out var channel) ? channel : new CommerceChannelOptions());
        var open = ticket.Status == ETicketStatus.Valid && (ticket.FreezeAt is not { } freeze || now < freeze);
        SparkTicketTransferInfo? transfer = null;
        if (ticket.TransferId is { } transferId && await session.LoadAsync<TicketTransfer>(transferId, cancellationToken) is { Status: ETicketTransferStatus.AwaitingFee or ETicketTransferStatus.Pending } t)
            transfer = new SparkTicketTransferInfo(t.Id!, t.Status.ToString(), t.ExpiresAt, t.Fee, t.Status == ETicketTransferStatus.AwaitingFee ? Exchange.TicketExchangeItems.TransferFee(t.Id!) : null);
        SparkTicketResaleInfo? resale = null;
        if (ticket.ResaleListingId is { } listingId && await session.LoadAsync<TicketResaleListing>(listingId, cancellationToken) is { Status: ETicketResaleStatus.Open } l)
            resale = new SparkTicketResaleInfo(l.Id!, l.Status.ToString(), l.Price, l.Cap);

        return new SparkTicketView(
            ticket.Code,
            ticket.Generation,
            ticket.EventId,
            ev?.Name ?? new TranslatedString(),
            ev?.Slug,
            ticket.TicketTypeId,
            ticket.Title,
            ticket.Kind.ToString(),
            dates,
            ticket.SeatLabel,
            ticket.HolderName,
            settings?.NamedTickets == true,
            ticket.Status.ToString(),
            ticket.MinimumAge,
            ticket.OrderNumber,
            available,
            ticket.AvailableFrom,
            payload,
            payload is null ? null : qr.RenderSvg(payload),
            ticket.FreezeAt,
            open && policy?.Transfer == true && ticket.ResaleListingId is null,
            open && policy?.FaceValueResale == true && ticket.TransferId is null,
            transfer,
            resale,
            Template(settings));
    }

    /// <summary>The vault's copy of the current secret, for a holder who opened the ticket through their account.</summary>
    public async Task<string?> RecoverSecretAsync(IAsyncDocumentSession session, Ticket ticket, CancellationToken cancellationToken)
        => vault.Unprotect(await session.LoadAsync<TicketSecret>(TicketSecret.IdFor(ticket.Code), cancellationToken), ticket.Code, ticket.Generation);

    public static SparkTicketTemplate Template(TicketEventSettings? settings) => new(
        settings?.TemplateAccentColor,
        settings?.TemplateAccentTextColor,
        settings?.TemplateExtraText,
        settings?.HasLogo == true ? $"{TemplateImagePath}?eventId={Uri.EscapeDataString(settings.EventId)}&kind=logo" : null,
        settings?.HasBackground == true ? $"{TemplateImagePath}?eventId={Uri.EscapeDataString(settings.EventId)}&kind=background" : null);

    public async Task<IReadOnlyList<SparkTicketDate>> DatesAsync(IAsyncDocumentSession session, IEnumerable<string> dateIds, CancellationToken cancellationToken)
    {
        var result = new List<SparkTicketDate>();
        foreach (var date in await TicketCatalog.DatesAsync(session, dateIds, cancellationToken))
        {
            var place = await PlaceAsync(session, date, cancellationToken);
            result.Add(new SparkTicketDate(date.Id!, date.Name, date.StartsAt, date.DoorsOpenAt, date.EndsAt, place?.Name, date.VenueTimeZoneId));
        }
        return result;
    }

    /// <summary>A venue's name and address lines: from the date's frozen snapshot, else the venue itself.</summary>
    private async Task<(string Name, IReadOnlyList<string> Lines)?> PlaceAsync(IAsyncDocumentSession session, TicketEventDate date, CancellationToken cancellationToken)
    {
        if (date.LayoutSnapshotId is { Length: > 0 } snapshotId && await venues.GetSnapshotAsync(snapshotId, cancellationToken) is { } snapshot)
            return (snapshot.VenueName, snapshot.AddressLines);
        if (date.VenueId is { Length: > 0 } venueId && await session.LoadAsync<Venue>(venueId, cancellationToken) is { } venue)
            return (venue.Name, venue.Address?.Lines ?? []);
        return null;
    }

    // ---- print ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// The printed pages of <paramref name="tickets"/> for one recipient (their stored zone and culture, X14). Tickets
    /// without a recoverable secret, revoked ones and ones not yet available are left out.
    /// </summary>
    public async Task<(IReadOnlyList<TicketPrintPage> Pages, TicketPrintTemplate Template)> PrintAsync(
        IAsyncDocumentSession session, IReadOnlyList<Ticket> tickets, string? recipientUserId, IReadOnlyDictionary<string, string>? knownSecrets, CancellationToken cancellationToken)
    {
        var locale = await LocaleAsync(recipientUserId, cancellationToken);
        var language = Language(locale.Culture);
        var formatter = services.GetService<ISparkDateTimeFormatter>();
        var now = clock.GetUtcNow();
        var pages = new List<TicketPrintPage>();
        TicketEventSettings? settings = null;
        foreach (var ticket in tickets)
        {
            if (ticket.Status == ETicketStatus.Revoked || ticket.AvailableFrom is { } from && from > now)
                continue;
            var secret = knownSecrets is not null && knownSecrets.TryGetValue(ticket.Code, out var known) ? known : await RecoverSecretAsync(session, ticket, cancellationToken);
            if (secret is null)
                continue;
            var ev = await session.LoadAsync<TicketEvent>(ticket.EventId, cancellationToken);
            settings ??= await TicketCatalog.SettingsAsync(session, ticket.EventId, cancellationToken);
            var dateLines = new List<string>();
            string? venue = null;
            foreach (var date in await TicketCatalog.DatesAsync(session, ticket.DateIds, cancellationToken))
            {
                var when = await FormatAsync(formatter, date.StartsAt, date.VenueTimeZoneId, locale, cancellationToken);
                var label = date.Name?.GetValue(language) is { Length: > 0 } name ? name + ": " : "";
                var doors = date.DoorsOpenAt is { } d ? " (" + Doors(language) + " " + await FormatAsync(formatter, d, date.VenueTimeZoneId, locale, cancellationToken, ESparkDateTimeStyle.Time) + ")" : "";
                dateLines.Add(label + when + doors);
                if (venue is null && await PlaceAsync(session, date, cancellationToken) is { } place)
                    venue = string.Join(", ", new[] { place.Name }.Concat(place.Lines).Where(s => !string.IsNullOrWhiteSpace(s)));
            }
            var organizer = ev?.OrganizationId is { } orgId ? (await session.LoadAsync<Organizations.Models.Organization>(orgId, cancellationToken))?.Name : null;
            pages.Add(new TicketPrintPage(
                ticket.Code,
                TicketSecrets.Payload(ticket.Code, secret),
                ev?.Name.GetValue(language) ?? "",
                ticket.Title?.GetValue(language) ?? "",
                dateLines,
                venue,
                ticket.SeatLabel,
                ticket.HolderName,
                ticket.OrderNumber,
                ticket.MinimumAge,
                organizer));
        }

        var template = new TicketPrintTemplate(
            settings?.TemplateAccentColor ?? "#1f2937",
            settings?.TemplateAccentTextColor ?? "#ffffff",
            settings?.TemplateExtraText?.GetValue(language),
            settings?.HasLogo == true ? await ImageAsync(session, settings, TicketEventSettings.LogoAttachment, cancellationToken) : null,
            settings?.HasBackground == true ? await ImageAsync(session, settings, TicketEventSettings.BackgroundAttachment, cancellationToken) : null,
            language);
        return (pages, template);
    }

    public async Task<SparkRecipientLocale> LocaleAsync(string? userId, CancellationToken cancellationToken)
        => services.GetService<ISparkRecipientLocale>() is { } locales
            ? await locales.ForUserAsync(userId, cancellationToken)
            : new SparkRecipientLocale("UTC", TranslatedString.DefaultLanguage);

    public static string Language(string? culture)
        => culture is { Length: >= 2 } c ? c[..2].ToLowerInvariant() : TranslatedString.DefaultLanguage;

    private static async Task<string> FormatAsync(ISparkDateTimeFormatter? formatter, DateTimeOffset value, string? venueZone, SparkRecipientLocale locale, CancellationToken cancellationToken, ESparkDateTimeStyle style = ESparkDateTimeStyle.LongDateTime)
    {
        if (formatter is null)
            return value.UtcDateTime.ToString(style == ESparkDateTimeStyle.Time ? "HH:mm 'UTC'" : "yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);
        var options = new SparkDateTimeFormatOptions
        {
            Style = style,
            Hint = venueZone is { Length: > 0 } zone ? SparkDateTimeZone.InZone(zone) : null,
        };
        return await formatter.FormatAsync(value, locale, options, cancellationToken);
    }

    private static string Doors(string language) => language switch
    {
        "nl" => "deuren",
        "fr" => "portes",
        "de" => "Einlass",
        _ => "doors",
    };

    /// <summary>A template image (JPEG attachment of the settings document), or null.</summary>
    public static async Task<byte[]?> ImageAsync(IAsyncDocumentSession session, TicketEventSettings settings, string name, CancellationToken cancellationToken)
    {
        using var attachment = await session.Advanced.Attachments.GetAsync(settings, name, cancellationToken);
        if (attachment?.Stream is null)
            return null;
        using var copy = new MemoryStream();
        await attachment.Stream.CopyToAsync(copy, cancellationToken);
        return copy.ToArray();
    }
}
