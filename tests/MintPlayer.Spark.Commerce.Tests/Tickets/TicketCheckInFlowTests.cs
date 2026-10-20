using MintPlayer.Spark.Commerce.Ticketing.Tickets;
using MintPlayer.Spark.Commerce.Ticketing.Tickets.CheckIn;
using MintPlayer.Spark.Commerce.Ticketing.Tickets.Contracts;
using MintPlayer.Spark.Commerce.Ticketing.Tickets.Models;
using MintPlayer.Spark.Commerce.Ticketing.Tickets.Scanner;
using MintPlayer.Spark.Commerce.Ticketing.Tickets.Secrets;
using MintPlayer.Spark.Commerce.Tests.Events;
using MintPlayer.Spark.Testing;
using Raven.Client.Documents;
using static MintPlayer.Spark.Commerce.Tests.Events.EventFixtures;
using static MintPlayer.Spark.Commerce.Tests.Tickets.TicketFixtures;

namespace MintPlayer.Spark.Commerce.Tests.Tickets;

/// <summary>
/// The door against RavenDB (§4.11, GB3, GB1, GB12, S9): online scans mark a ticket atomically per check-in list; two
/// gates scanning one ticket at once admit it once; offline scans from two devices are both recorded on sync and the
/// later entry is flagged whichever device syncs first; a re-sent sync changes nothing; the list pages and its changes;
/// a revoked device is refused (and wipes its list).
/// </summary>
public class TicketCheckInFlowTests : SparkTestDriver
{
    private readonly EventTestClock clock = new(Doors.AddMinutes(30));
    private readonly SparkTicketingTicketsOptions options = Options();

    private TicketCheckIns CheckIns() => new(Store, Monitor(options), clock);

    private ScannerDevices Devices() => new(Store, null!, null!, Monitor(options), clock);

    private static string DefaultList => TicketCheckInLists.DefaultListId(EventId);

    private static SparkScanRequest Online(string code, string secret, string direction = "In")
        => new(TicketSecrets.Payload(code, secret), Uuid(), direction);

    [Fact]
    public async Task An_online_scan_admits_once_and_flags_the_second_entry()
    {
        var (dates, _, _) = await SeedEventAsync(Store);
        var (ticket, secret) = (await StoreTicketsAsync(Store, dates, 1))[0];
        var device = await StoreDeviceAsync(Store, DefaultList, "Gate A");

        var first = await CheckIns().ScanAsync(device, Online(ticket.Code, secret), CancellationToken.None);
        first.Result.Should().Be(TicketScanResults.Admitted);
        first.DateId.Should().Be(dates[0]);
        var second = await CheckIns().ScanAsync(device, Online(ticket.Code, secret), CancellationToken.None);
        second.Result.Should().Be(TicketScanResults.AlreadyIn);
        second.DoubleEntry.Should().BeTrue();

        using var session = Store.OpenAsyncSession();
        var scans = await session.Query<TicketScan>().Customize(c => c.WaitForNonStaleResults()).Where(s => s.TicketCode == ticket.Code).ToListAsync();
        scans.Should().HaveCount(2);
        scans.Count(s => s.DoubleEntry).Should().Be(1);
    }

    [Fact]
    public async Task A_wrong_secret_or_another_events_ticket_is_unknown()
    {
        var (dates, _, _) = await SeedEventAsync(Store);
        var (ticket, _) = (await StoreTicketsAsync(Store, dates, 1))[0];
        var device = await StoreDeviceAsync(Store, DefaultList, "Gate A");

        (await CheckIns().ScanAsync(device, Online(ticket.Code, TicketSecrets.NewSecret()), CancellationToken.None)).Result.Should().Be(TicketScanResults.Unknown);
        (await CheckIns().ScanAsync(device, Online(TicketSecrets.NewCode(), TicketSecrets.NewSecret()), CancellationToken.None)).Result.Should().Be(TicketScanResults.Unknown);
        (await CheckIns().ScanAsync(device, new SparkScanRequest("not a ticket", Uuid()), CancellationToken.None)).Result.Should().Be(TicketScanResults.Unknown);
    }

    [Fact]
    public async Task Two_gates_scanning_one_ticket_at_once_admit_it_once()
    {
        var (dates, _, _) = await SeedEventAsync(Store);
        var (ticket, secret) = (await StoreTicketsAsync(Store, dates, 1))[0];
        var a = await StoreDeviceAsync(Store, DefaultList, "Gate A");
        var b = await StoreDeviceAsync(Store, DefaultList, "Gate B");

        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(i => CheckIns().ScanAsync(i % 2 == 0 ? a : b, Online(ticket.Code, secret), CancellationToken.None)));
        results.Count(r => r.Result == TicketScanResults.Admitted).Should().Be(1);
        results.Count(r => r.Result == TicketScanResults.AlreadyIn).Should().Be(5);
    }

    [Fact]
    public async Task S9_a_double_entry_across_two_offline_devices_is_flagged_whichever_syncs_first()
    {
        var (dates, _, _) = await SeedEventAsync(Store);
        var tickets = await StoreTicketsAsync(Store, dates, 2);
        var a = await StoreDeviceAsync(Store, DefaultList, "Gate A");
        var b = await StoreDeviceAsync(Store, DefaultList, "Gate B");

        // Ticket 0: A admitted at 17:05, B at 17:10. Ticket 1: only B. B syncs first.
        var a0 = new SparkOfflineScan(Uuid(), tickets[0].Ticket.Code, Doors.AddMinutes(5));
        var b0 = new SparkOfflineScan(Uuid(), tickets[0].Ticket.Code, Doors.AddMinutes(10));
        var b1 = new SparkOfflineScan(Uuid(), tickets[1].Ticket.Code, Doors.AddMinutes(11));

        var bSync = await CheckIns().SyncAsync(b, new SparkScannerSyncRequest([b0, b1]), CancellationToken.None);
        bSync.Results.Should().OnlyContain(r => r.Result == TicketScanResults.Admitted);
        bSync.DoubleEntries.Should().BeEmpty();

        var aSync = await CheckIns().SyncAsync(a, new SparkScannerSyncRequest([a0]), CancellationToken.None);
        aSync.Results.Single().Result.Should().Be(TicketScanResults.Admitted);
        // A's earlier entry stands; B's later one is now the double entry, reported to whoever syncs.
        aSync.DoubleEntries.Should().Equal(Normalize(b0.ScanId));

        using var session = Store.OpenAsyncSession();
        var log = await session.LoadAsync<TicketScan>(TicketScan.IdFor(Normalize(b0.ScanId)));
        log.DoubleEntry.Should().BeTrue();
        log.Result.Should().Be(TicketScanResults.AlreadyIn);
        log.ConflictScanId.Should().Be(Normalize(a0.ScanId));
        (await session.LoadAsync<TicketScan>(TicketScan.IdFor(Normalize(b1.ScanId)))).DoubleEntry.Should().BeFalse();
        var stored = await session.LoadAsync<Ticket>(tickets[0].Ticket.Id);
        stored.Presence.Single().Entries.Should().Be(1);
        stored.Presence.Single().Events.Should().HaveCount(2);
    }

    [Fact]
    public async Task A_resent_sync_changes_nothing()
    {
        var (dates, _, _) = await SeedEventAsync(Store);
        var (ticket, _) = (await StoreTicketsAsync(Store, dates, 1))[0];
        var device = await StoreDeviceAsync(Store, DefaultList, "Gate A");
        var scan = new SparkOfflineScan(Uuid(), ticket.Code, Doors.AddMinutes(5));

        var first = await CheckIns().SyncAsync(device, new SparkScannerSyncRequest([scan]), CancellationToken.None);
        var again = await CheckIns().SyncAsync(device, new SparkScannerSyncRequest([scan]), CancellationToken.None);
        again.Results.Single().Result.Should().Be(first.Results.Single().Result);

        using var session = Store.OpenAsyncSession();
        (await session.LoadAsync<Ticket>(ticket.Id)).Presence.Single().Events.Should().HaveCount(1);
        (await session.Query<TicketScan>().Customize(c => c.WaitForNonStaleResults()).CountAsync(s => s.TicketCode == ticket.Code)).Should().Be(1);
    }

    [Fact]
    public async Task A_device_clock_ahead_of_the_server_is_clamped()
    {
        var (dates, _, _) = await SeedEventAsync(Store);
        var (ticket, _) = (await StoreTicketsAsync(Store, dates, 1))[0];
        var device = await StoreDeviceAsync(Store, DefaultList, "Gate A");
        await CheckIns().SyncAsync(device, new SparkScannerSyncRequest([new SparkOfflineScan(Uuid(), ticket.Code, clock.Now.AddHours(3))]), CancellationToken.None);

        using var session = Store.OpenAsyncSession();
        (await session.LoadAsync<Ticket>(ticket.Id)).Presence.Single().Events.Single().At.Should().Be(clock.Now);
    }

    [Fact]
    public async Task Zones_and_reentry_follow_the_list()
    {
        var (dates, standard, vip) = await SeedEventAsync(Store);
        var standardTicket = (await StoreTicketsAsync(Store, dates, 1, standard))[0];
        var vipTicket = (await StoreTicketsAsync(Store, dates, 1, vip))[0];
        using (var session = Store.OpenAsyncSession())
        {
            await session.StoreAsync(new TicketCheckInList { Id = "TicketCheckInLists/vip", EventId = EventId, OrganizationId = Organization, Name = "VIP", Kind = ETicketCheckInListKind.Zone, TicketTypeIds = [vip], AllowReentry = true }, "TicketCheckInLists/vip");
            await session.SaveChangesAsync();
        }
        var zone = await StoreDeviceAsync(Store, "TicketCheckInLists/vip", "VIP door");

        (await CheckIns().ScanAsync(zone, Online(standardTicket.Ticket.Code, standardTicket.Secret), CancellationToken.None)).Result.Should().Be(TicketScanResults.NotOnList);
        (await CheckIns().ScanAsync(zone, Online(vipTicket.Ticket.Code, vipTicket.Secret), CancellationToken.None)).Result.Should().Be(TicketScanResults.Admitted);
        (await CheckIns().ScanAsync(zone, Online(vipTicket.Ticket.Code, vipTicket.Secret, "Out"), CancellationToken.None)).Result.Should().Be(TicketScanResults.Exited);
        (await CheckIns().ScanAsync(zone, Online(vipTicket.Ticket.Code, vipTicket.Secret), CancellationToken.None)).Result.Should().Be(TicketScanResults.Admitted);

        // The implicit list stops existing once the event has a list of its own.
        var gate = await StoreDeviceAsync(Store, DefaultList, "Gate A");
        var stale = () => CheckIns().ScanAsync(gate, Online(vipTicket.Ticket.Code, vipTicket.Secret), CancellationToken.None);
        (await stale.Should().ThrowAsync<TicketException>()).Which.Code.Should().Be(TicketRefusals.ListNotFound);
    }

    [Fact]
    public async Task GB1_and_GB12_an_on_hold_ticket_is_refused_and_an_age_restricted_one_shows_its_age()
    {
        var (dates, _, _) = await SeedEventAsync(Store);
        var (held, heldSecret) = NewTicket(dates);
        held.Status = ETicketStatus.OnHold;
        var (adult, adultSecret) = NewTicket(dates);
        adult.MinimumAge = 18;
        using (var session = Store.OpenAsyncSession())
        {
            await session.StoreAsync(held, held.Id);
            await session.StoreAsync(adult, adult.Id);
            await session.SaveChangesAsync();
        }
        var device = await StoreDeviceAsync(Store, DefaultList, "Gate A");

        (await CheckIns().ScanAsync(device, Online(held.Code, heldSecret), CancellationToken.None)).Result.Should().Be(TicketScanResults.OnHold);
        var admitted = await CheckIns().ScanAsync(device, Online(adult.Code, adultSecret), CancellationToken.None);
        admitted.Result.Should().Be(TicketScanResults.Admitted);
        admitted.MinimumAge.Should().Be(18);
    }

    [Fact]
    public async Task A_multi_day_pass_enters_once_per_date()
    {
        var (dates, _, _) = await SeedEventAsync(Store, dates: 2);
        var (pass, secret) = (await StoreTicketsAsync(Store, dates, 1))[0];
        var device = await StoreDeviceAsync(Store, DefaultList, "Gate A");

        (await CheckIns().ScanAsync(device, Online(pass.Code, secret), CancellationToken.None)).Result.Should().Be(TicketScanResults.Admitted);
        clock.Now = Doors.AddDays(1).AddMinutes(10);
        var dayTwo = await CheckIns().ScanAsync(device, Online(pass.Code, secret), CancellationToken.None);
        dayTwo.Result.Should().Be(TicketScanResults.Admitted);
        dayTwo.DateId.Should().Be(dates[1]);
        (await CheckIns().ScanAsync(device, Online(pass.Code, secret), CancellationToken.None)).Result.Should().Be(TicketScanResults.AlreadyIn);
    }

    [Fact]
    public async Task The_offline_list_pages_carries_verifiers_only_and_reports_changes_since_its_cursor()
    {
        options.Scanner.ListPageSize = 100;
        var (dates, _, _) = await SeedEventAsync(Store);
        var tickets = await StoreTicketsAsync(Store, dates, 250);
        var device = await StoreDeviceAsync(Store, DefaultList, "Gate A");

        var entries = new List<SparkScannerEntry>();
        string? next = null;
        string? cursor = null;
        var pages = 0;
        do
        {
            var page = await CheckIns().ListPageAsync(device, next, CancellationToken.None);
            cursor ??= page.Cursor; // the first page's cursor: changes made during the download come with the next sync
            entries.AddRange(page.Entries);
            next = page.Next;
            pages++;
        }
        while (next is not null);

        pages.Should().Be(3);
        entries.Select(e => e.Code).Should().OnlyHaveUniqueItems();
        entries.Should().HaveCount(250);
        // The device can verify a scanned secret against the entry alone, and the entry cannot mint one.
        var sample = tickets[17];
        var entry = entries.Single(e => e.Code == sample.Ticket.Code);
        TicketSecrets.Verify(new TicketSecretVerifier(entry.Salt, entry.Hash, entry.Iterations), sample.Secret).Should().BeTrue();

        // A revocation after the download reaches the device with its next sync.
        clock.Now = clock.Now.AddMinutes(1);
        using (var session = Store.OpenAsyncSession())
        {
            var revoked = await session.LoadAsync<Ticket>(tickets[3].Ticket.Id);
            revoked.Status = ETicketStatus.Revoked;
            revoked.UpdatedAt = clock.Now;
            await session.SaveChangesAsync();
        }
        var sync = await CheckIns().SyncAsync(device, new SparkScannerSyncRequest([], cursor), CancellationToken.None);
        sync.Changes.Should().ContainSingle();
        sync.Changes[0].Code.Should().Be(tickets[3].Ticket.Code);
        sync.Changes[0].Status.Should().Be(nameof(ETicketStatus.Revoked));
    }

    [Fact]
    public async Task A_revoked_or_expired_device_is_refused_and_a_wrong_token_is_unknown()
    {
        await SeedEventAsync(Store);
        var token = TicketSecrets.NewToken();
        var device = await StoreDeviceAsync(Store, DefaultList, "Gate A", token);
        var key = device.Id!["TicketScannerDevices/".Length..];

        (await Devices().AuthenticateAsync(key + "." + token, CancellationToken.None))!.Id.Should().Be(device.Id);
        (await Devices().AuthenticateAsync(key + "." + TicketSecrets.NewToken(), CancellationToken.None)).Should().BeNull();
        (await Devices().AuthenticateAsync(null, CancellationToken.None)).Should().BeNull();
        (await Devices().AuthenticateAsync("NOT-HEX." + token, CancellationToken.None)).Should().BeNull();

        using (var session = Store.OpenAsyncSession())
        {
            (await session.LoadAsync<TicketScannerDevice>(device.Id)).Revoked = true;
            await session.SaveChangesAsync();
        }
        var revoked = () => Devices().AuthenticateAsync(key + "." + token, CancellationToken.None);
        (await revoked.Should().ThrowAsync<TicketException>()).Which.Code.Should().Be(TicketRefusals.DeviceRevoked);

        var expiredToken = TicketSecrets.NewToken();
        var expired = await StoreDeviceAsync(Store, DefaultList, "Old", expiredToken, expires: clock.Now.AddMinutes(-1));
        var late = () => Devices().AuthenticateAsync(expired.Id!["TicketScannerDevices/".Length..] + "." + expiredToken, CancellationToken.None);
        (await late.Should().ThrowAsync<TicketException>()).Which.Code.Should().Be(TicketRefusals.DeviceRevoked);
    }

    [Fact]
    public async Task A_sync_refuses_more_scans_than_allowed()
    {
        options.Scanner.MaxScansPerSync = 2;
        await SeedEventAsync(Store);
        var device = await StoreDeviceAsync(Store, DefaultList, "Gate A");
        var scans = Enumerable.Range(0, 3).Select(_ => new SparkOfflineScan(Uuid(), TicketSecrets.NewCode(), Doors)).ToList();
        var sync = () => CheckIns().SyncAsync(device, new SparkScannerSyncRequest(scans), CancellationToken.None);
        (await sync.Should().ThrowAsync<TicketException>()).Which.Code.Should().Be(TicketRefusals.TooManyScans);
    }

    private static string Normalize(string scanId) => Guid.Parse(scanId).ToString("N");
}
