using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Commerce.Ticketing.Events.Models;
using MintPlayer.Spark.Commerce.Ticketing.Events.Services;

namespace MintPlayer.Spark.Commerce.Tests.Events;

/// <summary>M17: the storefront's public event list (pure parts: the next date and the order).</summary>
public class EventListTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-20T12:00:00+02:00");

    private static TicketEvent Event(string slug) => new() { Id = "TicketEvents/" + slug, Slug = slug, Name = TranslatedString.Of("en", slug), IsPublished = true };

    private static TicketEventDate Date(string eventId, string starts, string? ends = null, ETicketEventStatus status = ETicketEventStatus.Scheduled)
        => new() { EventId = eventId, StartsAt = DateTimeOffset.Parse(starts), EndsAt = ends is null ? null : DateTimeOffset.Parse(ends), Status = status, VenueTimeZoneId = "Europe/Brussels" };

    [Fact]
    public void The_next_date_is_the_first_one_not_ended_and_not_cancelled()
    {
        var ev = Event("fest");
        var summary = SparkTicketEvents.Summary(ev, [
            Date(ev.Id!, "2026-10-01T20:00:00+02:00"),
            Date(ev.Id!, "2026-10-21T20:00:00+02:00", status: ETicketEventStatus.Cancelled),
            Date(ev.Id!, "2026-10-20T10:00:00+02:00", "2026-10-20T23:00:00+02:00"),
            Date(ev.Id!, "2026-11-01T20:00:00+01:00"),
        ], Now);
        summary.NextStartsAt.Should().Be(DateTimeOffset.Parse("2026-10-20T10:00:00+02:00"));
        summary.NextTimeZoneId.Should().Be("Europe/Brussels");
        summary.DateCount.Should().Be(4);
    }

    [Fact]
    public void Upcoming_events_come_first_by_date_and_past_ones_last()
    {
        var past = SparkTicketEvents.Summary(Event("past"), [Date("TicketEvents/past", "2026-01-01T20:00:00+01:00")], Now);
        var later = SparkTicketEvents.Summary(Event("later"), [Date("TicketEvents/later", "2026-12-01T20:00:00+01:00")], Now);
        var soon = SparkTicketEvents.Summary(Event("soon"), [Date("TicketEvents/soon", "2026-10-25T20:00:00+02:00")], Now);
        past.NextStartsAt.HasValue.Should().BeFalse();
        SparkTicketEvents.OrderForList([past, later, soon]).Select(s => s.Slug).Should().Equal("soon", "later", "past");
    }
}
