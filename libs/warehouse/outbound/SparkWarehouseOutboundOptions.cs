using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Warehouse.Outbound;

/// <summary>
/// Outbound's part of <c>Spark:Warehouse</c> (PRD §4.13): <c>Routing</c>, <c>Release</c>, <c>Picking</c>,
/// <c>ShortPick</c>, <c>Cancellation</c> and <c>Consolidation</c>. Bound from the same section as the core's options; code
/// given to <c>AddWarehouseOutbound(o => …)</c> runs after it, and startup refuses an invalid value.
/// </summary>
public sealed class SparkWarehouseOutboundOptions : ISparkConfigurationSection
{
    public static string SectionName => "Spark:Warehouse";

    public RoutingOptions Routing { get; set; } = new();

    public ReleaseOptions Release { get; set; } = new();

    public PickingOptions Picking { get; set; } = new();

    public ShortPickOptions ShortPick { get; set; } = new();

    public CancellationOptions Cancellation { get; set; } = new();

    public sealed class RoutingOptions
    {
        /// <summary>
        /// Rules in order (WD3): the first whose non-null fields all match the order sends it to <see cref="RoutingRule.Site"/>.
        /// No match: the owner link's default site.
        /// </summary>
        public List<RoutingRule> Rules { get; set; } = [];
    }

    /// <summary>One routing rule: (owner, channel, ship-to country) → site, each part optional.</summary>
    public sealed class RoutingRule
    {
        /// <summary>The warehouse owner organization (the owner link maps the shop's owner id to it), or null for any.</summary>
        public string? Owner { get; set; }
        public string? Channel { get; set; }
        /// <summary>ISO 3166-1 alpha-2, or null for any.</summary>
        public string? Country { get; set; }
        /// <summary>The site code (<c>warehouse-sites/{code}</c>).</summary>
        public string Site { get; set; } = "";
    }

    public sealed class ReleaseOptions
    {
        public ReleaseMode Mode { get; set; } = ReleaseMode.Immediate;

        /// <summary>
        /// Wave cut-offs per site code: cron expressions on the site's local wall clock (Q-W1, WX14), e.g.
        /// <c>"S1": [ "0 11 * * 1-5", "0 15 * * 1-5" ]</c>. Used when <see cref="Mode"/> is <c>Waves</c>.
        /// </summary>
        public Dictionary<string, List<string>> Waves { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Orders one wave releases at most (1–100 000). Default 2 000.</summary>
        public int MaxOrdersPerWave { get; set; } = 2000;
    }

    public sealed class PickingOptions
    {
        /// <summary>The strategy a release uses (<c>Discrete</c>, <c>Batch</c>, <c>Cluster</c>, <c>Zone</c>, or a registered custom one).</summary>
        public string Strategy { get; set; } = WarehouseStrategyNames.Discrete;

        /// <summary>The router (<c>WalkSequence</c> or a registered custom one).</summary>
        public string Router { get; set; } = WarehouseStrategyNames.WalkSequence;

        /// <summary>What the picker scans (WD4, WD5).</summary>
        public PickConfirmMode Confirm { get; set; } = PickConfirmMode.ScanLocationAndItem;

        /// <summary>Check the scanned lot against the allocated lot (WD4). Default on.</summary>
        public bool LotCheck { get; set; } = true;

        /// <summary>
        /// Q-W6: allow swapping the allocation to the scanned lot when it is in the same location, released, unallocated and
        /// meets the item's minimum shelf life. Default off (refuse).
        /// </summary>
        public bool LotSubstitution { get; set; }

        /// <summary>Q-W7: batches may mix owners. Default true.</summary>
        public bool MixOwners { get; set; } = true;

        /// <summary>Orders per batch trip (Batch, Zone), 1–200. Default 12.</summary>
        public int MaxOrdersPerTrip { get; set; } = 12;

        /// <summary>Totes on a cluster cart (one per order), 1–48. Default 8.</summary>
        public int CartTotes { get; set; } = 8;
    }

    public sealed class ShortPickOptions
    {
        public ShortPickMode Mode { get; set; } = ShortPickMode.ReallocateThenBackorder;

        /// <summary>Create a count task for the location of every short pick.</summary>
        public bool TriggerCycleCount { get; set; } = true;

        /// <summary>The reason code a short pick books its zeroing adjustment with, when the picker gives none.</summary>
        public string DefaultReasonCode { get; set; } = "SHORT";
    }

    public sealed class CancellationOptions
    {
        /// <summary>Q-W2: a cancellation that arrives after picking. Default <c>AcceptWithPutBack</c>.</summary>
        public AfterPickCancellation AfterPick { get; set; } = AfterPickCancellation.AcceptWithPutBack;
    }
}

/// <summary><c>Spark:Warehouse:Release:Mode</c> (PRD §4.13).</summary>
public enum ReleaseMode
{
    /// <summary>Released right after acceptance.</summary>
    Immediate,
    /// <summary>Released by waves at the site's cut-offs.</summary>
    Waves,
    /// <summary>Released by a supervisor.</summary>
    Manual,
}

/// <summary><c>Spark:Warehouse:Picking:Confirm</c> (PRD §4.13).</summary>
public enum PickConfirmMode
{
    ScanLocationAndItem,
    ScanItem,
    Manual,
}

/// <summary><c>Spark:Warehouse:ShortPick</c> (PRD §4.13).</summary>
public enum ShortPickMode
{
    /// <summary>Re-allocate from other locations, then backorder what remains under the shop's policy.</summary>
    ReallocateThenBackorder,
    /// <summary>Backorder under the shop's policy without re-allocating.</summary>
    Backorder,
    /// <summary>Cancel the remainder, whatever the shop's policy (the shop refunds it, D45).</summary>
    CancelRemainder,
    /// <summary>Hold the order for a supervisor.</summary>
    HoldOrder,
}

/// <summary><c>Spark:Warehouse:Cancellation:AfterPick</c> (Q-W2).</summary>
public enum AfterPickCancellation
{
    AcceptWithPutBack,
    Refuse,
}

/// <summary>Startup validation of <see cref="SparkWarehouseOutboundOptions"/> (PRD §4.13 "Startup refuses").</summary>
internal sealed class SparkWarehouseOutboundOptionsValidator(IEnumerable<IPickStrategy> strategies, IEnumerable<IPickRouter> routers)
    : IValidateOptions<SparkWarehouseOutboundOptions>
{
    public ValidateOptionsResult Validate(string? name, SparkWarehouseOutboundOptions options)
    {
        var problems = Problems(options, strategies.Select(s => s.Name), routers.Select(r => r.Name)).ToList();
        return problems.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(problems);
    }

    internal static IEnumerable<string> Problems(SparkWarehouseOutboundOptions options, IEnumerable<string> strategyNames, IEnumerable<string> routerNames)
    {
        var strategies = strategyNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var routers = routerNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!strategies.Contains(options.Picking.Strategy))
            yield return $"Spark:Warehouse:Picking:Strategy '{options.Picking.Strategy}' is not a registered pick strategy ({string.Join(", ", strategies.Order())}).";
        if (!routers.Contains(options.Picking.Router))
            yield return $"Spark:Warehouse:Picking:Router '{options.Picking.Router}' is not a registered pick router ({string.Join(", ", routers.Order())}).";
        if (options.Picking.MaxOrdersPerTrip is < 1 or > 200)
            yield return $"Spark:Warehouse:Picking:MaxOrdersPerTrip must be 1–200 (is {options.Picking.MaxOrdersPerTrip}).";
        if (options.Picking.CartTotes is < 1 or > 48)
            yield return $"Spark:Warehouse:Picking:CartTotes must be 1–48 (is {options.Picking.CartTotes}).";
        if (options.Release.MaxOrdersPerWave is < 1 or > 100_000)
            yield return $"Spark:Warehouse:Release:MaxOrdersPerWave must be 1–100000 (is {options.Release.MaxOrdersPerWave}).";
        if (options.Release.Mode == ReleaseMode.Waves && options.Release.Waves.Count == 0)
            yield return "Spark:Warehouse:Release:Mode is 'Waves' but Spark:Warehouse:Release:Waves names no site's cut-offs.";
        foreach (var (site, cutOffs) in options.Release.Waves)
        {
            foreach (var cron in cutOffs)
            {
                if (WaveSchedule.Parse(cron) is null)
                    yield return $"Spark:Warehouse:Release:Waves:{site} has an invalid cron expression '{cron}'.";
            }
        }
        foreach (var rule in options.Routing.Rules)
        {
            if (string.IsNullOrWhiteSpace(rule.Site))
                yield return "A Spark:Warehouse:Routing:Rules entry has no Site.";
        }
        if (string.IsNullOrWhiteSpace(options.ShortPick.DefaultReasonCode))
            yield return "Spark:Warehouse:ShortPick:DefaultReasonCode is empty.";
    }
}

/// <summary>Wave cut-offs: a cron expression evaluated on a site's local wall clock (Q-W1), through the core's zone facility (K10).</summary>
internal static class WaveSchedule
{
    public static NCrontab.CrontabSchedule? Parse(string? cron)
    {
        if (string.IsNullOrWhiteSpace(cron))
            return null;
        var seconds = cron.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length == 6;
        return NCrontab.CrontabSchedule.TryParse(cron, new NCrontab.CrontabSchedule.ParseOptions { IncludingSeconds = seconds });
    }

    /// <summary>The first cut-off strictly after <paramref name="after"/>, on <paramref name="zone"/>'s wall clock (DST gaps and folds per WX14).</summary>
    public static DateTimeOffset? Next(NCrontab.CrontabSchedule schedule, TimeZoneInfo zone, DateTimeOffset after)
        => SparkTimeZones.NextWallClockOccurrence(after, zone, wallClock =>
        {
            var next = schedule.GetNextOccurrence(wallClock);
            return next >= DateTime.MaxValue.AddDays(-2) ? null : next;
        });
}
