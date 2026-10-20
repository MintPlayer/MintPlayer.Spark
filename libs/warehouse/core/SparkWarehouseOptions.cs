using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions.Builder;

namespace MintPlayer.Spark.Warehouse;

/// <summary>
/// <c>Spark:Warehouse</c> (PRD §4.13). Code given to <c>AddWarehouse(o => …)</c> runs after the section; startup refuses
/// an invalid value (<see cref="SparkWarehouseOptionsValidator"/>). Per-site overrides live on the site document.
/// </summary>
public sealed class SparkWarehouseOptions : ISparkConfigurationSection
{
    public static string SectionName => "Spark:Warehouse";

    public StockOptions Stock { get; set; } = new();

    public AllocationOptions Allocation { get; set; } = new();

    public ReplenishmentOptions Replenishment { get; set; } = new();

    public TaskOptions Tasks { get; set; } = new();

    public LocationOptions Locations { get; set; } = new();

    public sealed class StockOptions
    {
        /// <summary>
        /// Write stock in cluster-wide transactions (a multi-node cluster, PRD §4.5); off: one node with optimistic
        /// concurrency. Default false.
        /// </summary>
        public bool ClusterWide { get; set; }

        /// <summary>Sessions a command may take before it gives up on a hot balance (1–50). Default 8.</summary>
        public int MaxAttempts { get; set; } = 8;
    }

    public sealed class AllocationOptions
    {
        /// <summary>The default rule (<c>Fifo</c>, <c>Fefo</c>, <c>ClosestLocation</c> or a registered custom name); per item on <c>WarehouseItem.RemovalRule</c>.</summary>
        public string Rule { get; set; } = WarehouseStrategyNames.Fifo;
    }

    public sealed class ReplenishmentOptions
    {
        public ReplenishmentMode Mode { get; set; } = ReplenishmentMode.MinMax;

        /// <summary>The daily sweep (cron). Default 05:00.</summary>
        public string Schedule { get; set; } = "0 5 * * *";
    }

    public sealed class TaskOptions
    {
        /// <summary>How long a claimed task stays the claimer's without activity (1 min – 8 h). Default 15 minutes.</summary>
        public TimeSpan Lease { get; set; } = TimeSpan.FromMinutes(15);
    }

    public sealed class LocationOptions
    {
        /// <summary>Check digits printed on a location label (1–4). Default 2.</summary>
        public int CheckDigitLength { get; set; } = 2;

        /// <summary>An optional secret mixed into location check digits, so they cannot be derived from the code alone.</summary>
        public string? CheckDigitSecret { get; set; }
    }
}

/// <summary><c>Spark:Warehouse:Replenishment:Mode</c> (PRD §4.13).</summary>
public enum ReplenishmentMode
{
    /// <summary>Daily sweep plus every short pick; fixed pick faces only.</summary>
    MinMax,
    Off,
}

/// <summary>Startup validation of <see cref="SparkWarehouseOptions"/> (PRD §4.13 "Startup refuses").</summary>
internal sealed class SparkWarehouseOptionsValidator(IEnumerable<IAllocationRule> rules) : IValidateOptions<SparkWarehouseOptions>
{
    public ValidateOptionsResult Validate(string? name, SparkWarehouseOptions options)
    {
        var problems = Problems(options, rules.Select(r => r.Name)).ToList();
        return problems.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(problems);
    }

    /// <summary>Every problem, worded for the person editing appsettings.json.</summary>
    internal static IEnumerable<string> Problems(SparkWarehouseOptions options, IEnumerable<string> ruleNames)
    {
        var known = ruleNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!known.Contains(options.Allocation.Rule))
            yield return $"Spark:Warehouse:Allocation:Rule '{options.Allocation.Rule}' is not a registered allocation rule ({string.Join(", ", known.Order())}).";
        if (options.Stock.MaxAttempts is < 1 or > 50)
            yield return $"Spark:Warehouse:Stock:MaxAttempts must be 1–50 (is {options.Stock.MaxAttempts}).";
        if (options.Tasks.Lease < TimeSpan.FromMinutes(1) || options.Tasks.Lease > TimeSpan.FromHours(8))
            yield return $"Spark:Warehouse:Tasks:Lease must be between 1 minute and 8 hours (is {options.Tasks.Lease}).";
        if (options.Locations.CheckDigitLength is < 1 or > 4)
            yield return $"Spark:Warehouse:Locations:CheckDigitLength must be 1–4 (is {options.Locations.CheckDigitLength}).";
        if (options.Replenishment.Mode == ReplenishmentMode.MinMax && string.IsNullOrWhiteSpace(options.Replenishment.Schedule))
            yield return "Spark:Warehouse:Replenishment:Schedule is empty; set a cron expression or Mode 'Off'.";
    }
}
