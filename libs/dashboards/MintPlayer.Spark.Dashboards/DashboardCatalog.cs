using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Dashboards.Services;

namespace MintPlayer.Spark.Dashboards;

/// <summary>Every declared dashboard, filled while services are configured and read-only afterwards.</summary>
internal sealed class DashboardCatalog
{
    private readonly Dictionary<string, DashboardDefinition> dashboards = new(StringComparer.Ordinal);

    public IReadOnlyCollection<DashboardDefinition> All => dashboards.Values;

    public DashboardDefinition? Find(string name) => dashboards.GetValueOrDefault(name);

    public DashboardDefinition GetOrAdd(string name)
    {
        // The name is the model type name, the actions class prefix and the backing document's id suffix: one
        // shape for all three, checked once.
        RqlIdentifier.Collection(name);
        if (!dashboards.TryGetValue(name, out var definition))
            dashboards[name] = definition = new DashboardDefinition(name);
        return definition;
    }
}

/// <summary>One dashboard: its providers in the order they were added, its history and its object id.</summary>
internal sealed class DashboardDefinition(string name)
{
    private readonly List<Type> providers = [];

    public string Name { get; } = name;

    public string ObjectId { get; set; } = "main";

    public IReadOnlyList<Type> Providers => providers;

    /// <summary>The values sampled into time series, by attribute name.</summary>
    public HashSet<string> History { get; } = new(StringComparer.Ordinal);

    /// <summary>The backing document of the history: <c>SparkDashboards/{name}</c>.</summary>
    public string DocumentId => DashboardStorage.DocumentId(Name);

    public void AddProvider(Type type)
    {
        if (!providers.Contains(type))
            providers.Add(type);
    }
}
