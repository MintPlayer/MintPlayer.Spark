using System.Globalization;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Commerce.Ticketing.Events.Models;
using MintPlayer.Spark.Commerce.Ticketing.Tickets.Contracts;
using MintPlayer.Spark.Commerce.Ticketing.Tickets.Models;
using MintPlayer.Spark.Commerce.Ticketing.Tickets.Secrets;
using MintPlayer.Spark.Commerce.Ticketing.Tickets.Services;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Commerce.Ticketing.Tickets.CheckIn;

/// <summary>
/// Check-in at the door (§4.11, GB3, S9):
/// <list type="bullet">
/// <item><b>online</b>: the scan verifies the secret (PBKDF2), applies the list's rules and records the presence on the
/// ticket under optimistic concurrency, so two gates scanning one ticket at once admit it once;</item>
/// <item><b>offline list</b>: the device downloads the event's tickets as PBKDF2 verifiers (never the secrets), paged,
/// and verifies with WebCrypto;</item>
/// <item><b>sync</b>: the device uploads its offline scans; each ticket's presence is recomputed in time order with
/// every device's scans, so an entry made on two offline devices is recorded twice and the later one is flagged as a
/// double entry, whichever device syncs first. Re-sending a scan id changes nothing.</item>
/// </list>
/// </summary>
internal sealed class TicketCheckIns(
    IDocumentStore store,
    IOptionsMonitor<SparkTicketingTicketsOptions> options,
    TimeProvider clock,
    TicketChangeFeed? feed = null)
{
    private DateTimeOffset Now => clock.GetUtcNow();

    // ---- session and list ---------------------------------------------------------------------------------------

    public async Task<SparkScannerSession?> SessionAsync(TicketScannerDevice device, TicketPresenter presenter, CancellationToken cancellationToken)
    {
        using var session = store.OpenAsyncSession();
        var ev = await session.LoadAsync<TicketEvent>(device.EventId, cancellationToken);
        var list = await TicketCatalog.ListAsync(session, device.ListId, cancellationToken);
        if (ev is null || list is null)
            return null;
        var eventId = ev.Id!;
        var allDates = await session.Query<TicketEventDate>().Where(d => d.EventId == eventId).ToListAsync(cancellationToken);
        var dateIds = (list.DateIds.Count > 0 ? list.DateIds : allDates.Select(d => d.Id!)).ToList();
        var types = await session.Query<TicketType>().Where(t => t.EventId == eventId).ToListAsync(cancellationToken);
        var settings = await TicketCatalog.SettingsAsync(session, eventId, cancellationToken);
        return new SparkScannerSession(
            device.Id!,
            device.Name,
            eventId,
            ev.Name,
            ToContract(list),
            await presenter.DatesAsync(session, dateIds, cancellationToken),
            device.DateId,
            options.CurrentValue.Secrets.Iterations,
            TicketTimes.FreezeAt(settings, allDates.OrderBy(d => d.StartsAt).ToList(), options.CurrentValue),
            device.ExpiresAt,
            Now,
            types.Select(t => new SparkScannerType(t.Id!, t.Name)).ToList());
    }

    public static SparkScannerList ToContract(TicketCheckInList list)
        => new(list.Id!, list.Name, list.Kind.ToString(), list.DateIds, list.TicketTypeIds, list.AllowReentry);

    /// <summary>
    /// A page of the offline list (S9): the event's tickets in code order, <c>Scanner:ListPageSize</c> per page. The
    /// cursor of the last page asks for changes later (<see cref="SyncAsync"/>).
    /// </summary>
    public async Task<SparkScannerListPage> ListPageAsync(TicketScannerDevice device, string? next, CancellationToken cancellationToken)
    {
        var pageSize = Math.Clamp(options.CurrentValue.Scanner.ListPageSize, 100, 20_000);
        var skip = next is { Length: > 2 } && next.StartsWith("p:", StringComparison.Ordinal) && int.TryParse(next.AsSpan(2), NumberStyles.None, CultureInfo.InvariantCulture, out var s) ? s : 0;
        using var session = store.OpenAsyncSession();
        var eventId = device.EventId;
        var serverTime = Now;
        var page = await session.Query<Ticket>()
            .Customize(c => c.WaitForNonStaleResults())
            .Where(t => t.EventId == eventId)
            .OrderBy(t => t.Code)
            .Skip(skip)
            .Take(pageSize)
            .ToListAsync(cancellationToken);
        var entries = page.Select(t => Entry(t, device.ListId)).ToList();
        return new SparkScannerListPage(entries, page.Count == pageSize ? "p:" + (skip + pageSize).ToString(CultureInfo.InvariantCulture) : null, Cursor(serverTime), serverTime);
    }

    public static SparkScannerEntry Entry(Ticket t, string listId)
    {
        var presence = t.Presence.Where(p => p.ListId == listId).ToList();
        return new SparkScannerEntry(
            t.Code, t.SecretSalt, t.SecretHash, t.SecretIterations, t.TicketTypeId, t.DateIds, t.Status.ToString(), t.MinimumAge,
            t.HolderName, t.SeatLabel,
            presence.Where(p => p.Inside).Select(p => p.DateId).ToList(),
            presence.Where(p => p.Entries > 0).Select(p => p.DateId).ToList());
    }

    private static string Cursor(DateTimeOffset at) => at.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset? ParseCursor(string? cursor)
        => DateTimeOffset.TryParse(cursor, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at) ? at : null;

    // ---- online scan ------------------------------------------------------------------------------------------

    /// <summary>An online scan: verify, apply the rules, record the presence atomically (OCC), log it.</summary>
    public async Task<SparkScanResult> ScanAsync(TicketScannerDevice device, SparkScanRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var scanId = ScanId(request.ScanId);
        var direction = Direction(request.Direction);
        if (!TicketSecrets.TryParse(request.Payload, out var code, out var secret))
            return Unknown(scanId, null);
        var result = await TicketSessions.RetryAsync(store, async session =>
        {
            var ticket = await session.LoadAsync<Ticket>(Ticket.IdFor(code), cancellationToken);
            if (ticket is null || ticket.EventId != device.EventId || !TicketSecrets.Verify(ticket, secret))
                return Unknown(scanId, null);
            var list = await TicketCatalog.ListAsync(session, device.ListId, cancellationToken);
            if (list is null)
                throw TicketException.Of(TicketRefusals.ListNotFound);
            var dates = await EventDatesAsync(session, device.EventId, cancellationToken);
            return await ApplyAsync(session, device, list, dates, ticket, scanId, direction, Now, request.DateId, offline: false, cancellationToken);
        }, cancellationToken);
        if (result.Result != TicketScanResults.Unknown)
            feed?.Changed(device.EventId);
        return result;
    }

    // ---- sync (S9) --------------------------------------------------------------------------------------------

    /// <summary>
    /// Uploads offline scans and returns each one's result as decided with every device's scans in time order, every
    /// double entry of the tickets touched (whoever scanned them), and the list's changes since <c>Since</c>.
    /// </summary>
    public async Task<SparkScannerSyncResult> SyncAsync(TicketScannerDevice device, SparkScannerSyncRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var o = options.CurrentValue.Scanner;
        var scans = request.Scans ?? [];
        if (scans.Count > o.MaxScansPerSync)
            throw TicketException.Of(TicketRefusals.TooManyScans, o.MaxScansPerSync.ToString(CultureInfo.InvariantCulture));
        var now = Now;
        var results = new List<SparkScanResult>();
        var doubleEntries = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var group in scans.GroupBy(s => (s.Code ?? "").ToUpperInvariant()))
        {
            var code = group.Key;
            if (!TicketSecrets.IsCode(code))
            {
                results.AddRange(group.Select(s => Unknown(ScanId(s.ScanId), null)));
                continue;
            }
            var (groupResults, flagged) = await TicketSessions.RetryAsync(store, async session =>
            {
                var outcomes = new List<SparkScanResult>();
                var ticket = await session.LoadAsync<Ticket>(Ticket.IdFor(code), cancellationToken);
                if (ticket is null || ticket.EventId != device.EventId)
                    return (outcomes: group.Select(s => Unknown(ScanId(s.ScanId), code)).ToList(), flagged: new List<string>());
                var list = await TicketCatalog.ListAsync(session, device.ListId, cancellationToken) ?? throw TicketException.Of(TicketRefusals.ListNotFound);
                var dates = await EventDatesAsync(session, device.EventId, cancellationToken);
                foreach (var scan in group.OrderBy(s => s.At))
                {
                    // A device clock ahead of the server is clamped: a scan cannot be later than it was received.
                    var at = scan.At > now + o.MaxClockSkew ? now : scan.At;
                    outcomes.Add(await ApplyAsync(session, device, list, dates, ticket, ScanId(scan.ScanId), Direction(scan.Direction), at, scan.DateId, offline: true, cancellationToken));
                }
                var flags = ticket.Presence.Where(p => p.ListId == list.Id).SelectMany(p => p.Events).Where(e => e.DoubleEntry).Select(e => e.ScanId).ToList();
                return (outcomes, flagged: flags);
            }, cancellationToken);
            results.AddRange(groupResults);
            foreach (var id in flagged)
                doubleEntries.Add(id);
        }

        if (scans.Count > 0)
            feed?.Changed(device.EventId);

        // The list's changes since the device's cursor: reissues, revocations, freezes, other devices' presence. A
        // truncated page answers the last change's instant as the cursor, so the device asks again (it upserts).
        var changes = new List<SparkScannerEntry>();
        var cursor = Cursor(now);
        if (ParseCursor(request.Since) is { } since)
        {
            using var session = store.OpenAsyncSession();
            var eventId = device.EventId;
            var pageSize = Math.Clamp(o.ListPageSize, 100, 20_000);
            var changed = await session.Query<Ticket>()
                .Customize(c => c.WaitForNonStaleResults())
                .Where(t => t.EventId == eventId && t.UpdatedAt > since)
                .OrderBy(t => t.UpdatedAt)
                .Take(pageSize)
                .ToListAsync(cancellationToken);
            changes.AddRange(changed.Select(t => Entry(t, device.ListId)));
            if (changed.Count == pageSize)
                cursor = Cursor(changed[^1].UpdatedAt.AddTicks(-1));
        }

        using (var session = store.OpenAsyncSession())
        {
            if (await session.LoadAsync<TicketScannerDevice>(device.Id!, cancellationToken) is { } stored)
            {
                stored.LastSyncAt = now;
                await session.SaveChangesAsync(cancellationToken);
            }
        }
        return new SparkScannerSyncResult(results, doubleEntries.ToList(), changes, cursor, now);
    }

    // ---- shared ---------------------------------------------------------------------------------------------

    private async Task<SparkScanResult> ApplyAsync(IAsyncDocumentSession session, TicketScannerDevice device, TicketCheckInList list, IReadOnlyList<TicketEventDate> dates,
        Ticket ticket, string scanId, ETicketScanDirection direction, DateTimeOffset at, string? requestedDate, bool offline, CancellationToken cancellationToken)
    {
        var dateId = CheckInRules.DateFor(list, ticket, dates, requestedDate ?? device.DateId, at);
        var logId = TicketScan.IdFor(scanId);
        var log = await session.LoadAsync<TicketScan>(logId, cancellationToken);
        if (log is not null)
            return Result(log.ScanIdOf(), log.Result, ticket, log.DoubleEntry, log.DateId); // a re-sent scan

        string result;
        var doubleEntry = false;
        string? conflict = null;
        if (CheckInRules.Refusal(ticket, list, dateId, direction) is { } refusal)
        {
            result = refusal;
        }
        else
        {
            var outcome = CheckInRules.Apply(ticket, list.Id!, dateId!, new TicketPresenceEvent
            {
                ScanId = scanId, Direction = direction, At = at, DeviceId = device.Id, Offline = offline,
            }, options.CurrentValue.Scanner.MaxPresenceEvents);
            result = outcome.Result;
            doubleEntry = outcome.Event.DoubleEntry;
            conflict = outcome.Event.ConflictScanId;
            ticket.UpdatedAt = Now;
            // Earlier scans whose flag changed now that this one is known (S9: the order devices sync in does not matter).
            foreach (var changed in outcome.Changed)
            {
                if (await session.LoadAsync<TicketScan>(TicketScan.IdFor(changed.ScanId), cancellationToken) is { } earlier)
                {
                    earlier.DoubleEntry = changed.DoubleEntry;
                    earlier.ConflictScanId = changed.ConflictScanId;
                    earlier.Result = changed.DoubleEntry ? TicketScanResults.AlreadyIn : TicketScanResults.Admitted;
                }
            }
        }

        await session.StoreAsync(new TicketScan
        {
            Id = logId,
            OrganizationId = ticket.OrganizationId,
            EventId = ticket.EventId,
            TicketCode = ticket.Code,
            ListId = list.Id!,
            DateId = dateId ?? "",
            DeviceId = device.Id,
            Direction = direction,
            Result = result,
            At = at,
            ReceivedAt = Now,
            Offline = offline,
            DoubleEntry = doubleEntry,
            ConflictScanId = conflict,
        }, logId, cancellationToken);
        return Result(scanId, result, ticket, doubleEntry, dateId);
    }

    private static SparkScanResult Result(string scanId, string result, Ticket ticket, bool doubleEntry, string? dateId)
        => new(scanId, result, ticket.Code, ticket.Title, ticket.SeatLabel, ticket.HolderName, ticket.MinimumAge, doubleEntry, dateId);

    private static SparkScanResult Unknown(string scanId, string? code)
        => new(scanId, TicketScanResults.Unknown, code, null, null, null, null, false, null);

    private static async Task<IReadOnlyList<TicketEventDate>> EventDatesAsync(IAsyncDocumentSession session, string eventId, CancellationToken cancellationToken)
        => await session.Query<TicketEventDate>().Where(d => d.EventId == eventId).ToListAsync(cancellationToken);

    /// <summary>A device's scan id (a UUID); anything else is replaced, so the gate log's ids stay well formed.</summary>
    private static string ScanId(string? scanId)
        => Guid.TryParse(scanId, out var id) ? id.ToString("N") : Guid.NewGuid().ToString("N");

    private static ETicketScanDirection Direction(string? direction)
        => string.Equals(direction, "Out", StringComparison.OrdinalIgnoreCase) ? ETicketScanDirection.Out : ETicketScanDirection.In;
}

internal static class TicketScanExtensions
{
    /// <summary>The device's scan id of a gate-log row.</summary>
    public static string ScanIdOf(this TicketScan scan) => scan.Id is { } id && id.StartsWith("TicketScans/", StringComparison.Ordinal) ? id["TicketScans/".Length..] : scan.Id ?? "";
}
