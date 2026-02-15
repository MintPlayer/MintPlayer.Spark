using System.Text.Json;
using Microsoft.Extensions.Hosting;
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
    [Inject] private readonly IHostEnvironment hostEnvironment;

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
        var queriesPath = Path.Combine(hostEnvironment.ContentRootPath, "App_Data", "Queries");

        if (!Directory.Exists(queriesPath))
            return (byId, byAlias);

        var jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        foreach (var file in Directory.GetFiles(queriesPath, "*.json"))
        {
            try
            {
                var json = File.ReadAllText(file);
                var query = JsonSerializer.Deserialize<SparkQuery>(json, jsonOptions);
                if (query != null)
                {
                    // Auto-generate alias from Name if not explicitly set
                    query.Alias ??= GenerateQueryAlias(query.Name);

                    byId[query.Id] = query;

                    if (byAlias.ContainsKey(query.Alias))
                    {
                        Console.WriteLine($"Warning: Duplicate query alias '{query.Alias}' in {file}. Alias must be unique.");
                    }
                    else
                    {
                        byAlias[query.Alias] = query;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error loading query file {file}: {ex.Message}");
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
