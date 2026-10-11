namespace SeoTester.Monitoring;

/// <summary>
/// When a monitored site's next scheduled run is due (<c>docs/seo_analyzer_PRD.md</c> F9, D12, §4.3 "Monitoring").
/// A site's schedule is <c>Off</c>, <c>Weekly</c> or <c>Monthly</c> (the keys of the host's <c>MonitoringSchedule</c>
/// lookup); the sweep runs every 15 minutes and dispatches each due slot once.
/// </summary>
public static class MonitoringPlan
{
    public const string Off = nameof(Off);
    public const string Weekly = nameof(Weekly);
    public const string Monthly = nameof(Monthly);

    /// <summary>Whether <paramref name="schedule"/> asks for scheduled runs.</summary>
    public static bool IsScheduled(string? schedule) => schedule is Weekly or Monthly;

    /// <summary>One period after <paramref name="fromUtc"/>, or null when the schedule is off (or unknown).</summary>
    public static DateTime? Advance(string? schedule, DateTime fromUtc) => schedule switch
    {
        Weekly => fromUtc.AddDays(7),
        Monthly => fromUtc.AddMonths(1),
        _ => null,
    };

    /// <summary>
    /// The first slot once a schedule is switched on (or changed): one period after the site's latest run, so a site
    /// audited yesterday is not audited again today; now when it was never audited or that moment has passed.
    /// </summary>
    public static DateTime? FirstSlot(string? schedule, DateTime? latestRunAtUtc, DateTime nowUtc)
    {
        if (!IsScheduled(schedule))
            return null;
        var next = latestRunAtUtc is { } latest ? Advance(schedule, latest)!.Value : nowUtc;
        return next < nowUtc ? nowUtc : next;
    }

    /// <summary>
    /// The slot after a dispatched one: a whole number of periods after <paramref name="dueUtc"/>, past
    /// <paramref name="nowUtc"/>. Slots missed while the application was down are skipped: one run, not a backlog.
    /// </summary>
    public static DateTime? NextAfter(string? schedule, DateTime dueUtc, DateTime nowUtc)
    {
        var next = Advance(schedule, dueUtc);
        while (next is { } n && n <= nowUtc)
            next = Advance(schedule, n);
        return next;
    }

    /// <summary>The deduplication key of one slot of one site: a slot is dispatched at most once, whatever the sweep does.</summary>
    public static string SlotKey(string siteId, DateTime slotUtc)
        => $"{siteId}:{slotUtc.ToUniversalTime():yyyyMMddTHHmmssZ}";
}
