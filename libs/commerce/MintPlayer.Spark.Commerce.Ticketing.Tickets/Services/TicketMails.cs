using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Commerce.Payments;
using MintPlayer.Spark.Commerce.Ticketing.Events.Models;
using MintPlayer.Spark.Commerce.Ticketing.Events.Services;
using MintPlayer.Spark.Commerce.Ticketing.Tickets.Models;
using MintPlayer.Spark.Commerce.Ticketing.Tickets.Pdf;
using MintPlayer.Spark.MailManager;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Commerce.Ticketing.Tickets.Services;

/// <summary>
/// The ticket mails through MailManager (optional: without it nothing is sent and a warning is logged). The delivery
/// mail carries the tickets as one PDF (GC7 template) and an <c>.ics</c> per date (GB9, written by Events'
/// iCalendar writer, K10), plus a link per ticket whose fragment holds the secret. Every mail is
/// <see cref="SparkMailRequest.Sensitive"/>: it grants entry.
/// </summary>
internal sealed class TicketMails(
    IServiceProvider services,
    TicketPresenter presenter,
    ITicketPdfRenderer pdf,
    IOptionsMonitor<SparkTicketingTicketsOptions> options,
    ILogger<TicketMails> logger)
{
    private ISparkMailer? Mailer => services.GetService<ISparkMailer>();

    /// <summary>The app's public origin for links in mail.</summary>
    public string? PublicBaseUrl => SparkPublicBaseUrl.Resolve(
        services.GetService<IOptionsMonitor<SparkCommercePaymentsOptions>>()?.CurrentValue.PublicBaseUrl,
        services.GetService<IConfiguration>(), services.GetService<IHostEnvironment>(), services.GetService<IHttpContextAccessor>()?.HttpContext);

    public string Absolute(string path) => PublicBaseUrl is { } origin ? origin.TrimEnd('/') + path : path;

    /// <summary>The ticket page link; the secret goes in the fragment, which browsers never send to a server.</summary>
    public string TicketLink(string code, string secret)
        => Absolute(options.CurrentValue.Delivery.TicketPagePath.Replace("{code}", Uri.EscapeDataString(code), StringComparison.Ordinal)) + "#k=" + secret;

    /// <summary>
    /// Mails <paramref name="tickets"/> (one holder) with their PDF and calendar files. Deduplicated per ticket generation
    /// and <paramref name="occasion"/> (a re-send passes its own). Returns false when nothing could be sent.
    /// </summary>
    public async Task<bool> SendTicketsAsync(IAsyncDocumentSession session, IReadOnlyList<Ticket> tickets, string occasion,
        IReadOnlyDictionary<string, string>? knownSecrets, CancellationToken cancellationToken)
    {
        if (tickets.Count == 0)
            return false;
        if (Mailer is not { } mailer)
        {
            logger.LogWarning("No MailManager: tickets {Codes} are not mailed; holders open them on the ticket page.", string.Join(",", tickets.Select(t => t.Code)));
            return false;
        }
        var first = tickets[0];
        if (first.HolderEmail is not { Length: > 0 } email)
            return false;

        var (pages, template) = await presenter.PrintAsync(session, tickets, first.HolderUserId, knownSecrets, cancellationToken);
        if (pages.Count == 0)
            return false;
        var language = template.Language;
        var locale = await presenter.LocaleAsync(first.HolderUserId, cancellationToken);
        var ev = await session.LoadAsync<TicketEvent>(first.EventId, cancellationToken);

        var attachments = new List<SparkMailAttachment>
        {
            SparkMailAttachment.FromBytes(pages.Count == 1 ? $"ticket-{pages[0].Code}.pdf" : "tickets.pdf", "application/pdf", pdf.Render(pages, template)),
        };
        if (services.GetService<ISparkTicketEvents>() is { } events)
        {
            foreach (var dateId in tickets.SelectMany(t => t.DateIds).Distinct(StringComparer.Ordinal).Take(options.CurrentValue.Delivery.MaxCalendarAttachments))
            {
                if (await events.GetCalendarAsync(dateId, locale.Culture, cancellationToken) is { } ics)
                    attachments.Add(SparkMailAttachment.FromBytes($"event-{attachments.Count}.ics", "text/calendar", Encoding.UTF8.GetBytes(ics)));
            }
        }

        var printed = pages.Select(p => p.Code).ToHashSet(StringComparer.Ordinal);
        var lines = new List<object>();
        foreach (var page in pages)
        {
            var secret = page.Payload[(page.Payload.LastIndexOf('.') + 1)..];
            lines.Add(new { title = page.TicketTypeName, seat = page.Seat, holder = page.HolderName, dates = string.Join("; ", page.Dates), link = TicketLink(page.Code, secret) });
        }
        var key = string.Join(",", tickets.Where(t => printed.Contains(t.Code)).OrderBy(t => t.Code, StringComparer.Ordinal).Select(t => t.Code + ":" + t.Generation));
        await mailer.SendAsync(new SparkMailRequest
        {
            Template = options.CurrentValue.Delivery.MailTemplate,
            To = email,
            ToName = first.HolderName,
            Culture = locale.Culture,
            Data = new
            {
                holder_name = first.HolderName,
                event_name = ev?.Name.GetValue(language) ?? "",
                order_number = first.OrderNumber,
                tickets = lines,
                count = lines.Count,
            },
            Sensitive = true,
            Attachments = attachments,
            DeduplicationKey = "tickets:" + occasion + ":" + Hash(key),
        }, cancellationToken);
        return true;
    }

    /// <summary>D30: the transfer offer to its recipient, with the acceptance link (token in the fragment).</summary>
    public async Task SendTransferOfferAsync(IAsyncDocumentSession session, TicketTransfer transfer, string token, Ticket ticket, CancellationToken cancellationToken)
    {
        if (Mailer is not { } mailer || transfer.ToEmail is not { Length: > 0 } email)
        {
            logger.LogWarning("No MailManager or recipient: the transfer offer {TransferId} is not mailed.", transfer.Id);
            return;
        }
        var ev = await session.LoadAsync<TicketEvent>(ticket.EventId, cancellationToken);
        var language = TicketPresenter.Language(null);
        var link = Absolute(options.CurrentValue.Delivery.TransferPagePath.Replace("{id}", Uri.EscapeDataString(transfer.Id!["TicketTransfers/".Length..]), StringComparison.Ordinal)) + "#t=" + token;
        await mailer.SendAsync(new SparkMailRequest
        {
            Template = options.CurrentValue.Delivery.TransferMailTemplate,
            To = email,
            ToName = transfer.ToName,
            Data = new
            {
                recipient_name = transfer.ToName,
                event_name = ev?.Name.GetValue(language) ?? "",
                ticket_type = ticket.Title?.GetValue(language) ?? "",
                seat = ticket.SeatLabel,
                accept_url = link,
                expires = transfer.ExpiresAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'", System.Globalization.CultureInfo.InvariantCulture),
            },
            Sensitive = true,
            ExpiresAtUtc = transfer.ExpiresAt.UtcDateTime,
            DeduplicationKey = "tickets:transfer:" + transfer.Id,
        }, cancellationToken);
    }

    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..32];
}
