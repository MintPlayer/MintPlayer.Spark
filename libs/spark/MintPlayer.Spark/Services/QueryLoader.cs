using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.Services;

public interface IQueryLoader
{
    IEnumerable<SparkQuery> GetQueries();
    SparkQuery? GetQuery(Guid id);
    SparkQuery? GetQueryByName(string name);
    SparkQuery? GetQueryByAlias(string alias);
    SparkQuery? ResolveQuery(string idOrAlias);
}

[Register(typeof(IQueryLoader), ServiceLifetime.Singleton)]
internal partial class QueryLoader : IQueryLoader
{
    [Inject] private readonly IModelLoader modelLoader;

    private Lazy<(Dictionary<Guid, SparkQuery> ById, Dictionary<string, SparkQuery> ByAlias)>? _queries;

    private (Dictionary<Guid, SparkQuery> ById, Dictionary<string, SparkQuery> ByAlias) Queries
    {
        get
        {
            _queries ??= new Lazy<(Dictionary<Guid, SparkQuery>, Dictionary<string, SparkQuery>)>(LoadQueries);
            return _queries.Value;
        }
    }

    private static string GenerateQueryAlias(string name)
    {
        // Strip "Get" prefix and lowercase: "GetCars" -> "cars"
        var alias = name;
        if (alias.StartsWith("Get", StringComparison.OrdinalIgnoreCase) && alias.Length > 3)
            alias = alias[3..];
        return alias.ToLowerInvariant();
    }

    private (Dictionary<Guid, SparkQuery>, Dictionary<string, SparkQuery>) LoadQueries()
    {
        var byId = new Dictionary<Guid, SparkQuery>();
        var byAlias = new Dictionary<string, SparkQuery>(StringComparer.OrdinalIgnoreCase);

        foreach (var query in modelLoader.GetQueries())
        {
            // Auto-generate alias from Name if not explicitly set
            query.Alias ??= GenerateQueryAlias(query.Name);

            byId[query.Id] = query;

            if (byAlias.ContainsKey(query.Alias))
            {
                Console.WriteLine($"Warning: Duplicate query alias '{query.Alias}'. Alias must be unique.");
            }
            else
            {
                byAlias[query.Alias] = query;
            }
        }

        return (byId, byAlias);
    }

    public IEnumerable<SparkQuery> GetQueries()
        => Queries.ById.Values;

    public SparkQuery? GetQuery(Guid id)
        => Queries.ById.TryGetValue(id, out var query) ? query : null;

    public SparkQuery? GetQueryByName(string name)
        => Queries.ById.Values.FirstOrDefault(q => q.Name == name);

    public SparkQuery? GetQueryByAlias(string alias)
        => Queries.ByAlias.TryGetValue(alias, out var query) ? query : null;

    public SparkQuery? ResolveQuery(string idOrAlias)
    {
        if (Guid.TryParse(idOrAlias, out var guid))
            return GetQuery(guid);
        return GetQueryByAlias(idOrAlias);
    }
}
