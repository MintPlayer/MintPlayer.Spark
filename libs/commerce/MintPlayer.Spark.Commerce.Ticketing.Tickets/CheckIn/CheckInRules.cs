using MintPlayer.Spark.Commerce.Ticketing.Events.Models;
using MintPlayer.Spark.Commerce.Ticketing.Tickets.Models;

namespace MintPlayer.Spark.Commerce.Ticketing.Tickets.CheckIn;

/// <summary>
/// The check-in rules of GB3 (PRD §4.11), pure so the server and the offline scanner (its TypeScript twin in
/// <c>ng-spark/ticketing/tickets</c>, <c>check-in-rules.ts</c>) decide alike:
/// <list type="bullet">
/// <item>a list admits its ticket types (all when it names none; a zone names its own) on its dates (all when none);</item>
/// <item>a ticket is valid on its own dates: one, or each date of a multi-day or combi pass, and each date of each list
/// counts its entries separately;</item>
/// <item>without re-entry the first entry uses the ticket on that list and date, and any later entry is a double entry;</item>
/// <item>with re-entry scans toggle in and out, and a second "in" without an "out" in between is a double entry;</item>
/// <item>a revoked ticket is refused, a frozen one (GB1) is "on hold".</item>
/// </list>
/// </summary>
public static class CheckInRules
{
    /// <summary>How long before doors (or the start) a date's scans begin to count as "that date". Default 6 hours.</summary>
    public static readonly TimeSpan BeforeDoors = TimeSpan.FromHours(6);

    /// <summary>How long after the start a date without an end still counts. Default 12 hours.</summary>
    public static readonly TimeSpan AfterStartWithoutEnd = TimeSpan.FromHours(12);

    /// <summary>
    /// The refusal of a scan before presence is looked at, or null when the ticket may pass this list on
    /// <paramref name="dateId"/> in <paramref name="direction"/>.
    /// </summary>
    public static string? Refusal(Ticket ticket, TicketCheckInList list, string? dateId, ETicketScanDirection direction)
    {
        if (ticket.EventId != list.EventId)
            return TicketScanResults.Unknown;
        if (ticket.Status == ETicketStatus.Revoked)
            return TicketScanResults.Revoked;
        if (ticket.Status == ETicketStatus.OnHold)
            return TicketScanResults.OnHold;
        if (dateId is null || !ticket.DateIds.Contains(dateId) || (list.DateIds.Count > 0 && !list.DateIds.Contains(dateId)))
            return TicketScanResults.WrongDate;
        if (list.TicketTypeIds.Count > 0 ? !list.TicketTypeIds.Contains(ticket.TicketTypeId) : list.Kind == ETicketCheckInListKind.Zone)
            return TicketScanResults.NotOnList;
        if (direction == ETicketScanDirection.Out && !list.AllowReentry)
            return TicketScanResults.NoReentry;
        return null;
    }

    /// <summary>
    /// The date a scan counts for: <paramref name="preferred"/> when the device was set to one, else the date of the
    /// list (and the ticket) whose window holds <paramref name="now"/>: from <see cref="BeforeDoors"/> before doors (or the
    /// start) until the end (or <see cref="AfterStartWithoutEnd"/> after the start). Null when none is on.
    /// </summary>
    public static string? DateFor(TicketCheckInList list, Ticket? ticket, IReadOnlyList<TicketEventDate> dates, string? preferred, DateTimeOffset now)
    {
        if (!string.IsNullOrEmpty(preferred))
            return preferred;
        foreach (var date in dates.OrderBy(d => d.StartsAt))
        {
            if (list.DateIds.Count > 0 && !list.DateIds.Contains(date.Id!))
                continue;
            if (ticket is not null && !ticket.DateIds.Contains(date.Id!))
                continue;
            if (IsOn(date, now))
                return date.Id;
        }
        return null;
    }

    /// <summary>Whether <paramref name="now"/> lies in <paramref name="date"/>'s check-in window.</summary>
    public static bool IsOn(TicketEventDate date, DateTimeOffset now)
    {
        var opens = (date.DoorsOpenAt ?? date.StartsAt) - BeforeDoors;
        var closes = date.EndsAt ?? date.StartsAt + AfterStartWithoutEnd;
        return now >= opens && now <= closes;
    }

    /// <summary>
    /// Adds <paramref name="scan"/> to the ticket's presence on (<paramref name="listId"/>, <paramref name="dateId"/>) and
    /// recomputes the whole history in time order, so the outcome does not depend on the order in which devices sync
    /// (S9: two offline devices that admitted the same ticket are both recorded, and the later entry is flagged). A
    /// scan id seen before is not added again: its earlier outcome is returned.
    /// </summary>
    /// <returns>The scan's outcome, and every scan whose double-entry flag changed (to update their gate-log rows).</returns>
    public static PresenceOutcome Apply(Ticket ticket, string listId, string dateId, TicketPresenceEvent scan, int maxEvents = 200)
    {
        var presence = ticket.Presence.FirstOrDefault(p => p.ListId == listId && p.DateId == dateId);
        if (presence is null)
        {
            presence = new TicketPresence { ListId = listId, DateId = dateId };
            ticket.Presence.Add(presence);
        }

        var known = presence.Events.FirstOrDefault(e => e.ScanId == scan.ScanId);
        if (known is not null)
            return new PresenceOutcome(ResultOf(known, presence), known, []);

        var before = presence.Events.ToDictionary(e => e.ScanId, e => e.DoubleEntry, StringComparer.Ordinal);
        presence.Events.Add(scan);
        Recompute(presence);
        if (presence.Events.Count > maxEvents)
            presence.Events.RemoveRange(0, presence.Events.Count - maxEvents);

        var changed = presence.Events.Where(e => e.ScanId != scan.ScanId && before.TryGetValue(e.ScanId, out var was) && was != e.DoubleEntry).ToList();
        return new PresenceOutcome(ResultOf(scan, presence), scan, changed);
    }

    /// <summary>Recomputes the double-entry flags and the inside state of <paramref name="presence"/> in time order.</summary>
    public static void Recompute(TicketPresence presence)
    {
        presence.Events.Sort(static (a, b) =>
        {
            var byTime = a.At.CompareTo(b.At);
            return byTime != 0 ? byTime : string.CompareOrdinal(a.ScanId, b.ScanId);
        });
        var inside = false;
        var entries = 0;
        TicketPresenceEvent? lastIn = null;
        foreach (var e in presence.Events)
        {
            e.DoubleEntry = false;
            e.ConflictScanId = null;
            if (e.Direction == ETicketScanDirection.In)
            {
                if (inside)
                {
                    e.DoubleEntry = true;
                    e.ConflictScanId = lastIn?.ScanId;
                    continue;
                }
                inside = true;
                entries++;
                lastIn = e;
            }
            else if (inside)
            {
                inside = false;
            }
        }
        presence.Inside = inside;
        presence.Entries = entries;
    }

    /// <summary>The outcome of one recorded scan given the recomputed presence.</summary>
    private static string ResultOf(TicketPresenceEvent scan, TicketPresence presence)
    {
        if (scan.Direction == ETicketScanDirection.In)
            return scan.DoubleEntry ? TicketScanResults.AlreadyIn : TicketScanResults.Admitted;
        // An exit counts when the ticket was inside just before it.
        var inside = false;
        foreach (var e in presence.Events)
        {
            if (ReferenceEquals(e, scan) || e.ScanId == scan.ScanId)
                return inside ? TicketScanResults.Exited : TicketScanResults.NotInside;
            if (e.Direction == ETicketScanDirection.In && !e.DoubleEntry)
                inside = true;
            else if (e.Direction == ETicketScanDirection.Out && inside)
                inside = false;
        }
        return TicketScanResults.NotInside;
    }
}

/// <summary>What a scan did: its result code, the recorded event, and the earlier scans whose double-entry flag changed.</summary>
public sealed record PresenceOutcome(string Result, TicketPresenceEvent Event, IReadOnlyList<TicketPresenceEvent> Changed);
