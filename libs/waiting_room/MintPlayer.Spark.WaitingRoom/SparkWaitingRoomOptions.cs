using MintPlayer.Spark.Abstractions.Builder;

namespace MintPlayer.Spark.WaitingRoom;

/// <summary><c>Spark:WaitingRoom</c> (commerce PRD §4.12). Code given to <c>AddWaitingRoom(o =&gt; …)</c> runs after the section.</summary>
public sealed class SparkWaitingRoomOptions : ISparkConfigurationSection
{
    public static string SectionName => "Spark:WaitingRoom";

    /// <summary>
    /// Rooms declared in configuration, by key (<c>WaitingRooms/{key}</c>), upserted at startup:
    /// <c>"Rooms": { "summer-fest": { "Gate": "SalePhases/1", "Mode": "RandomizedPreQueue", "StartsAt": "…", "RequireSignIn": true } }</c>.
    /// </summary>
    public Dictionary<string, WaitingRoomDefinition> Rooms { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The worker's schedule (six fields: seconds). Default every second, so latecomers are ranked about once per second.</summary>
    public string WorkerSchedule { get; set; } = "* * * * * *";

    /// <summary>
    /// How long a visible tab has to answer its presence check. It answers by itself while visible (no user action), so
    /// this only covers network delay. Default 15 s.
    /// </summary>
    public TimeSpan PresenceTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// How long a hidden tab has to answer: it is notified and needs one action. At least 20 s (WCAG 2.2.1 "Extend": the
    /// user gets at least 20 seconds for a simple action); startup refuses less. Default 60 s.
    /// </summary>
    public TimeSpan HiddenPresenceWindow { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How long the ranker leaves a fresh entry alone, so one committed a moment earlier but not yet indexed cannot be
    /// overtaken. Default 2 s.
    /// </summary>
    public TimeSpan SettleWindow { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>The admission worker's lease per room; another instance takes over a room whose lease lapsed (failover). Default 15 s.</summary>
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>The most entries the worker calls, ranks or writes in one transaction. Default 256.</summary>
    public int BatchSize { get; set; } = 256;

    /// <summary>How long entries are kept after their room ends (or starts, without an end). Default 30 days.</summary>
    public TimeSpan EntryRetention { get; set; } = TimeSpan.FromDays(30);

    /// <summary>How long <see cref="IWaitingRoomAdmissions.IsGatedAsync"/> caches which phases a room gates. Default 5 s.</summary>
    public TimeSpan GateCacheDuration { get; set; } = TimeSpan.FromSeconds(5);
}
