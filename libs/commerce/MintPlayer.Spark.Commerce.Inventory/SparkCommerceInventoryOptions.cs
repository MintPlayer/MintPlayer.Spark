using MintPlayer.Spark.Abstractions.Builder;

namespace MintPlayer.Spark.Commerce.Inventory;

/// <summary><c>Spark:Commerce:Inventory</c> (commerce PRD §4.3). Code given to <c>AddCommerceInventory(o => …)</c> runs after the section.</summary>
public sealed class SparkCommerceInventoryOptions : ISparkConfigurationSection
{
    public static string SectionName => "Spark:Commerce:Inventory";

    /// <summary>How long past its expiry a hold stays live (I5). Default 2 s (S3).</summary>
    public TimeSpan Grace { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Attempts per operation on concurrency conflicts. Default 10.</summary>
    public int MaxAttempts { get; set; } = 10;

    /// <summary>The single-empty-seat rule for picked seats (a sale may name its own per pick). Default <see cref="SingleSeatRule.BetweenTakenOrRowEnd"/>.</summary>
    public SingleSeatRule SingleSeatRule { get; set; } = SingleSeatRule.BetweenTakenOrRowEnd;

    /// <summary>
    /// The sweeper's schedule: six fields = seconds (NCrontab, UTC). Default every 5 seconds. It releases lapsed
    /// claims and republishes availability; correctness never depends on it (I3). Read at registration.
    /// </summary>
    public string SweepSchedule { get; set; } = "*/5 * * * * *";

    /// <summary>How many lapsed claims one sweep releases at most. Default 500.</summary>
    public int SweepBatchSize { get; set; } = 500;

    /// <summary>Publish a quota's availability on its live topic after every commit that changes it (X2). Default true.</summary>
    public bool PublishOnCommit { get; set; } = true;

    internal InventoryEngineOptions ToEngineOptions() => new()
    {
        Grace = Grace,
        MaxAttempts = MaxAttempts,
        SingleSeatRule = SingleSeatRule,
    };
}
