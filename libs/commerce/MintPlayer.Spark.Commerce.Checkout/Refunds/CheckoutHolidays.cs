using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.ReferenceData;
using MintPlayer.Spark.Services;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Commerce.Checkout.Refunds;

/// <summary>
/// Public holidays for counting refund periods (Regulation (EEC, Euratom) No 1182/71 Art. 3(4): a period whose last day
/// is a public holiday, a Saturday or a Sunday ends at the end of the next working day). None is registered by default:
/// without one, only weekends move a period's end. <c>UseCheckoutReferenceDataHolidays()</c> registers the reference data's
/// calendar; a host may register its own. Several calendars combine: a day is a holiday when any of them says so.
/// </summary>
public interface ICheckoutHolidayCalendar
{
    /// <summary>Whether <paramref name="date"/> is a public holiday in <paramref name="country"/> (ISO 3166-1 alpha-2).</summary>
    /// <param name="subdivision">ISO 3166-2 subdivision (<c>BE-VLG</c>) for regional holidays, or null.</param>
    ValueTask<bool> IsHolidayAsync(string country, string? subdivision, DateOnly date, CancellationToken cancellationToken);
}

/// <summary>
/// Loads, before a synchronous period computation, the holidays that can matter: the days from each candidate last day
/// onward (a period's end only ever moves forward, and no run of weekend + holidays is longer than the look-ahead).
/// </summary>
internal sealed class CheckoutHolidays(IEnumerable<ICheckoutHolidayCalendar> calendars, IOptionsMonitor<SparkCommerceCheckoutOptions> options)
{
    /// <summary>How many days past a candidate last day are looked up.</summary>
    internal const int LookAhead = 10;

    /// <summary>
    /// A predicate for <see cref="RefundMath.PeriodEnd"/> covering periods of <paramref name="days"/> days from each of
    /// <paramref name="starts"/>; null when no calendar or no <see cref="CheckoutRefundOptions.HolidayCountry"/> is set
    /// (weekend-only counting).
    /// </summary>
    public async Task<Func<DateOnly, bool>?> ForPeriodsAsync(IEnumerable<DateTimeOffset?> starts, int days, TimeZoneInfo zone, CancellationToken cancellationToken)
    {
        var settings = options.CurrentValue.Refunds;
        var all = calendars.ToList();
        if (all.Count == 0 || settings.HolidayCountry is not { Length: 2 } country)
            return null;
        country = country.ToUpperInvariant();

        var holidays = new HashSet<DateOnly>();
        var checkedDays = new HashSet<DateOnly>();
        foreach (var start in starts.OfType<DateTimeOffset>().Distinct())
        {
            var last = DateOnly.FromDateTime(SparkTimeZones.ToZone(start, zone).Date).AddDays(days);
            for (var i = 0; i <= LookAhead; i++)
            {
                var day = last.AddDays(i);
                if (!checkedDays.Add(day))
                    continue;
                foreach (var calendar in all)
                {
                    if (await calendar.IsHolidayAsync(country, settings.HolidaySubdivision, day, cancellationToken))
                    {
                        holidays.Add(day);
                        break;
                    }
                }
            }
        }
        return holidays.Count == 0 ? null : new Func<DateOnly, bool>(holidays.Contains);
    }
}

/// <summary>
/// The reference data's public holidays (GC11, reference_data_PRD §4.4) as the reference-data client syncs them into this
/// application's database (<c>PublicHolidays</c>, ids <c>holidays/{cc}/{yyyy-MM-dd}</c>). Reads by id only, so it needs
/// no index. Without a synced calendar no day is a holiday.
/// </summary>
internal sealed class ReferenceDataCheckoutHolidayCalendar(IDocumentStore store) : ICheckoutHolidayCalendar
{
    public async ValueTask<bool> IsHolidayAsync(string country, string? subdivision, DateOnly date, CancellationToken cancellationToken)
    {
        using var session = store.OpenAsyncSession();
        if (await session.Advanced.ExistsAsync(RefIds.Holiday(country, null, date), cancellationToken))
            return true;
        if (subdivision is not { Length: > 0 })
            return false;
        // ISO 3166-2 codes carry the country (BE-VLG); the id carries only the subdivision part.
        var dash = subdivision.IndexOf('-', StringComparison.Ordinal);
        var part = dash >= 0 ? subdivision[(dash + 1)..] : subdivision;
        if (part.Length == 0)
            return false;
        try { return await session.Advanced.ExistsAsync(RefIds.Holiday(country, part, date), cancellationToken); }
        catch (ArgumentException) { return false; }
    }
}
