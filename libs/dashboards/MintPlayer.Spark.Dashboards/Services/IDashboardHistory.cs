namespace MintPlayer.Spark.Dashboards.Services;

/// <summary>
/// Where the sampled history of the dashboards lives (#536 D45): RavenDB's time series (<see cref="DashboardStorage"/>),
/// or the rows of <see cref="StoreDashboardHistory"/> on a store without them. <c>AddDashboards</c> picks one by
/// <c>ISparkStore.ProviderName</c>.
/// </summary>
internal interface IDashboardHistory
{
    /// <summary>Prepares the store before the first sample; returns what it changed.</summary>
    Task<IReadOnlyList<string>> ConfigureAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Appends one point per series at <paramref name="timestamp"/> (a slot start): a second write of the same slot
    /// overwrites the point instead of adding one.
    /// </summary>
    Task AppendAsync(string dashboard, DateTime timestamp, IReadOnlyDictionary<string, double> samples, CancellationToken cancellationToken);

    /// <summary>
    /// The averages of <paramref name="series"/> between <paramref name="from"/> and <paramref name="to"/> in buckets of
    /// <paramref name="bucket"/>, rounded up to whole minutes, oldest first. A bucket without samples has no point. Where
    /// the raw samples have been retired, the rollups answer.
    /// </summary>
    Task<IReadOnlyList<DashboardHistoryPoint>> ReadHistoryAsync(
        string dashboard, string series, DateTime from, DateTime to, TimeSpan bucket, CancellationToken cancellationToken);
}
