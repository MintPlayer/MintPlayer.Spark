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
    EntityTypeDefinition? GetEntityTypeByAlias(string alias);
    EntityTypeDefinition? ResolveEntityType(string idOrAlias);
}

[Register(typeof(IModelLoader), ServiceLifetime.Singleton)]
internal partial class ModelLoader : IModelLoader
{
    [Inject] private readonly IHostEnvironment hostEnvironment;

    private Lazy<(Dictionary<Guid, EntityTypeDefinition> ById, Dictionary<string, EntityTypeDefinition> ByAlias)>? _entityTypes;

    private (Dictionary<Guid, EntityTypeDefinition> ById, Dictionary<string, EntityTypeDefinition> ByAlias) EntityTypes
    {
        get
        {
            _entityTypes ??= new Lazy<(Dictionary<Guid, EntityTypeDefinition>, Dictionary<string, EntityTypeDefinition>)>(LoadEntityTypes);
            return _entityTypes.Value;
        }
    }

    private (Dictionary<Guid, EntityTypeDefinition>, Dictionary<string, EntityTypeDefinition>) LoadEntityTypes()
    {
        var byId = new Dictionary<Guid, EntityTypeDefinition>();
        var byAlias = new Dictionary<string, EntityTypeDefinition>(StringComparer.OrdinalIgnoreCase);
        var modelPath = Path.Combine(hostEnvironment.ContentRootPath, "App_Data", "Model");

        if (!Directory.Exists(modelPath))
            return (byId, byAlias);

        var jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        foreach (var file in Directory.GetFiles(modelPath, "*.json"))
        {
            try
            {
                var json = File.ReadAllText(file);
                var entityType = JsonSerializer.Deserialize<EntityTypeDefinition>(json, jsonOptions);
                if (entityType != null)
                {
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
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error loading model file {file}: {ex.Message}");
            }
        }

        return (byId, byAlias);
    }

    public IEnumerable<EntityTypeDefinition> GetEntityTypes()
        => EntityTypes.ById.Values;

    public EntityTypeDefinition? GetEntityType(Guid id)
        => EntityTypes.ById.TryGetValue(id, out var entityType) ? entityType : null;

    public EntityTypeDefinition? GetEntityTypeByClrType(string clrType)
        => EntityTypes.ById.Values.FirstOrDefault(e => e.ClrType == clrType);

    public EntityTypeDefinition? GetEntityTypeByAlias(string alias)
        => EntityTypes.ByAlias.TryGetValue(alias, out var entityType) ? entityType : null;

    public EntityTypeDefinition? ResolveEntityType(string idOrAlias)
    {
        if (Guid.TryParse(idOrAlias, out var guid))
            return GetEntityType(guid);
        return GetEntityTypeByAlias(idOrAlias);
    }
}
