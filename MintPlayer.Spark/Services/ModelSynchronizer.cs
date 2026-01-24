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

[Register(typeof(IModelSynchronizer), ServiceLifetime.Singleton)]
internal partial class ModelSynchronizer : IModelSynchronizer
{
    [Inject] private readonly IHostEnvironment hostEnvironment;
    [Inject] private readonly IIndexRegistry indexRegistry;

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

            // Get projection type from IndexRegistry (populated from FromIndexAttribute on projections)
            var registration = indexRegistry.GetRegistrationForCollectionType(entityType);
            Type? projectionType = registration?.ProjectionType;
            string? indexName = registration?.IndexName;

            // Find or create entity type definition (merging with projection type if present)
            var existingDef = existingEntityTypes.Values.FirstOrDefault(e => e.ClrType == clrType);
            var entityTypeDef = CreateOrUpdateEntityTypeDefinition(entityType, projectionType, indexName, existingDef);

            // Save the entity type definition
            var fileName = Path.Combine(modelPath, $"{entityType.Name}.json");
            var json = JsonSerializer.Serialize(entityTypeDef, JsonOptions);
            File.WriteAllText(fileName, json);
            processedTypes.Add(clrType);

            // Also mark projection type as processed (no separate JSON file)
            if (projectionType != null)
            {
                var projectionClrType = projectionType.FullName ?? projectionType.Name;
                processedTypes.Add(projectionClrType);
                Console.WriteLine($"Synchronized model: {entityType.Name} (merged with {projectionType.Name}) -> {fileName}");
            }
            else
            {
                Console.WriteLine($"Synchronized model: {entityType.Name} -> {fileName}");
            }

            // Collect embedded types from this entity
            CollectEmbeddedTypes(entityType, typesToProcess, processedTypes);

            // Also collect embedded types from projection type (if any)
            if (projectionType != null)
            {
                CollectEmbeddedTypes(projectionType, typesToProcess, processedTypes);
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
            var entityTypeDef = CreateOrUpdateEntityTypeDefinition(embeddedType, projectionType: null, indexName: null, existingDef);

            var fileName = Path.Combine(modelPath, $"{embeddedType.Name}.json");
            var json = JsonSerializer.Serialize(entityTypeDef, JsonOptions);
            File.WriteAllText(fileName, json);
            processedTypes.Add(clrType);

            Console.WriteLine($"Synchronized model (embedded): {embeddedType.Name} -> {fileName}");

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

    private EntityTypeDefinition CreateOrUpdateEntityTypeDefinition(Type entityType, Type? projectionType, string? indexName, EntityTypeDefinition? existing)
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

        // Set QueryType and IndexName if projection type is provided
        if (projectionType != null)
        {
            entityTypeDef.QueryType = projectionType.FullName ?? projectionType.Name;
            entityTypeDef.IndexName = indexName;
        }

        // Get existing attributes as a dictionary for quick lookup
        var existingAttrs = entityTypeDef.Attributes.ToDictionary(a => a.Name, a => a);

        // Get properties from collection type
        var collectionProperties = entityType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.Name != "Id" && p.CanRead && p.CanWrite)
            .ToDictionary(p => p.Name, p => p);

        // Get properties from projection type (if any)
        var projectionProperties = projectionType?.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.Name != "Id" && p.CanRead && p.CanWrite)
            .ToDictionary(p => p.Name, p => p)
            ?? new Dictionary<string, PropertyInfo>();

        // Merge property names from both types
        var allPropertyNames = collectionProperties.Keys
            .Union(projectionProperties.Keys)
            .Distinct()
            .ToList();

        // Build new attributes list, preserving existing IDs and custom settings
        var newAttributes = new List<EntityAttributeDefinition>();
        var order = 1;

        foreach (var propertyName in allPropertyNames)
        {
            var inCollectionType = collectionProperties.TryGetValue(propertyName, out var collectionProp);
            var inQueryType = projectionProperties.TryGetValue(propertyName, out var projectionProp);

            // Use collection property if available, otherwise use projection property
            var property = collectionProp ?? projectionProp!;

            // Validate type compatibility if property exists in both
            if (inCollectionType && inQueryType)
            {
                var collectionDataType = GetDataType(collectionProp!.PropertyType);
                var projectionDataType = GetDataType(projectionProp!.PropertyType);

                if (!AreDataTypesCompatible(collectionDataType, projectionDataType))
                {
                    throw new InvalidOperationException(
                        $"Type mismatch for property '{propertyName}' between collection type '{entityType.Name}' " +
                        $"({collectionProp!.PropertyType.Name} -> {collectionDataType}) and projection type '{projectionType!.Name}' " +
                        $"({projectionProp!.PropertyType.Name} -> {projectionDataType}). Property types must be convertible.");
                }
            }

            var referenceAttr = property.GetCustomAttribute<ReferenceAttribute>();
            var lookupRefAttr = property.GetCustomAttribute<LookupReferenceAttribute>();
            var lookupRefNameAttr = property.GetCustomAttribute<LookupReferenceNameAttribute>();
            var propType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            var dataType = referenceAttr != null ? "Reference" : GetDataType(property.PropertyType);
            string? referenceType = referenceAttr?.TargetType.FullName ?? referenceAttr?.TargetType.Name;
            string? asDetailType = dataType == "AsDetail" ? (propType.FullName ?? propType.Name) : null;
            // Support both type-based and name-based lookup references
            string? lookupReferenceType = lookupRefAttr?.LookupType.Name ?? lookupRefNameAttr?.Name;

            // Determine ShowedOn based on inQueryType/inCollectionType
            // If property doesn't exist in projection type (inQueryType=false), only show on PersistentObject pages
            // If property doesn't exist in collection type (inCollectionType=false), only show on Query pages
            EShowedOn showedOn = EShowedOn.Query | EShowedOn.PersistentObject;
            if (projectionType != null)
            {
                if (!inQueryType && inCollectionType)
                {
                    // Property only in collection type - show only on detail/edit pages
                    showedOn = EShowedOn.PersistentObject;
                }
                else if (inQueryType && !inCollectionType)
                {
                    // Property only in projection type - show only on query/list pages
                    showedOn = EShowedOn.Query;
                }
            }

            if (existingAttrs.TryGetValue(propertyName, out var existingAttr))
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

                if (dataType == "AsDetail")
                {
                    existingAttr.AsDetailType = asDetailType;
                }

                if (lookupRefAttr != null)
                {
                    existingAttr.LookupReferenceType = lookupReferenceType;
                }

                // Set InCollectionType/InQueryType flags only when projection type exists
                if (projectionType != null)
                {
                    existingAttr.InCollectionType = inCollectionType ? null : false;
                    existingAttr.InQueryType = inQueryType ? null : false;
                    // Update ShowedOn based on type availability
                    existingAttr.ShowedOn = showedOn;
                }
                else
                {
                    existingAttr.InCollectionType = null;
                    existingAttr.InQueryType = null;
                }

                newAttributes.Add(existingAttr);
            }
            else
            {
                // Create new attribute
                var newAttr = new EntityAttributeDefinition
                {
                    Id = Guid.NewGuid(),
                    Name = propertyName,
                    Label = AddSpacesToCamelCase(propertyName),
                    DataType = dataType,
                    IsRequired = !IsNullable(property.PropertyType) && property.PropertyType != typeof(string),
                    IsVisible = true,
                    IsReadOnly = false,
                    Order = order,
                    Query = referenceAttr?.Query,
                    ReferenceType = referenceType,
                    AsDetailType = asDetailType,
                    LookupReferenceType = lookupReferenceType,
                    // Set InCollectionType/InQueryType flags only when projection type exists
                    InCollectionType = projectionType != null ? (inCollectionType ? null : false) : null,
                    InQueryType = projectionType != null ? (inQueryType ? null : false) : null,
                    ShowedOn = showedOn,
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

    private bool AreDataTypesCompatible(string type1, string type2)
    {
        // Same types are always compatible
        if (type1 == type2) return true;

        // Number and decimal are compatible (both are numeric)
        var numericTypes = new HashSet<string> { "number", "decimal" };
        if (numericTypes.Contains(type1) && numericTypes.Contains(type2)) return true;

        return false;
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
            _ when underlying == typeof(DateOnly) => "date",
            _ when underlying == typeof(Guid) => "guid",
            _ when IsComplexType(underlying) => "AsDetail",
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
