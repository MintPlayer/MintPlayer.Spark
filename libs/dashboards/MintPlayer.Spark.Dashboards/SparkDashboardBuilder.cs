using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Dashboards.Providers;

namespace MintPlayer.Spark.Dashboards;

/// <summary>Declares the application's dashboards inside <c>spark.AddDashboards(dashboards => …)</c>.</summary>
public sealed class SparkDashboardsBuilder
{
    private readonly DashboardCatalog catalog;

    internal SparkDashboardsBuilder(DashboardCatalog catalog) => this.catalog = catalog;

    /// <summary>
    /// Declares (or extends) the dashboard whose model type is <paramref name="name"/>: a live virtual type in
    /// <c>App_Data/Model/{name}.json</c> (<c>"live": true</c>, no <c>clrType</c>) served by an actions class
    /// <c>public sealed class {name}Actions(IServiceProvider services) : SparkDashboardActions(services);</c>.
    /// Startup refuses a declaration whose type or actions class is missing.
    /// </summary>
    public SparkDashboardsBuilder Add(string name, Action<SparkDashboardBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        configure(new SparkDashboardBuilder(catalog.GetOrAdd(name)));
        return this;
    }
}

/// <summary>One dashboard's providers, history and object id.</summary>
public sealed class SparkDashboardBuilder
{
    private readonly DashboardDefinition definition;

    internal SparkDashboardBuilder(DashboardDefinition definition) => this.definition = definition;

    /// <summary>The dashboard's model type name.</summary>
    public string Name => definition.Name;

    /// <summary>
    /// The object id the dashboard answers to and is pushed under (default <c>main</c>): its program unit's
    /// <c>objectId</c>. Any other id is 404.
    /// </summary>
    public SparkDashboardBuilder ObjectId(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        definition.ObjectId = id;
        return this;
    }

    /// <summary>Adds a provider, created per reading in its own scope.</summary>
    public SparkDashboardBuilder Provider<TProvider>() where TProvider : class, IDashboardProvider
    {
        definition.AddProvider(typeof(TProvider));
        return this;
    }

    /// <summary>
    /// Adds the built-in RavenDB statistics provider (D13): <c>DatabaseSize</c> (bytes on disk),
    /// <c>DocumentCount</c>, <c>CollectionCount</c> and <c>Collections</c> (a table of <c>name</c>,
    /// <c>documents</c> and <c>sizeBytes</c>, largest first), read with the application's own document store.
    /// </summary>
    public SparkDashboardBuilder DatabaseStatistics() => Provider<RavenStatisticsProvider>();

    /// <summary>
    /// Adds the built-in host metrics provider (<c>docs/spark_dashboards_PRD.md</c> §9.2): <c>HostDisk</c> (a meter of
    /// used of total bytes, from <c>DriveInfo("/")</c>, which inside a container measures the host's disk, S10),
    /// <c>HostDiskFree</c> and <c>HostDiskUsedPercent</c>; <c>Memory</c> (a meter of the container's
    /// <c>memory.current</c> of its limit, or of the host total when there is none) and <c>MemoryUsed</c>; and
    /// <c>Cpu</c> (percent of the available cores since the previous reading). On a development machine without
    /// cgroups the process's own figures stand in.
    /// </summary>
    public SparkDashboardBuilder HostMetrics() => Provider<HostMetricsProvider>();

    /// <summary>
    /// Keeps a history of each named value: sampled every <see cref="SparkDashboardsOptions.SampleInterval"/> into a
    /// single-valued RavenDB time series of that name on <c>SparkDashboards/{dashboard}</c> (D14, D29). A model
    /// attribute named <c>{value}History</c> receives it as points for <c>spark-trend</c>. A name is a plain
    /// identifier (letters, digits, underscores); only a number or a meter's value is ever written.
    /// </summary>
    public SparkDashboardBuilder History(params string[] values)
    {
        foreach (var value in values)
            definition.History.Add(RqlIdentifier.Collection(value));
        return this;
    }
}
