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

    private (Dictionary<Guid, SparkQuery>, Dictionary<string, SparkQuery>) LoadQueries()
    {
        var queries = modelLoader.GetQueries().ToList();

        // Alias derivation and the one-query-per-URL rule both live in SparkQueryAliases, shared
        // with the --spark-verify-model gate so CI cannot accept a model the runtime refuses.
        var byAlias = SparkQueryAliases.Index(queries);

        var byId = new Dictionary<Guid, SparkQuery>();
        foreach (var query in queries)
            byId[query.Id] = query;

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
