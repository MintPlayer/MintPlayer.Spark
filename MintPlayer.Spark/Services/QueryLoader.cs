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
}

[Register(typeof(IQueryLoader), ServiceLifetime.Singleton, "AddSparkServices")]
internal partial class QueryLoader : IQueryLoader
{
    [Inject] private readonly IHostEnvironment hostEnvironment;

    private readonly Lazy<Dictionary<Guid, SparkQuery>> _queries;

    public QueryLoader()
    {
        _queries = new Lazy<Dictionary<Guid, SparkQuery>>(LoadQueries);
    }

    private Dictionary<Guid, SparkQuery> LoadQueries()
    {
        var result = new Dictionary<Guid, SparkQuery>();
        var queriesPath = Path.Combine(hostEnvironment.ContentRootPath, "App_Data", "Queries");

        if (!Directory.Exists(queriesPath))
            return result;

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
                    result[query.Id] = query;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error loading query file {file}: {ex.Message}");
            }
        }

        return result;
    }

    public IEnumerable<SparkQuery> GetQueries()
        => _queries.Value.Values;

    public SparkQuery? GetQuery(Guid id)
        => _queries.Value.TryGetValue(id, out var query) ? query : null;

    public SparkQuery? GetQueryByName(string name)
        => _queries.Value.Values.FirstOrDefault(q => q.Name == name);
}
