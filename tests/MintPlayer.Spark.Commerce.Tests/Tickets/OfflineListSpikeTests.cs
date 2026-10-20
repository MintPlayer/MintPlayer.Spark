using System.Diagnostics;
using System.Text.Json;
using MintPlayer.Spark.Commerce.Ticketing.Tickets;
using MintPlayer.Spark.Commerce.Ticketing.Tickets.CheckIn;
using MintPlayer.Spark.Commerce.Ticketing.Tickets.Contracts;
using MintPlayer.Spark.Commerce.Ticketing.Tickets.Models;
using MintPlayer.Spark.Commerce.Tests.Events;
using MintPlayer.Spark.Testing;
using Xunit.Abstractions;
using static MintPlayer.Spark.Commerce.Tests.Events.EventFixtures;
using static MintPlayer.Spark.Commerce.Tests.Tickets.TicketFixtures;

namespace MintPlayer.Spark.Commerce.Tests.Tickets;

/// <summary>
/// Spike S9 (PRD §7: "a 50k-ticket list downloads, is stored and wiped; double entry across two offline devices detected
/// on sync"), at CI size per round 4 (no load runs): 2,000 tickets in pages of 500 by default. The full 50k list runs only
/// on request (<c>SPARK_SPIKE_S9_TICKETS=50000</c>); the result is recorded in PRD §13. Storing and wiping on the device is
/// the client's half (<c>ng-spark/ticketing/tickets</c> <c>scanner-store.spec.ts</c>); the double entry across two devices
/// is <see cref="TicketCheckInFlowTests.S9_a_double_entry_across_two_offline_devices_is_flagged_whichever_syncs_first"/>.
/// </summary>
public class OfflineListSpikeTests(ITestOutputHelper output) : SparkTestDriver
{
    private static int TicketCount => int.TryParse(Environment.GetEnvironmentVariable("SPARK_SPIKE_S9_TICKETS"), out var n) && n > 0 ? n : 2_000;

    [Fact]
    public async Task S9_the_offline_list_downloads_completely_in_pages()
    {
        var count = TicketCount;
        var options = Options(o => o.Scanner.ListPageSize = count >= 10_000 ? 5_000 : 500);
        var clock = new EventTestClock(Doors.AddMinutes(-30));
        var (dates, _, _) = await SeedEventAsync(Store);
        // Production verifiers use 50,000 iterations; the list's size does not depend on the count, so the seed uses 1.
        await StoreTicketsAsync(Store, dates, count, iterations: 1);
        var device = await StoreDeviceAsync(Store, TicketCheckInLists.DefaultListId(EventId), "Gate A");
        var checkIns = new TicketCheckIns(Store, Monitor(options), clock);

        var watch = Stopwatch.StartNew();
        var entries = new List<SparkScannerEntry>(count);
        long bytes = 0;
        var pages = 0;
        string? next = null;
        do
        {
            var page = await checkIns.ListPageAsync(device, next, CancellationToken.None);
            bytes += JsonSerializer.SerializeToUtf8Bytes(page, JsonSerializerOptions.Web).LongLength;
            entries.AddRange(page.Entries);
            next = page.Next;
            pages++;
        }
        while (next is not null);
        watch.Stop();

        entries.Should().HaveCount(count);
        entries.Select(e => e.Code).Should().OnlyHaveUniqueItems();
        pages.Should().Be((count + options.Scanner.ListPageSize - 1) / options.Scanner.ListPageSize + (count % options.Scanner.ListPageSize == 0 ? 1 : 0));
        output.WriteLine($"S9: {count} tickets, {pages} pages, {bytes / 1024.0:0.0} KiB JSON ({bytes / (double)count:0} B/ticket), {watch.ElapsedMilliseconds} ms");
    }
}
