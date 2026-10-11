namespace MintPlayer.Spark.Dashboards;

/// <summary>Options of the dashboards package, bound from <c>Spark:Dashboards</c>, then code.</summary>
public sealed class SparkDashboardsOptions
{
    /// <summary>The configuration section the options are bound from.</summary>
    public const string SectionName = "Spark:Dashboards";

    /// <summary>
    /// How often every dashboard is pushed to its viewers over <c>/spark/live</c>
    /// (<c>ISparkLiveNotifier.Changed</c>). Default 15 s. A viewer without the right gets nothing: the push
    /// is only a dirty bit, re-read under each viewer's own rights.
    /// </summary>
    public TimeSpan RefreshInterval { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// How long one reading of a dashboard is shared by every viewer. Default 5 s, so that concurrent
    /// viewers and the live hub's re-reads cost one provider run between them.
    /// </summary>
    public TimeSpan CacheDuration { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How often a value declared with <see cref="SparkDashboardBuilder.History"/> is sampled into its time
    /// series. Default 60 s (D29). Timestamps are aligned to this slot, and one host in the cluster samples
    /// each slot (a compare-exchange lease).
    /// </summary>
    public TimeSpan SampleInterval { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>How long raw samples are kept (the collection's raw policy). Default 7 days (D29).</summary>
    public TimeSpan RawRetention { get; set; } = TimeSpan.FromDays(7);

    /// <summary>The rollup's aggregation interval. Default 5 minutes (D29).</summary>
    public TimeSpan RollupInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>How long the rollup is kept. Default 90 days (D29).</summary>
    public TimeSpan RollupRetention { get; set; } = TimeSpan.FromDays(90);

    /// <summary>The span a <c>{Value}History</c> attribute shows, ending now. Default 7 days.</summary>
    public TimeSpan HistoryWindow { get; set; } = TimeSpan.FromDays(7);

    /// <summary>About how many points a <c>{Value}History</c> attribute holds (the window's buckets). Default 200 (D29).</summary>
    public int HistoryPoints { get; set; } = 200;

    /// <summary>
    /// Whether this process samples history at all. Default true. Switch it off on a host that must not
    /// write (a read replica); another host's sampler then takes every slot.
    /// </summary>
    public bool Sample { get; set; } = true;
}
