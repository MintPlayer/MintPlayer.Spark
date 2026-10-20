using MintPlayer.Spark.Commerce.Ticketing.Events.Models;
using MintPlayer.Spark.Commerce.Ticketing.Tickets.CheckIn;
using MintPlayer.Spark.Commerce.Ticketing.Tickets.Models;

namespace MintPlayer.Spark.Commerce.Tests.Tickets;

/// <summary>
/// The check-in rules (GB3, S9), pure. The scanner's TypeScript twin (<c>check-in-rules.spec.ts</c>) runs the same cases,
/// so a device offline decides exactly as the server.
/// </summary>
public class CheckInRulesTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 1, 18, 0, 0, TimeSpan.Zero);

    private static Ticket Ticket(params string[] dateIds) => new()
    {
        Id = "Tickets/T", Code = "0123456789ABCDEF", EventId = "TicketEvents/1", TicketTypeId = "TicketTypes/standard",
        DateIds = dateIds.Length == 0 ? ["TicketEventDates/1"] : [.. dateIds],
    };

    private static TicketCheckInList List(bool reentry = false, ETicketCheckInListKind kind = ETicketCheckInListKind.Gate, string[]? types = null, string[]? dates = null) => new()
    {
        Id = "TicketCheckInLists/1", EventId = "TicketEvents/1", Name = "Gate A", Kind = kind, AllowReentry = reentry,
        TicketTypeIds = types?.ToList() ?? [], DateIds = dates?.ToList() ?? [],
    };

    private static TicketPresenceEvent Scan(string id, int minutes, ETicketScanDirection direction = ETicketScanDirection.In, string device = "A")
        => new() { ScanId = id, At = T0.AddMinutes(minutes), Direction = direction, DeviceId = device, Offline = true };

    [Fact]
    public void Without_reentry_the_second_entry_is_a_double_entry()
    {
        var ticket = Ticket();
        CheckInRules.Apply(ticket, "L", "TicketEventDates/1", Scan("s1", 0)).Result.Should().Be(TicketScanResults.Admitted);
        var second = CheckInRules.Apply(ticket, "L", "TicketEventDates/1", Scan("s2", 5));
        second.Result.Should().Be(TicketScanResults.AlreadyIn);
        second.Event.DoubleEntry.Should().BeTrue();
        second.Event.ConflictScanId.Should().Be("s1");
        ticket.Presence.Single().Entries.Should().Be(1);
    }

    [Fact]
    public void With_reentry_scans_toggle_and_only_an_in_after_an_in_is_flagged()
    {
        var ticket = Ticket();
        const string date = "TicketEventDates/1";
        CheckInRules.Apply(ticket, "L", date, Scan("in1", 0)).Result.Should().Be(TicketScanResults.Admitted);
        CheckInRules.Apply(ticket, "L", date, Scan("out1", 10, ETicketScanDirection.Out)).Result.Should().Be(TicketScanResults.Exited);
        CheckInRules.Apply(ticket, "L", date, Scan("in2", 20)).Result.Should().Be(TicketScanResults.Admitted);
        CheckInRules.Apply(ticket, "L", date, Scan("in3", 25)).Result.Should().Be(TicketScanResults.AlreadyIn);
        CheckInRules.Apply(ticket, "L", date, Scan("out2", 30, ETicketScanDirection.Out)).Result.Should().Be(TicketScanResults.Exited);
        CheckInRules.Apply(ticket, "L", date, Scan("out3", 31, ETicketScanDirection.Out)).Result.Should().Be(TicketScanResults.NotInside);

        var presence = ticket.Presence.Single();
        presence.Inside.Should().BeFalse();
        presence.Entries.Should().Be(2);
    }

    [Fact]
    public void Two_offline_devices_give_the_same_flags_whichever_syncs_first()
    {
        // S9: device A admitted the ticket at 18:00, device B at 18:03; B syncs first.
        var bFirst = Ticket();
        CheckInRules.Apply(bFirst, "L", "TicketEventDates/1", Scan("b", 3, device: "B")).Result.Should().Be(TicketScanResults.Admitted);
        var aLate = CheckInRules.Apply(bFirst, "L", "TicketEventDates/1", Scan("a", 0, device: "A"));
        aLate.Result.Should().Be(TicketScanResults.Admitted);
        aLate.Changed.Should().ContainSingle();
        aLate.Changed[0].ScanId.Should().Be("b");
        aLate.Changed[0].DoubleEntry.Should().BeTrue();

        var aFirst = Ticket();
        CheckInRules.Apply(aFirst, "L", "TicketEventDates/1", Scan("a", 0, device: "A"));
        CheckInRules.Apply(aFirst, "L", "TicketEventDates/1", Scan("b", 3, device: "B")).Result.Should().Be(TicketScanResults.AlreadyIn);

        Flags(bFirst).Should().Equal(Flags(aFirst));
        Flags(aFirst).Should().Equal(("a", false), ("b", true));

        static IEnumerable<(string, bool)> Flags(Ticket t) => t.Presence.Single().Events.Select(e => (e.ScanId, e.DoubleEntry));
    }

    [Fact]
    public void A_resent_scan_changes_nothing()
    {
        var ticket = Ticket();
        CheckInRules.Apply(ticket, "L", "TicketEventDates/1", Scan("s1", 0));
        var again = CheckInRules.Apply(ticket, "L", "TicketEventDates/1", Scan("s1", 0));
        again.Result.Should().Be(TicketScanResults.Admitted);
        again.Changed.Should().BeEmpty();
        ticket.Presence.Single().Events.Should().HaveCount(1);
    }

    [Fact]
    public void A_pass_counts_one_entry_per_date_and_list()
    {
        var ticket = Ticket("TicketEventDates/1", "TicketEventDates/2");
        CheckInRules.Apply(ticket, "L", "TicketEventDates/1", Scan("d1", 0)).Result.Should().Be(TicketScanResults.Admitted);
        CheckInRules.Apply(ticket, "L", "TicketEventDates/2", Scan("d2", 24 * 60)).Result.Should().Be(TicketScanResults.Admitted);
        CheckInRules.Apply(ticket, "VIP", "TicketEventDates/1", Scan("v1", 60)).Result.Should().Be(TicketScanResults.Admitted);
        ticket.Presence.Should().HaveCount(3);
        ticket.Presence.Should().OnlyContain(p => p.Entries == 1);
    }

    [Fact]
    public void Refusals_come_before_presence()
    {
        var list = List();
        const string date = "TicketEventDates/1";
        CheckInRules.Refusal(Ticket(), list, date, ETicketScanDirection.In).Should().BeNull();
        CheckInRules.Refusal(Ticket(), list, "TicketEventDates/9", ETicketScanDirection.In).Should().Be(TicketScanResults.WrongDate);
        CheckInRules.Refusal(Ticket(), list, null, ETicketScanDirection.In).Should().Be(TicketScanResults.WrongDate);
        CheckInRules.Refusal(Ticket(), list, date, ETicketScanDirection.Out).Should().Be(TicketScanResults.NoReentry);
        CheckInRules.Refusal(Ticket(), List(dates: ["TicketEventDates/2"]), date, ETicketScanDirection.In).Should().Be(TicketScanResults.WrongDate);

        var revoked = Ticket();
        revoked.Status = ETicketStatus.Revoked;
        CheckInRules.Refusal(revoked, list, date, ETicketScanDirection.In).Should().Be(TicketScanResults.Revoked);

        // GB1: a disputed order's ticket scans as "on hold".
        var held = Ticket();
        held.Status = ETicketStatus.OnHold;
        CheckInRules.Refusal(held, list, date, ETicketScanDirection.In).Should().Be(TicketScanResults.OnHold);

        var other = Ticket();
        other.EventId = "TicketEvents/2";
        CheckInRules.Refusal(other, list, date, ETicketScanDirection.In).Should().Be(TicketScanResults.Unknown);
    }

    [Fact]
    public void A_zone_admits_only_the_types_it_names()
    {
        const string date = "TicketEventDates/1";
        var vip = List(kind: ETicketCheckInListKind.Zone, types: ["TicketTypes/vip"]);
        CheckInRules.Refusal(Ticket(), vip, date, ETicketScanDirection.In).Should().Be(TicketScanResults.NotOnList);
        var vipTicket = Ticket();
        vipTicket.TicketTypeId = "TicketTypes/vip";
        CheckInRules.Refusal(vipTicket, vip, date, ETicketScanDirection.In).Should().BeNull();
        // A zone that names no type admits nobody (the list interceptor refuses saving one).
        CheckInRules.Refusal(vipTicket, List(kind: ETicketCheckInListKind.Zone), date, ETicketScanDirection.In).Should().Be(TicketScanResults.NotOnList);
    }

    [Fact]
    public void The_date_of_a_scan_is_the_one_whose_window_is_on()
    {
        var d1 = new TicketEventDate { Id = "TicketEventDates/1", EventId = "TicketEvents/1", StartsAt = T0, DoorsOpenAt = T0.AddHours(-1), EndsAt = T0.AddHours(5) };
        var d2 = new TicketEventDate { Id = "TicketEventDates/2", EventId = "TicketEvents/1", StartsAt = T0.AddDays(1) };
        var pass = Ticket("TicketEventDates/1", "TicketEventDates/2");
        var list = List();

        CheckInRules.DateFor(list, pass, [d1, d2], null, T0.AddHours(-6)).Should().Be("TicketEventDates/1"); // doors - 1 h - 6 h opens at T0-7h
        CheckInRules.DateFor(list, pass, [d1, d2], null, T0.AddHours(4)).Should().Be("TicketEventDates/1");
        CheckInRules.DateFor(list, pass, [d1, d2], null, T0.AddHours(12)).Should().BeNull();
        CheckInRules.DateFor(list, pass, [d1, d2], null, T0.AddDays(1).AddHours(11)).Should().Be("TicketEventDates/2"); // no end: start + 12 h
        CheckInRules.DateFor(list, pass, [d1, d2], "TicketEventDates/2", T0).Should().Be("TicketEventDates/2"); // the device's own date wins
        CheckInRules.DateFor(List(dates: ["TicketEventDates/2"]), pass, [d1, d2], null, T0).Should().BeNull();
        CheckInRules.DateFor(list, Ticket("TicketEventDates/2"), [d1, d2], null, T0).Should().BeNull();
    }

    [Fact]
    public void Presence_keeps_at_most_the_configured_events()
    {
        var ticket = Ticket();
        for (var i = 0; i < 12; i++)
            CheckInRules.Apply(ticket, "L", "TicketEventDates/1", Scan("s" + i.ToString("00"), i, i % 2 == 0 ? ETicketScanDirection.In : ETicketScanDirection.Out), maxEvents: 5);
        ticket.Presence.Single().Events.Should().HaveCount(5);
    }
}
