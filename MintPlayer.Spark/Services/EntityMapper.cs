using System.Reflection;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.Services;

public interface IEntityMapper
{
    object ToEntity(PersistentObject persistentObject);
    PersistentObject ToPersistentObject(object entity, Guid objectTypeId);
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

    public PersistentObject ToPersistentObject(object entity, Guid objectTypeId)
    {
        var entityType = entity.GetType();
        var idProperty = entityType.GetProperty("Id", BindingFlags.Public | BindingFlags.Instance);
        var id = idProperty?.GetValue(entity)?.ToString();

        var attributes = new List<PersistentObjectAttribute>();
        var properties = entityType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.Name != "Id" && p.CanRead);

        foreach (var property in properties)
        {
            attributes.Add(new PersistentObjectAttribute
            {
                Name = property.Name,
                Value = property.GetValue(entity),
                DataType = GetDataType(property.PropertyType),
            });
        }

        return new PersistentObject
        {
            Id = id,
            Name = GetEntityDisplayName(entity, entityType),
            ObjectTypeId = objectTypeId,
            Attributes = attributes.ToArray(),
        };
    }

    private static void SetPropertyValue(PropertyInfo property, object entity, object? value)
    {
        if (value == null)
        {
            if (Nullable.GetUnderlyingType(property.PropertyType) != null || !property.PropertyType.IsValueType)
            {
                property.SetValue(entity, null);
            }
            return;
        }

        var targetType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;

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

    private static string GetDataType(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;

        return underlying switch
        {
            _ when underlying == typeof(string) => "string",
            _ when underlying == typeof(int) || underlying == typeof(long) => "number",
            _ when underlying == typeof(decimal) || underlying == typeof(double) || underlying == typeof(float) => "number",
            _ when underlying == typeof(bool) => "boolean",
            _ when underlying == typeof(DateTime) => "datetime",
            _ when underlying == typeof(Guid) => "guid",
            _ => "string"
        };
    }

    private static string GetEntityDisplayName(object entity, Type entityType)
    {
        // Try common display name properties
        var nameProperty = entityType.GetProperty("Name")
            ?? entityType.GetProperty("FullName")
            ?? entityType.GetProperty("Title");

        return nameProperty?.GetValue(entity)?.ToString() ?? entityType.Name;
    }

    private static Type? ResolveType(string clrType)
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
