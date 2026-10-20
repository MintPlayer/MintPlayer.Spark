using System.Globalization;
using System.Text;

namespace MintPlayer.Spark.Commerce.Ticketing.Events.Discovery;

/// <summary>One calendar entry (GB9 "add to calendar").</summary>
/// <param name="Uid">A globally unique, stable id (<c>{dateId}@{host}</c>), so a re-download updates the same entry.</param>
/// <param name="Description">Plain text: doors-open time (formatted by the core formatter, K10), the ticket page link.</param>
/// <param name="Location">The venue's name and address lines.</param>
/// <param name="VenueTimeZoneId">The venue's IANA zone, written as <c>X-WR-TIMEZONE</c> beside the UTC times.</param>
public sealed record EventCalendarEntry(
    string Uid,
    string Summary,
    DateTimeOffset Start,
    DateTimeOffset? End,
    DateTimeOffset Stamp,
    string? Description = null,
    string? Location = null,
    string? Url = null,
    string? VenueTimeZoneId = null,
    bool Cancelled = false);

/// <summary>
/// Writes an iCalendar file (RFC 5545) for event dates (GB9, §4.11): start and end in UTC (<c>…Z</c>), plus the venue's
/// IANA zone as <c>X-WR-TIMEZONE</c>. Writing instants in UTC needs no zone conversion, so nothing here formats or
/// converts a zone (K10); text that shows a local time (the doors) is formatted by <c>ISparkDateTimeFormatter</c> before
/// it is passed in. Pure. Tickets (M15) attaches the same output to ticket mails.
/// </summary>
public static class EventCalendar
{
    public const string ContentType = "text/calendar; charset=utf-8";

    public static string Build(IReadOnlyList<EventCalendarEntry> entries, string productId = "-//MintPlayer//Spark Ticketing//EN")
    {
        var lines = new List<string>
        {
            "BEGIN:VCALENDAR",
            "VERSION:2.0",
            "PRODID:" + productId,
            "CALSCALE:GREGORIAN",
            "METHOD:PUBLISH",
        };
        var zone = entries.Select(e => e.VenueTimeZoneId).FirstOrDefault(z => !string.IsNullOrEmpty(z));
        if (zone is not null)
            lines.Add("X-WR-TIMEZONE:" + Escape(zone));
        foreach (var entry in entries)
        {
            lines.Add("BEGIN:VEVENT");
            lines.Add("UID:" + Escape(entry.Uid));
            lines.Add("DTSTAMP:" + Utc(entry.Stamp));
            lines.Add("DTSTART:" + Utc(entry.Start));
            if (entry.End is { } end && end > entry.Start)
                lines.Add("DTEND:" + Utc(end));
            lines.Add("SUMMARY:" + Escape(entry.Summary));
            if (!string.IsNullOrWhiteSpace(entry.Description))
                lines.Add("DESCRIPTION:" + Escape(entry.Description));
            if (!string.IsNullOrWhiteSpace(entry.Location))
                lines.Add("LOCATION:" + Escape(entry.Location));
            if (!string.IsNullOrWhiteSpace(entry.Url))
                lines.Add("URL:" + entry.Url);
            lines.Add("STATUS:" + (entry.Cancelled ? "CANCELLED" : "CONFIRMED"));
            lines.Add("END:VEVENT");
        }
        lines.Add("END:VCALENDAR");

        var builder = new StringBuilder();
        foreach (var line in lines)
            Fold(builder, line);
        return builder.ToString();
    }

    /// <summary>An instant as an RFC 5545 UTC date-time (<c>20260704T180000Z</c>).</summary>
    public static string Utc(DateTimeOffset value) => value.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

    /// <summary>RFC 5545 §3.3.11 TEXT escaping: backslash, semicolon, comma and newlines.</summary>
    public static string Escape(string value)
        => value.Replace("\\", "\\\\").Replace(";", "\\;").Replace(",", "\\,").Replace("\r\n", "\\n").Replace("\n", "\\n").Replace("\r", "\\n");

    /// <summary>RFC 5545 §3.1: lines end in CRLF and are folded at 75 octets (continuations start with a space), never inside a UTF-8 sequence.</summary>
    private static void Fold(StringBuilder builder, string line)
    {
        var octets = 0;
        var limit = 75;
        foreach (var rune in line.EnumerateRunes())
        {
            var size = rune.Utf8SequenceLength;
            if (octets + size > limit)
            {
                builder.Append("\r\n ");
                octets = 1;
                limit = 75;
            }
            builder.Append(rune.ToString());
            octets += size;
        }
        builder.Append("\r\n");
    }
}
