using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Commerce.Ticketing.Venues.Models;
using MintPlayer.Spark.Commerce.Ticketing.Venues.Plans;

namespace MintPlayer.Spark.Commerce.Ticketing.Venues;

/// <summary><c>Spark:Commerce:Ticketing:Venues</c> (PRD §4.11, D8). Code given to <c>AddTicketingVenues(o => …)</c> runs after the section.</summary>
public sealed class SparkTicketingVenuesOptions : ISparkConfigurationSection
{
    public static string SectionName => "Spark:Commerce:Ticketing:Venues";

    /// <summary>
    /// Whether creating a venue, and publishing a layout for the first time, waits for a platform approver (D8:
    /// "creations need it when configured"). Modifications always do. Default false.
    /// </summary>
    public bool ApproveCreations { get; set; }

    /// <summary>Most numbered seats one layout may hold. Default 100,000 (S7 renders 50k).</summary>
    public int MaxSeats { get; set; } = 100_000;

    /// <summary>Most blocks one layout may hold. Default 2,000.</summary>
    public int MaxBlocks { get; set; } = 2_000;

    /// <summary>Largest background image in bytes (PNG, JPEG or WebP). Default 5 MB.</summary>
    public int MaxBackgroundBytes { get; set; } = 5 * 1024 * 1024;

    /// <summary>
    /// The seat tags best available never assigns (default 3): accessible and companion seats are sold only to the
    /// ticket types that allow them. Default <see cref="SeatTags.Restricting"/>.
    /// </summary>
    public int ExcludedFromChains { get; set; } = SeatTags.Restricting;

    /// <summary>Shards of a standing or unnumbered block's quota at most (one per 250 places, at least one). Default 16.</summary>
    public int ShardsPerBlockQuota { get; set; } = 16;

    /// <summary>
    /// How long after an inventory commit a live snapshot's seat states are pushed. Commits on the same snapshot within
    /// it share one push, so a hot sale computes at most one frame per snapshot per delay. Default 250 ms.
    /// </summary>
    public TimeSpan PushDelay { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <summary>How long a computed state frame is reused by <c>/seatmap/state</c> and the list alternative. Default 2 s.</summary>
    public TimeSpan StateCacheDuration { get; set; } = TimeSpan.FromSeconds(2);

    internal LayoutPlanLimits Limits => new(MaxSeats, MaxBlocks);
}
