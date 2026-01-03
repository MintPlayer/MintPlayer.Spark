using System.Reflection;
using System.Text.Json;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.Services;

public interface IEntityMapper
{
    object ToEntity(PersistentObject persistentObject);
    PersistentObject ToPersistentObject(object entity, Guid objectTypeId, Dictionary<string, object>? includedDocuments = null);
}

[Register(typeof(IEntityMapper), ServiceLifetime.Scoped, "AddSparkServices")]
internal partial class EntityMapper : IEntityMapper
{
    [Inject] private readonly IModelLoader modelLoader;

    public object ToEntity(PersistentObject persistentObject)
    {
        var entityTypeDefinition = modelLoader.GetEntityType(persistentObject.ObjectTypeId)
            ?? throw new InvalidOperationException($"Could not find EntityType with ID '{persistentObject.ObjectTypeId}'");

        var entityType = ResolveType(entityTypeDefinition.ClrType)
            ?? throw new InvalidOperationException($"Could not resolve type '{entityTypeDefinition.ClrType}'");

        var entity = Activator.CreateInstance(entityType)
            ?? throw new InvalidOperationException($"Could not create instance of type '{entityTypeDefinition.ClrType}'");

        // Set the Id if provided
        var idProperty = entityType.GetProperty("Id", BindingFlags.Public | BindingFlags.Instance);
        if (idProperty != null && !string.IsNullOrEmpty(persistentObject.Id))
        {
            SetPropertyValue(idProperty, entity, persistentObject.Id);
        }

        // Map attributes to entity properties
        foreach (var attribute in persistentObject.Attributes)
        {
            var property = entityType.GetProperty(attribute.Name, BindingFlags.Public | BindingFlags.Instance);
            if (property != null && property.CanWrite)
            {
                SetPropertyValue(property, entity, attribute.Value);
            }
        }

        return entity;
    }

    public PersistentObject ToPersistentObject(object entity, Guid objectTypeId, Dictionary<string, object>? includedDocuments = null)
    {
        var entityType = entity.GetType();
        var idProperty = entityType.GetProperty("Id", BindingFlags.Public | BindingFlags.Instance);
        var id = idProperty?.GetValue(entity)?.ToString();

        // Get the entity type definition for attribute metadata
        var entityTypeDef = modelLoader.GetEntityType(objectTypeId);

        var attributes = new List<PersistentObjectAttribute>();
        var properties = entityType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.Name != "Id" && p.CanRead);

        foreach (var property in properties)
        {
            var value = property.GetValue(entity);
            var referenceAttr = property.GetCustomAttribute<ReferenceAttribute>();
            var attrDef = entityTypeDef?.Attributes.FirstOrDefault(a => a.Name == property.Name);
            var dataType = referenceAttr != null ? "Reference" : GetDataType(property.PropertyType);

            // For complex types (AsDetail), convert to dictionary for proper JSON serialization
            // System.Text.Json doesn't serialize object? properties based on runtime type
            if (dataType == "AsDetail" && value != null)
            {
                value = ConvertToSerializableDictionary(value);
            }

            var attribute = new PersistentObjectAttribute
            {
                Name = property.Name,
                Label = attrDef?.Label,
                Value = value,
                DataType = dataType,
                IsRequired = attrDef?.IsRequired ?? false,
                IsVisible = attrDef?.IsVisible ?? true,
                IsReadOnly = attrDef?.IsReadOnly ?? false,
                Order = attrDef?.Order ?? 0,
                ShowedOn = attrDef?.ShowedOn ?? (EShowedOn.Query | EShowedOn.PersistentObject),
                Rules = attrDef?.Rules ?? [],
            };

            // Handle reference attributes - resolve breadcrumb from included documents
            if (referenceAttr != null && value is string refId && !string.IsNullOrEmpty(refId) && includedDocuments != null)
            {
                if (includedDocuments.TryGetValue(refId, out var referencedEntity) && referencedEntity != null)
                {
                    // Get the referenced entity's type definition for displayFormat resolution
                    var referencedEntityType = referencedEntity.GetType();
                    var referencedEntityTypeDef = modelLoader.GetEntityTypeByClrType(referencedEntityType.FullName ?? referencedEntityType.Name);
                    attribute.Breadcrumb = GetEntityDisplayName(referencedEntity, referencedEntityType, referencedEntityTypeDef);
                }
                attribute.Query = referenceAttr.Query ?? attrDef?.Query;
            }
            else if (attrDef?.DataType == "Reference")
            {
                attribute.Query = attrDef.Query;
            }

            attributes.Add(attribute);
        }

        return new PersistentObject
        {
            Id = id,
            Name = GetEntityDisplayName(entity, entityType, entityTypeDef),
            Breadcrumb = GetEntityDisplayName(entity, entityType, entityTypeDef),
            ObjectTypeId = objectTypeId,
            Attributes = attributes.ToArray(),
        };
    }

    private void SetPropertyValue(PropertyInfo property, object entity, object? value)
    {
        var targetType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;

        // Handle JsonElement - either extract simple value or deserialize complex types
        if (value is JsonElement je)
        {
            if (je.ValueKind == JsonValueKind.Object && IsComplexType(targetType))
            {
                // Deserialize complex objects (like Address) directly from JsonElement
                try
                {
                    var deserializedValue = je.Deserialize(targetType, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    property.SetValue(entity, deserializedValue);
                }
                catch
                {
                    // Skip properties that can't be deserialized
                }
                return;
            }
            else
            {
                value = ExtractJsonElementValue(je);
            }
        }

        if (value == null)
        {
            if (Nullable.GetUnderlyingType(property.PropertyType) != null || !property.PropertyType.IsValueType)
            {
                property.SetValue(entity, null);
            }
            return;
        }

        try
        {
            object? convertedValue;

            if (targetType == typeof(string))
            {
                convertedValue = value.ToString();
            }
            else if (targetType == typeof(Guid))
            {
                convertedValue = value is Guid g ? g : Guid.Parse(value.ToString()!);
            }
            else if (targetType == typeof(DateTime))
            {
                convertedValue = value is DateTime dt ? dt : DateTime.Parse(value.ToString()!);
            }
            else if (targetType == typeof(DateOnly))
            {
                convertedValue = value is DateOnly d ? d : DateOnly.Parse(value.ToString()!);
            }
            else if (targetType.IsEnum)
            {
                convertedValue = Enum.Parse(targetType, value.ToString()!);
            }
            else
            {
                convertedValue = Convert.ChangeType(value, targetType);
            }

            property.SetValue(entity, convertedValue);
        }
        catch
        {
            // Skip properties that can't be converted
        }
    }

    private static object? ExtractJsonElementValue(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number when element.TryGetInt32(out var i) => i,
            JsonValueKind.Number when element.TryGetInt64(out var l) => l,
            JsonValueKind.Number when element.TryGetDecimal(out var d) => d,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            JsonValueKind.Undefined => null,
            _ => element.ToString()
        };
    }

    private string GetDataType(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;

        return underlying switch
        {
            _ when underlying == typeof(string) => "string",
            _ when underlying == typeof(int) || underlying == typeof(long) => "number",
            _ when underlying == typeof(decimal) || underlying == typeof(double) || underlying == typeof(float) => "number",
            _ when underlying == typeof(bool) => "boolean",
            _ when underlying == typeof(DateTime) => "datetime",
            _ when underlying == typeof(DateOnly) => "date",
            _ when underlying == typeof(Guid) => "guid",
            _ when IsComplexType(underlying) => "AsDetail",
            _ => "string"
        };
    }

    private static bool IsComplexType(Type type)
    {
        // A complex type is a class (not string) that has its own properties
        if (type == typeof(string) || type.IsValueType || type.IsEnum || type.IsPrimitive)
            return false;

        // Check if it's a class with public properties
        var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);
        return properties.Length > 0;
    }

    private static Dictionary<string, object?> ConvertToSerializableDictionary(object obj)
    {
        var result = new Dictionary<string, object?>();
        var type = obj.GetType();
        var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);

        foreach (var property in properties)
        {
            var value = property.GetValue(obj);

            // Recursively convert nested complex types
            if (value != null && IsComplexType(property.PropertyType))
            {
                value = ConvertToSerializableDictionary(value);
            }

            result[property.Name] = value;
        }

        return result;
    }

    private string GetEntityDisplayName(object entity, Type entityType, EntityTypeDefinition? entityTypeDef = null)
    {
        // If no entity type definition provided, try to find it
        if (entityTypeDef == null)
        {
            entityTypeDef = modelLoader.GetEntityTypeByClrType(entityType.FullName ?? entityType.Name);
        }

        // 1. Try DisplayFormat (template with {PropertyName} placeholders)
        if (!string.IsNullOrEmpty(entityTypeDef?.DisplayFormat))
        {
            return ResolveDisplayFormat(entity, entityType, entityTypeDef.DisplayFormat);
        }

        // 2. Try DisplayAttribute (single property name)
        if (!string.IsNullOrEmpty(entityTypeDef?.DisplayAttribute))
        {
            var displayProperty = entityType.GetProperty(entityTypeDef.DisplayAttribute, BindingFlags.Public | BindingFlags.Instance);
            if (displayProperty != null)
            {
                var value = displayProperty.GetValue(entity);
                if (value != null)
                {
                    return value.ToString() ?? entityType.Name;
                }
            }
        }

        // 3. Fallback to common display name properties
        var nameProperty = entityType.GetProperty("Name")
            ?? entityType.GetProperty("FullName")
            ?? entityType.GetProperty("Title");

        return nameProperty?.GetValue(entity)?.ToString() ?? entityType.Name;
    }

    private static string ResolveDisplayFormat(object entity, Type entityType, string displayFormat)
    {
        var result = displayFormat;
        var properties = entityType.GetProperties(BindingFlags.Public | BindingFlags.Instance);

        foreach (var property in properties)
        {
            var placeholder = $"{{{property.Name}}}";
            if (result.Contains(placeholder))
            {
                var value = property.GetValue(entity)?.ToString() ?? string.Empty;
                result = result.Replace(placeholder, value);
            }
        }

        return result;
    }

    private Type? ResolveType(string clrType)
    {
        // First try the standard Type.GetType which works for assembly-qualified names
        var type = Type.GetType(clrType);
        if (type != null) return type;

        // Search through all loaded assemblies
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            type = assembly.GetType(clrType);
            if (type != null) return type;
        }

        return null;
    }
}
