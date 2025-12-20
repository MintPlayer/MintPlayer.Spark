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
}

[Register(typeof(IModelLoader), ServiceLifetime.Singleton, "AddSparkServices")]
internal partial class ModelLoader : IModelLoader
{
    [Inject] private readonly IHostEnvironment hostEnvironment;

    private Lazy<Dictionary<Guid, EntityTypeDefinition>>? _entityTypes;

    private Dictionary<Guid, EntityTypeDefinition> EntityTypes
    {
        get
        {
            _entityTypes ??= new Lazy<Dictionary<Guid, EntityTypeDefinition>>(LoadEntityTypes);
            return _entityTypes.Value;
        }
    }

    private Dictionary<Guid, EntityTypeDefinition> LoadEntityTypes()
    {
        var result = new Dictionary<Guid, EntityTypeDefinition>();
        var modelPath = Path.Combine(hostEnvironment.ContentRootPath, "App_Data", "Model");

        if (!Directory.Exists(modelPath))
            return result;

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
                    result[entityType.Id] = entityType;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error loading model file {file}: {ex.Message}");
            }
        }

        return result;
    }

    public IEnumerable<EntityTypeDefinition> GetEntityTypes()
        => EntityTypes.Values;

    public EntityTypeDefinition? GetEntityType(Guid id)
        => EntityTypes.TryGetValue(id, out var entityType) ? entityType : null;

    public EntityTypeDefinition? GetEntityTypeByClrType(string clrType)
        => EntityTypes.Values.FirstOrDefault(e => e.ClrType == clrType);
}
