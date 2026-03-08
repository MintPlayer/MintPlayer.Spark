using System.Text.Json;
using Microsoft.Extensions.Hosting;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.Services;

public interface IModelLoader
{
    IEnumerable<EntityTypeDefinition> GetEntityTypes();
    EntityTypeDefinition? GetEntityType(Guid id);
    EntityTypeDefinition? GetEntityTypeByClrType(string clrType);
    EntityTypeDefinition? GetEntityTypeByName(string name);
    EntityTypeDefinition? GetEntityTypeByAlias(string alias);
    EntityTypeDefinition? ResolveEntityType(string idOrAlias);
    IEnumerable<SparkQuery> GetQueries();
}

[Register(typeof(IModelLoader), ServiceLifetime.Singleton)]
internal partial class ModelLoader : IModelLoader
{
    [Inject] private readonly IHostEnvironment hostEnvironment;

    private Lazy<(Dictionary<Guid, EntityTypeDefinition> ById, Dictionary<string, EntityTypeDefinition> ByAlias, List<SparkQuery> Queries)>? _data;

    private (Dictionary<Guid, EntityTypeDefinition> ById, Dictionary<string, EntityTypeDefinition> ByAlias, List<SparkQuery> Queries) Data
    {
        get
        {
            _data ??= new Lazy<(Dictionary<Guid, EntityTypeDefinition>, Dictionary<string, EntityTypeDefinition>, List<SparkQuery>)>(LoadData);
            return _data.Value;
        }
    }

    private (Dictionary<Guid, EntityTypeDefinition>, Dictionary<string, EntityTypeDefinition>, List<SparkQuery>) LoadData()
    {
        var byId = new Dictionary<Guid, EntityTypeDefinition>();
        var byAlias = new Dictionary<string, EntityTypeDefinition>(StringComparer.OrdinalIgnoreCase);
        var allQueries = new List<SparkQuery>();
        var modelPath = Path.Combine(hostEnvironment.ContentRootPath, "App_Data", "Model");

        if (!Directory.Exists(modelPath))
            return (byId, byAlias, allQueries);

        var jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        foreach (var file in Directory.GetFiles(modelPath, "*.json"))
        {
            try
            {
                var json = File.ReadAllText(file);
                var entityTypeFile = JsonSerializer.Deserialize<EntityTypeFile>(json, jsonOptions);
                if (entityTypeFile?.PersistentObject != null)
                {
                    var entityType = entityTypeFile.PersistentObject;

                    // Auto-generate alias from Name if not explicitly set
                    entityType.Alias ??= entityType.Name.ToLowerInvariant();

                    byId[entityType.Id] = entityType;

                    if (byAlias.ContainsKey(entityType.Alias))
                    {
                        Console.WriteLine($"Warning: Duplicate entity type alias '{entityType.Alias}' in {file}. Alias must be unique.");
                    }
                    else
                    {
                        byAlias[entityType.Alias] = entityType;
                    }

                    // Extract queries and auto-populate EntityType
                    foreach (var query in entityTypeFile.Queries)
                    {
                        query.EntityType ??= entityType.Name;
                        allQueries.Add(query);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error loading model file {file}: {ex.Message}");
            }
        }

        return (byId, byAlias, allQueries);
    }

    public IEnumerable<EntityTypeDefinition> GetEntityTypes()
        => Data.ById.Values;

    public EntityTypeDefinition? GetEntityType(Guid id)
        => Data.ById.TryGetValue(id, out var entityType) ? entityType : null;

    public EntityTypeDefinition? GetEntityTypeByClrType(string clrType)
        => Data.ById.Values.FirstOrDefault(e => e.ClrType == clrType);

    public EntityTypeDefinition? GetEntityTypeByName(string name)
        => Data.ById.Values.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));

    public EntityTypeDefinition? GetEntityTypeByAlias(string alias)
        => Data.ByAlias.TryGetValue(alias, out var entityType) ? entityType : null;

    public EntityTypeDefinition? ResolveEntityType(string idOrAlias)
    {
        if (Guid.TryParse(idOrAlias, out var guid))
            return GetEntityType(guid);
        return GetEntityTypeByAlias(idOrAlias);
    }

    public IEnumerable<SparkQuery> GetQueries()
        => Data.Queries;
}
