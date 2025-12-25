using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Hosting;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using Raven.Client.Documents.Linq;

namespace MintPlayer.Spark.Services;

public interface IModelSynchronizer
{
    void SynchronizeModels(SparkContext sparkContext);
}

[Register(typeof(IModelSynchronizer), ServiceLifetime.Singleton, "AddSparkServices")]
internal partial class ModelSynchronizer : IModelSynchronizer
{
    [Inject] private readonly IHostEnvironment hostEnvironment;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public void SynchronizeModels(SparkContext sparkContext)
    {
        var contextType = sparkContext.GetType();
        var modelPath = Path.Combine(hostEnvironment.ContentRootPath, "App_Data", "Model");
        var queriesPath = Path.Combine(hostEnvironment.ContentRootPath, "App_Data", "Queries");

        // Ensure directories exist
        Directory.CreateDirectory(modelPath);
        Directory.CreateDirectory(queriesPath);

        // Load existing entity types
        var existingEntityTypes = LoadExistingEntityTypes(modelPath);
        var existingQueries = LoadExistingQueries(queriesPath);

        // Find all IRavenQueryable<T> properties on the SparkContext
        var queryableProperties = contextType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => IsRavenQueryable(p.PropertyType))
            .ToList();

        // Track types to process (including embedded types)
        var processedTypes = new HashSet<string>();
        var typesToProcess = new Queue<Type>();

        foreach (var property in queryableProperties)
        {
            var entityType = GetQueryableEntityType(property.PropertyType);
            if (entityType == null) continue;

            var clrType = entityType.FullName ?? entityType.Name;

            // Find or create entity type definition
            var existingDef = existingEntityTypes.Values.FirstOrDefault(e => e.ClrType == clrType);
            var entityTypeDef = CreateOrUpdateEntityTypeDefinition(entityType, existingDef);

            // Save the entity type definition
            var fileName = Path.Combine(modelPath, $"{entityType.Name}.json");
            var json = JsonSerializer.Serialize(entityTypeDef, JsonOptions);
            File.WriteAllText(fileName, json);
            processedTypes.Add(clrType);

            Console.WriteLine($"Synchronized model: {entityType.Name} -> {fileName}");

            // Collect embedded types from this entity
            CollectEmbeddedTypes(entityType, typesToProcess, processedTypes);

            // Check for [QueryType] attribute and collect projection type
            var queryTypeAttr = entityType.GetCustomAttribute<QueryTypeAttribute>();
            if (queryTypeAttr != null)
            {
                var projectionType = queryTypeAttr.ProjectionType;
                var projectionClrType = projectionType.FullName ?? projectionType.Name;

                if (!processedTypes.Contains(projectionClrType))
                {
                    typesToProcess.Enqueue(projectionType);
                    Console.WriteLine($"Found projection type: {projectionType.Name} (from [QueryType] on {entityType.Name})");
                }
            }

            // Create default query for this entity type if it doesn't exist
            var queryName = $"Get{property.Name}";
            if (!existingQueries.Values.Any(q => q.Name == queryName))
            {
                var query = new SparkQuery
                {
                    Id = Guid.NewGuid(),
                    Name = queryName,
                    ContextProperty = property.Name,
                    SortBy = GetDefaultSortProperty(entityTypeDef),
                    SortDirection = "asc"
                };

                var queryFileName = Path.Combine(queriesPath, $"{queryName}.json");
                var queryJson = JsonSerializer.Serialize(query, JsonOptions);
                File.WriteAllText(queryFileName, queryJson);

                Console.WriteLine($"Created query: {queryName} -> {queryFileName}");
            }
        }

        // Process embedded types
        while (typesToProcess.Count > 0)
        {
            var embeddedType = typesToProcess.Dequeue();
            var clrType = embeddedType.FullName ?? embeddedType.Name;

            if (processedTypes.Contains(clrType))
                continue;

            var existingDef = existingEntityTypes.Values.FirstOrDefault(e => e.ClrType == clrType);
            var entityTypeDef = CreateOrUpdateEntityTypeDefinition(embeddedType, existingDef);

            var fileName = Path.Combine(modelPath, $"{embeddedType.Name}.json");
            var json = JsonSerializer.Serialize(entityTypeDef, JsonOptions);
            File.WriteAllText(fileName, json);
            processedTypes.Add(clrType);

            Console.WriteLine($"Synchronized model (embedded/projection): {embeddedType.Name} -> {fileName}");

            // Recursively collect embedded types from this type
            CollectEmbeddedTypes(embeddedType, typesToProcess, processedTypes);
        }
    }

    private void CollectEmbeddedTypes(Type entityType, Queue<Type> typesToProcess, HashSet<string> processedTypes)
    {
        var properties = entityType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.Name != "Id" && p.CanRead && p.CanWrite);

        foreach (var property in properties)
        {
            var propType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            var clrType = propType.FullName ?? propType.Name;

            if (IsComplexType(propType) && !processedTypes.Contains(clrType))
            {
                typesToProcess.Enqueue(propType);
            }
        }
    }

    private Dictionary<Guid, EntityTypeDefinition> LoadExistingEntityTypes(string modelPath)
    {
        var result = new Dictionary<Guid, EntityTypeDefinition>();

        if (!Directory.Exists(modelPath))
            return result;

        foreach (var file in Directory.GetFiles(modelPath, "*.json"))
        {
            try
            {
                var json = File.ReadAllText(file);
                var entityType = JsonSerializer.Deserialize<EntityTypeDefinition>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
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

    private Dictionary<Guid, SparkQuery> LoadExistingQueries(string queriesPath)
    {
        var result = new Dictionary<Guid, SparkQuery>();

        if (!Directory.Exists(queriesPath))
            return result;

        foreach (var file in Directory.GetFiles(queriesPath, "*.json"))
        {
            try
            {
                var json = File.ReadAllText(file);
                var query = JsonSerializer.Deserialize<SparkQuery>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
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

    private EntityTypeDefinition CreateOrUpdateEntityTypeDefinition(Type entityType, EntityTypeDefinition? existing)
    {
        var entityTypeDef = existing ?? new EntityTypeDefinition
        {
            Id = Guid.NewGuid(),
            Name = entityType.Name,
            ClrType = entityType.FullName ?? entityType.Name,
        };

        // Update basic info
        entityTypeDef.Name = entityType.Name;
        entityTypeDef.ClrType = entityType.FullName ?? entityType.Name;

        // Get existing attributes as a dictionary for quick lookup
        var existingAttrs = entityTypeDef.Attributes.ToDictionary(a => a.Name, a => a);

        // Build new attributes list, preserving existing IDs and custom settings
        var newAttributes = new List<EntityAttributeDefinition>();
        var properties = entityType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.Name != "Id" && p.CanRead && p.CanWrite)
            .ToList();

        var order = 1;
        foreach (var property in properties)
        {
            var referenceAttr = property.GetCustomAttribute<ReferenceAttribute>();

            var propType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            var dataType = referenceAttr != null ? "reference" : GetDataType(property.PropertyType);
            string? referenceType = referenceAttr?.TargetType.FullName ?? referenceAttr?.TargetType.Name;
            string? embeddedType = dataType == "embedded" ? (propType.FullName ?? propType.Name) : null;

            if (existingAttrs.TryGetValue(property.Name, out var existingAttr))
            {
                // Update existing attribute, preserving custom settings
                existingAttr.DataType = dataType;
                existingAttr.Order = existingAttr.Order > 0 ? existingAttr.Order : order;

                if (referenceAttr != null)
                {
                    existingAttr.ReferenceType = referenceType;
                    if (string.IsNullOrEmpty(existingAttr.Query))
                    {
                        existingAttr.Query = referenceAttr.Query;
                    }
                }

                if (dataType == "embedded")
                {
                    existingAttr.EmbeddedType = embeddedType;
                }

                newAttributes.Add(existingAttr);
            }
            else
            {
                // Create new attribute
                var newAttr = new EntityAttributeDefinition
                {
                    Id = Guid.NewGuid(),
                    Name = property.Name,
                    Label = AddSpacesToCamelCase(property.Name),
                    DataType = dataType,
                    IsRequired = !IsNullable(property.PropertyType) && property.PropertyType != typeof(string),
                    IsVisible = true,
                    IsReadOnly = false,
                    Order = order,
                    Query = referenceAttr?.Query,
                    ReferenceType = referenceType,
                    EmbeddedType = embeddedType,
                    Rules = []
                };
                newAttributes.Add(newAttr);
            }
            order++;
        }

        entityTypeDef.Attributes = newAttributes.ToArray();

        // Set display attribute if not set
        if (string.IsNullOrEmpty(entityTypeDef.DisplayAttribute))
        {
            entityTypeDef.DisplayAttribute = newAttributes
                .FirstOrDefault(a => a.Name is "Name" or "FullName" or "Title")?.Name
                ?? newAttributes.FirstOrDefault()?.Name;
        }

        return entityTypeDef;
    }

    private bool IsRavenQueryable(Type type)
    {
        if (!type.IsGenericType) return false;
        var genericDef = type.GetGenericTypeDefinition();
        return genericDef == typeof(IRavenQueryable<>);
    }

    private Type? GetQueryableEntityType(Type queryableType)
    {
        if (!queryableType.IsGenericType) return null;
        return queryableType.GetGenericArguments().FirstOrDefault();
    }

    private string GetDataType(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;

        return underlying switch
        {
            _ when underlying == typeof(string) => "string",
            _ when underlying == typeof(int) || underlying == typeof(long) => "number",
            _ when underlying == typeof(decimal) || underlying == typeof(double) || underlying == typeof(float) => "decimal",
            _ when underlying == typeof(bool) => "boolean",
            _ when underlying == typeof(DateTime) || underlying == typeof(DateTimeOffset) => "datetime",
            _ when underlying == typeof(Guid) => "guid",
            _ when IsComplexType(underlying) => "embedded",
            _ => "string"
        };
    }

    private bool IsComplexType(Type type)
    {
        // A complex type is a class (not string) that has its own properties with an Id property
        if (type == typeof(string) || type.IsValueType || type.IsEnum || type.IsPrimitive)
            return false;

        // Check if it's a class with public properties and has an Id property
        var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);
        return properties.Any(p => p.Name == "Id" && p.CanRead && p.CanWrite);
    }

    private bool IsNullable(Type type)
    {
        return Nullable.GetUnderlyingType(type) != null || !type.IsValueType;
    }

    private string AddSpacesToCamelCase(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;

        var result = new System.Text.StringBuilder();
        result.Append(text[0]);

        for (int i = 1; i < text.Length; i++)
        {
            if (char.IsUpper(text[i]))
            {
                result.Append(' ');
            }
            result.Append(text[i]);
        }

        return result.ToString();
    }

    private string? GetDefaultSortProperty(EntityTypeDefinition entityType)
    {
        // Prefer Name, LastName, or first string attribute
        var sortAttr = entityType.Attributes
            .FirstOrDefault(a => a.Name is "Name" or "LastName")
            ?? entityType.Attributes.FirstOrDefault(a => a.DataType == "string");

        return sortAttr?.Name;
    }
}
