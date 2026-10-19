using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Dashboards.Services;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Dashboards;

/// <summary>Every declared dashboard, filled while services are configured and read-only afterwards.</summary>
internal sealed class DashboardCatalog
{
    private readonly Dictionary<string, DashboardDefinition> dashboards = new(StringComparer.Ordinal);

    public IReadOnlyCollection<DashboardDefinition> All => dashboards.Values;

    public DashboardDefinition? Find(string name) => dashboards.GetValueOrDefault(name);

    /// <param name="name">The declared name: the model type's name as its file declares it.</param>
    /// <param name="library">The alias of the library whose model layer declares the type; <see langword="null"/> for the application's own.</param>
    public DashboardDefinition GetOrAdd(string name, string? library = null)
    {
        // The name is the declared model type name, the actions class prefix and the backing document's id suffix:
        // one shape for all three, checked once.
        RqlIdentifier.Collection(name);
        if (!dashboards.TryGetValue(name, out var definition))
            dashboards[name] = definition = new DashboardDefinition(name, library);
        return definition;
    }
}

/// <summary>One dashboard: its providers in the order they were added, its history and its object id.</summary>
/// <remarks>
/// The <see cref="Name"/> is the <b>declared</b> name (D28): the name the model file declares the type under, which is
/// also how its actions class is named. An application may rename a library's dashboard type
/// (<c>[assembly: PersistentObjectName("dashboards", "DatabaseStatistics", "…")]</c>); the declared name, the actions
/// class and the stored history (<see cref="DocumentId"/>) do not change with it, and the model type is found by
/// identity (<see cref="TypeOf"/>), never by the name the application gives it.
/// </remarks>
internal sealed class DashboardDefinition(string name, string? library)
{
    private readonly List<Type> providers = [];

    public string Name { get; } = name;

    /// <summary>The alias of the library that ships the model type; <see langword="null"/> for an application dashboard.</summary>
    public string? Library { get; } = library;

    public string ObjectId { get; set; } = "main";

    public IReadOnlyList<Type> Providers => providers;

    /// <summary>The values sampled into time series, by attribute name.</summary>
    public HashSet<string> History { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// The backing document of the history: <c>SparkDashboards/{declared name}</c>. Keyed by the declared name, which a
    /// rename never changes (D28), so stored series are never orphaned and need no migration.
    /// </summary>
    public string DocumentId => DashboardStorage.DocumentId(Name);

    /// <summary>
    /// The dashboard's model type: a library's by its identity (<c>alias:DeclaredName</c>), the application's own by
    /// its name. <see langword="null"/> when the model has none.
    /// </summary>
    public EntityTypeDefinition? TypeOf(IModelLoader modelLoader)
        => Library is { } alias
            ? modelLoader.GetEntityTypes().FirstOrDefault(t => t.LibraryAlias == alias && t.DeclaredName == Name)
            : modelLoader.GetEntityTypes().FirstOrDefault(t => t.DeclaringAssembly is null && string.Equals(t.Name, Name, StringComparison.Ordinal));

    public void AddProvider(Type type)
    {
        if (!providers.Contains(type))
            providers.Add(type);
    }
}
