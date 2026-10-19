using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Commerce.Shipping.Models;
using MintPlayer.Spark.ReferenceData;
using MintPlayer.Spark.Services;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Commerce.Shipping;

/// <summary>
/// Public holidays for dispatch cut-offs and delivery promises (GC11). None is registered by default: without one,
/// only <see cref="DispatchOptions.WorkingDays"/> count. <c>UseReferenceDataHolidays()</c> registers the reference
/// data's calendar (reference_data_PRD §4.4); a host may register its own (<c>[SparkDiscoverable]</c>). Several
/// calendars combine: a day is a holiday when any of them says so.
/// </summary>
[SparkDiscoverable]
public interface IShippingHolidayCalendar
{
    /// <param name="country">ISO 3166-1 alpha-2.</param>
    /// <param name="subdivision">ISO 3166-2 subdivision (<c>BE-VLG</c>) for regional holidays, or null.</param>
    /// <param name="date">The local date.</param>
    ValueTask<bool> IsHolidayAsync(string country, string? subdivision, DateOnly date, CancellationToken cancellationToken);
}

/// <summary>A delivery promise (GC11): the parcel leaves on <see cref="DispatchDate"/> and arrives between <see cref="From"/> and <see cref="By"/>.</summary>
public sealed record DeliveryPromise(DateOnly DispatchDate, DateOnly From, DateOnly By);

/// <summary>
/// Cut-offs and delivery promises (GC11). The clock is <see cref="TimeProvider"/>; the cut-off is a wall-clock time
/// in <see cref="DispatchOptions.TimeZone"/>, converted through the core's <see cref="SparkTimeZones"/> (K10: no
/// library converts zones itself). Dates are local dates; the delivery promise is a date range, rendered by the
/// client.
/// </summary>
public sealed class DispatchSchedule(IOptionsMonitor<SparkCommerceShippingOptions> options, IEnumerable<IShippingHolidayCalendar> calendars, TimeProvider time)
{
    /// <summary>No working day within this many days means the configuration is wrong; the search stops there.</summary>
    private const int SearchLimitDays = 60;

    /// <summary>
    /// The day a parcel paid at <paramref name="at"/> (now when null) leaves: today before the cut-off on a working
    /// day that is not a holiday, else the next such day.
    /// </summary>
    public async Task<DateOnly> NextDispatchDateAsync(DateTimeOffset? at, CancellationToken cancellationToken)
    {
        var dispatch = options.CurrentValue.Dispatch;
        var local = SparkTimeZones.ToZone(at ?? time.GetUtcNow(), SparkTimeZones.FindOrUtc(dispatch.TimeZone));
        var date = DateOnly.FromDateTime(local.DateTime);
        if (TimeOnly.FromDateTime(local.DateTime) >= dispatch.CutOff)
            date = date.AddDays(1);
        return await NextWorkingDayAsync(date, dispatch.HolidayCountry, dispatch.HolidaySubdivision, dispatch.EffectiveWorkingDays, cancellationToken);
    }

    /// <summary>
    /// The promise for a method's transit days to <paramref name="destination"/>: the dispatch date plus
    /// <see cref="TransitDaysOptions.Min"/>..<see cref="TransitDaysOptions.Max"/> delivery days (Monday to Friday),
    /// skipping the destination country's holidays when <see cref="DispatchOptions.SkipDestinationHolidays"/> is on.
    /// </summary>
    public async Task<DeliveryPromise> PromiseAsync(TransitDaysOptions transit, ShippingAddress destination, DateTimeOffset? at, CancellationToken cancellationToken)
    {
        var dispatch = options.CurrentValue.Dispatch;
        var leaves = await NextDispatchDateAsync(at, cancellationToken);
        var country = dispatch.SkipDestinationHolidays && ShippingAddressRules.IsCountryCode(destination.Country) ? destination.Country.ToUpperInvariant() : null;
        var subdivision = country is null ? null : destination.Subdivision;
        var min = Math.Max(0, transit.Min);
        var max = Math.Max(min, transit.Max);
        var from = await AddDeliveryDaysAsync(leaves, min, country, subdivision, cancellationToken);
        var by = await AddDeliveryDaysAsync(from, max - min, country, subdivision, cancellationToken);
        return new DeliveryPromise(leaves, from, by);
    }

    /// <summary>Whether any registered calendar says <paramref name="date"/> is a holiday in <paramref name="country"/>.</summary>
    public async ValueTask<bool> IsHolidayAsync(string? country, string? subdivision, DateOnly date, CancellationToken cancellationToken)
    {
        if (country is not { Length: 2 })
            return false;
        foreach (var calendar in calendars)
        {
            if (await calendar.IsHolidayAsync(country, subdivision, date, cancellationToken))
                return true;
        }
        return false;
    }

    private async Task<DateOnly> NextWorkingDayAsync(DateOnly date, string? country, string? subdivision, IReadOnlyList<DayOfWeek> workingDays, CancellationToken cancellationToken)
    {
        for (var i = 0; i < SearchLimitDays; i++, date = date.AddDays(1))
        {
            if (workingDays.Contains(date.DayOfWeek) && !await IsHolidayAsync(country, subdivision, date, cancellationToken))
                return date;
        }
        throw new InvalidOperationException($"No dispatch day within {SearchLimitDays} days: check Spark:Commerce:Shipping:Dispatch:WorkingDays.");
    }

    private async Task<DateOnly> AddDeliveryDaysAsync(DateOnly start, int days, string? country, string? subdivision, CancellationToken cancellationToken)
    {
        var date = start;
        var guard = 0;
        while (days > 0)
        {
            date = date.AddDays(1);
            if (++guard > SearchLimitDays * 4)
                break;
            if (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
                continue;
            if (await IsHolidayAsync(country, subdivision, date, cancellationToken))
                continue;
            days--;
        }
        return date;
    }
}

/// <summary>
/// The reference data's public holidays (GC11, reference_data_PRD §4.4), as the reference-data client syncs them into
/// this application's own database (<c>PublicHolidays</c>, ids <c>holidays/{cc}/{yyyy-MM-dd}</c>). Reads by id
/// only, so it needs no index. Registered by <c>UseReferenceDataHolidays()</c>; the sync itself is the
/// reference-data client's (<c>AddReferenceDataClient</c>), and without a synced calendar no day is a holiday.
/// </summary>
internal sealed class ReferenceDataHolidayCalendar(IDocumentStore store) : IShippingHolidayCalendar
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
