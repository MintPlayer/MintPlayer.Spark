using MintPlayer.Spark.Commerce.Checkout;
using MintPlayer.Spark.Commerce.Checkout.Refunds;
using MintPlayer.Spark.Commerce.Tests.Payments;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Commerce.Tests.Checkout;

/// <summary>
/// Item 9: Reg. (EEC, Euratom) No 1182/71 Art. 3(4): a period whose last day is a public holiday ends at the end of the
/// next working day. Holidays come from an optional calendar; without one, only weekends move the end.
/// </summary>
public class RefundHolidayTests
{
    private sealed class FixedCalendar(params string[] days) : ICheckoutHolidayCalendar
    {
        public List<DateOnly> Asked { get; } = [];

        public ValueTask<bool> IsHolidayAsync(string country, string? subdivision, DateOnly date, CancellationToken cancellationToken)
        {
            Asked.Add(date);
            return ValueTask.FromResult(country == "BE" && days.Contains(date.ToString("yyyy-MM-dd")));
        }
    }

    private static readonly TimeZoneInfo Utc = SparkTimeZones.FindOrUtc("UTC");

    [Fact]
    public void A_last_day_on_a_public_holiday_moves_to_the_next_working_day()
    {
        // Delivered Wednesday 2026-10-28: day 14 is Wednesday 11 November (Armistice Day, BE) → Thursday the 12th.
        var holidays = new HashSet<DateOnly> { new(2026, 11, 11) };
        RefundMath.PeriodEnd(DateTimeOffset.Parse("2026-10-28T10:00:00Z"), 14, Utc, isHoliday: holidays.Contains)
            .Should().Be(DateTimeOffset.Parse("2026-11-13T00:00:00Z"));
        // Default (no calendar): weekend-only, the period ends with the 11th.
        RefundMath.PeriodEnd(DateTimeOffset.Parse("2026-10-28T10:00:00Z"), 14, Utc)
            .Should().Be(DateTimeOffset.Parse("2026-11-12T00:00:00Z"));
    }

    [Fact]
    public void A_holiday_followed_by_a_weekend_moves_past_both()
    {
        // Day 14 Friday 2026-12-25 (Christmas) → Saturday, Sunday → Monday the 28th.
        var holidays = new HashSet<DateOnly> { new(2026, 12, 25) };
        RefundMath.PeriodEnd(DateTimeOffset.Parse("2026-12-11T10:00:00Z"), 14, Utc, isHoliday: holidays.Contains)
            .Should().Be(DateTimeOffset.Parse("2026-12-29T00:00:00Z"));
    }

    [Fact]
    public void The_guarantee_period_is_never_moved()
    {
        var holidays = new HashSet<DateOnly> { new(2026, 11, 11) };
        RefundMath.PeriodEnd(DateTimeOffset.Parse("2026-10-28T10:00:00Z"), 14, Utc, moveOffWeekend: false, isHoliday: holidays.Contains)
            .Should().Be(DateTimeOffset.Parse("2026-11-12T00:00:00Z"));
    }

    private static CheckoutHolidays Holidays(string? country, params ICheckoutHolidayCalendar[] calendars)
    {
        var options = new SparkCommerceCheckoutOptions();
        options.Refunds.HolidayCountry = country;
        return new CheckoutHolidays(calendars, new StaticOptionsMonitor<SparkCommerceCheckoutOptions>(options));
    }

    [Fact]
    public async Task Without_a_calendar_or_a_country_counting_is_weekend_only()
    {
        (await Holidays("BE").ForPeriodsAsync([DateTimeOffset.Parse("2026-10-28T10:00:00Z")], 14, Utc, default)).Should().BeNull();
        var calendar = new FixedCalendar("2026-11-11");
        (await Holidays(null, calendar).ForPeriodsAsync([DateTimeOffset.Parse("2026-10-28T10:00:00Z")], 14, Utc, default)).Should().BeNull();
        calendar.Asked.Should().BeEmpty();
    }

    [Fact]
    public async Task The_calendar_is_asked_from_each_candidate_last_day_and_feeds_the_period()
    {
        var calendar = new FixedCalendar("2026-11-11");
        var isHoliday = await Holidays("be", calendar).ForPeriodsAsync([DateTimeOffset.Parse("2026-10-28T10:00:00Z"), null], 14, Utc, default);

        isHoliday.Should().NotBeNull();
        calendar.Asked.Min().Should().Be(new DateOnly(2026, 11, 11));
        calendar.Asked.Count.Should().Be(CheckoutHolidays.LookAhead + 1);
        RefundMath.PeriodEnd(DateTimeOffset.Parse("2026-10-28T10:00:00Z"), 14, Utc, isHoliday: isHoliday)
            .Should().Be(DateTimeOffset.Parse("2026-11-13T00:00:00Z"));
    }
}
