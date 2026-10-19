using MintPlayer.Spark.Authorization.Identity;

namespace MintPlayer.Spark.Commerce.Ticketing.Tickets.Pdf;

/// <summary>One ticket as printed. Every text is already formatted for the recipient (X14: zone and culture).</summary>
/// <param name="Payload">The QR code's text (<c>ST1.{code}.{secret}</c>).</param>
/// <param name="Dates">The date lines (one per date of a pass), formatted with the venue-local hint (D33).</param>
/// <param name="MinimumAge">GB12: prints the "ID check" marker.</param>
public sealed record TicketPrintPage(
    string Code,
    string Payload,
    string EventName,
    string TicketTypeName,
    IReadOnlyList<string> Dates,
    string? Venue,
    string? Seat,
    string? HolderName,
    string OrderNumber,
    int? MinimumAge,
    string? Organizer);

/// <summary>GC7: the per-event template. Colours are <c>#rrggbb</c>; images are JPEG bytes.</summary>
public sealed record TicketPrintTemplate(
    string AccentColor,
    string AccentTextColor,
    string? ExtraText,
    byte[]? Logo,
    byte[]? Background,
    string Language)
{
    public static TicketPrintTemplate Plain(string language) => new("#1f2937", "#ffffff", null, null, null, language);
}

/// <summary>Renders tickets as PDF, one page per ticket. Replace it to change the layout; the default is <see cref="TicketPdfRenderer"/>.</summary>
public interface ITicketPdfRenderer
{
    byte[] Render(IReadOnlyList<TicketPrintPage> tickets, TicketPrintTemplate template);
}

/// <summary>
/// The default ticket layout (A4). <b>GC7:</b> the seller's template sets the accent bar's colours, a logo, a background
/// image behind the header and extra text; the library keeps the QR code, the printed code and secret, and the legal
/// block fixed, so no template can hide what the door and the law need. Labels in en, nl, fr and de.
/// </summary>
public sealed class TicketPdfRenderer : ITicketPdfRenderer
{
    private const float Left = 50, Right = 545, HeaderHeight = 120;

    private readonly SparkQrCodeRenderer qr = new();

    public byte[] Render(IReadOnlyList<TicketPrintPage> tickets, TicketPrintTemplate template)
    {
        ArgumentNullException.ThrowIfNull(tickets);
        var l = Labels.For(template.Language);
        var accent = TicketPdfWriter.IsColor(template.AccentColor) ? template.AccentColor : "#1f2937";
        var accentText = TicketPdfWriter.IsColor(template.AccentTextColor) ? template.AccentTextColor : "#ffffff";
        var pdf = new TicketPdfWriter();
        for (var index = 0; index < tickets.Count; index++)
        {
            if (index > 0)
                pdf.NewPage();
            var t = tickets[index];

            // Header: the template's colour, background and logo.
            pdf.Rect(0, 0, TicketPdfWriter.PageWidth, HeaderHeight, accent);
            if (template.Background is { } background)
                pdf.Jpeg(0, 0, TicketPdfWriter.PageWidth, HeaderHeight, background);
            var titleX = Left;
            if (template.Logo is { } logo && pdf.Jpeg(Left, 20, 80, 80, logo))
                titleX = Left + 95;
            var y = 48f;
            foreach (var line in TicketPdfWriter.Wrap(t.EventName, Right - titleX, 20, bold: true).Take(2))
            {
                pdf.Text(titleX, y, line, 20, bold: true, color: accentText);
                y += 24;
            }
            if (t.Organizer is { Length: > 0 } organizer)
                pdf.Text(titleX, Math.Min(y, HeaderHeight - 12), organizer, 10, color: accentText);

            // The ticket's facts.
            y = HeaderHeight + 36;
            pdf.Text(Left, y, t.TicketTypeName, 16, bold: true);
            y += 22;
            foreach (var date in t.Dates)
            {
                pdf.Text(Left, y, date, 11);
                y += 15;
            }
            if (t.Venue is { Length: > 0 } venue)
            {
                foreach (var line in TicketPdfWriter.Wrap(venue, 300, 10))
                {
                    pdf.Text(Left, y, line, 10, color: "#374151");
                    y += 13;
                }
            }
            y += 8;
            if (t.Seat is { Length: > 0 } seat)
            {
                pdf.Text(Left, y, l.Seat, 9, color: "#6b7280");
                pdf.Text(Left, y + 14, seat, 13, bold: true);
                y += 32;
            }
            if (t.HolderName is { Length: > 0 } holder)
            {
                pdf.Text(Left, y, l.Holder, 9, color: "#6b7280");
                pdf.Text(Left, y + 14, holder, 13, bold: true);
                y += 32;
            }
            pdf.Text(Left, y, l.Order + ": " + t.OrderNumber, 9, color: "#6b7280");
            y += 14;
            if (t.MinimumAge is { } age)
            {
                pdf.Rect(Left, y, 220, 22, "#b91c1c");
                pdf.Text(Left + 8, y + 15, string.Format(System.Globalization.CultureInfo.InvariantCulture, l.IdCheck, age), 11, bold: true, color: "#ffffff");
                y += 30;
            }

            // Fixed: the QR code, the code and the secret (for manual entry at the door).
            var qrTop = HeaderHeight + 30;
            pdf.Modules(355, qrTop, 190, qr.RenderModules(t.Payload));
            pdf.Text(355, qrTop + 206, l.Code + ": " + t.Code, 9, bold: true);
            var secret = t.Payload[(t.Payload.LastIndexOf('.') + 1)..];
            pdf.Text(355, qrTop + 219, l.Secret + ": " + secret, 7, color: "#374151");

            // The seller's extra text.
            y = Math.Max(y, qrTop + 240) + 10;
            if (template.ExtraText is { Length: > 0 } extra)
            {
                pdf.Line(Left, y, Right, y);
                y += 16;
                foreach (var line in TicketPdfWriter.Wrap(extra, Right - Left, 10).Take(20))
                {
                    pdf.Text(Left, y, line, 10);
                    y += 13;
                }
            }

            // Fixed: the legal block.
            y = Math.Max(y + 10, 700);
            pdf.Line(Left, y, Right, y);
            y += 14;
            foreach (var line in TicketPdfWriter.Wrap(l.Legal, Right - Left, 8))
            {
                pdf.Text(Left, y, line, 8, color: "#4b5563");
                y += 11;
            }
        }
        return pdf.Build();
    }

    private sealed record Labels(string Seat, string Holder, string Order, string Code, string Secret, string IdCheck, string Legal)
    {
        public static Labels For(string? language) => (language ?? "en")[..Math.Min(2, (language ?? "en").Length)].ToLowerInvariant() switch
        {
            "nl" => new("Plaats", "Houder", "Bestelling", "Code", "Geheim", "ID-controle: {0}+", "Dit ticket is persoonlijk en geeft één keer toegang per datum en lijst. Het QR-code-geheim wordt gecontroleerd aan de deur; een kopie wordt geweigerd zodra het origineel gebruikt is. Doorverkopen boven de nominale waarde is niet toegestaan; gebruik de officiële doorverkoop. Bij overdracht of doorverkoop wordt een nieuw ticket uitgegeven en vervalt dit ticket."),
            "fr" => new("Place", "Titulaire", "Commande", "Code", "Secret", "Contrôle d'identité : {0}+", "Ce billet est personnel et donne accès une fois par date et par liste. Le secret du code QR est vérifié à l'entrée ; une copie est refusée dès que l'original a été utilisé. La revente au-dessus de la valeur faciale est interdite ; utilisez la revente officielle. En cas de transfert ou de revente, un nouveau billet est émis et celui-ci n'est plus valable."),
            "de" => new("Platz", "Inhaber", "Bestellung", "Code", "Geheimnis", "Ausweiskontrolle: {0}+", "Dieses Ticket ist persönlich und gewährt einmal pro Datum und Liste Einlass. Das Geheimnis des QR-Codes wird am Einlass geprüft; eine Kopie wird abgewiesen, sobald das Original verwendet wurde. Ein Weiterverkauf über dem Nennwert ist nicht erlaubt; nutzen Sie den offiziellen Weiterverkauf. Bei Übertragung oder Weiterverkauf wird ein neues Ticket ausgestellt und dieses verliert seine Gültigkeit."),
            _ => new("Seat", "Holder", "Order", "Code", "Secret", "ID check: {0}+", "This ticket is personal and admits once per date and list. The QR code's secret is checked at the door; a copy is refused once the original has been used. Resale above face value is not allowed; use the official resale. A transfer or resale issues a new ticket and this one stops being valid."),
        };
    }
}
