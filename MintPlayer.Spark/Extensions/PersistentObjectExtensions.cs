using System.Reflection;
using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.Extensions;

/// <summary>
/// Extension methods for bidirectional mapping between entities and PersistentObjects.
/// </summary>
public static class PersistentObjectExtensions
{
    /// <summary>
    /// Populates the PersistentObject's attribute values from an entity's properties.
    /// </summary>
    /// <typeparam name="T">The entity type</typeparam>
    /// <param name="persistentObject">The PersistentObject to populate</param>
    /// <param name="entity">The entity to read values from</param>
    public static void PopulateAttributeValues<T>(this PersistentObject persistentObject, T entity) where T : class
    {
        if (entity == null) throw new ArgumentNullException(nameof(entity));

        var entityType = typeof(T);
        var idProperty = entityType.GetProperty("Id", BindingFlags.Public | BindingFlags.Instance);

        // Set the ID if available
        if (idProperty != null)
        {
            persistentObject.Id = idProperty.GetValue(entity)?.ToString();
        }

        // Map entity properties to PersistentObject attributes
        foreach (var attribute in persistentObject.Attributes)
        {
            var property = entityType.GetProperty(attribute.Name, BindingFlags.Public | BindingFlags.Instance);
            if (property != null && property.CanRead)
            {
                attribute.Value = property.GetValue(entity);
            }
        }

        // Set display name
        persistentObject.Name = GetEntityDisplayName(entity, entityType);
        persistentObject.Breadcrumb = persistentObject.Name;
    }

    /// <summary>
    /// Populates an entity's properties from the PersistentObject's attribute values.
    /// </summary>
    /// <typeparam name="T">The entity type</typeparam>
    /// <param name="persistentObject">The PersistentObject to read values from</param>
    /// <param name="entity">The entity to populate</param>
    public static void PopulateObjectValues<T>(this PersistentObject persistentObject, T entity) where T : class
    {
        if (entity == null) throw new ArgumentNullException(nameof(entity));

        var entityType = typeof(T);

        // Set the ID if available
        if (!string.IsNullOrEmpty(persistentObject.Id))
        {
            var idProperty = entityType.GetProperty("Id", BindingFlags.Public | BindingFlags.Instance);
            if (idProperty != null && idProperty.CanWrite)
            {
                SetPropertyValue(idProperty, entity, persistentObject.Id);
            }
        }

        // Map PersistentObject attributes to entity properties
        foreach (var attribute in persistentObject.Attributes)
        {
            var property = entityType.GetProperty(attribute.Name, BindingFlags.Public | BindingFlags.Instance);
            if (property != null && property.CanWrite)
            {
                SetPropertyValue(property, entity, attribute.Value);
            }
        }
    }

    /// <summary>
    /// Creates a new entity instance and populates it from the PersistentObject's attribute values.
    /// </summary>
    /// <typeparam name="T">The entity type</typeparam>
    /// <param name="persistentObject">The PersistentObject to read values from</param>
    /// <returns>A new entity instance with populated values</returns>
    public static T ToEntity<T>(this PersistentObject persistentObject) where T : class, new()
    {
        var entity = new T();
        persistentObject.PopulateObjectValues(entity);
        return entity;
    }

    /// <summary>
    /// Creates a new PersistentObject from an entity.
    /// </summary>
    /// <typeparam name="T">The entity type</typeparam>
    /// <param name="entity">The entity to convert</param>
    /// <param name="objectTypeId">The object type ID for the PersistentObject</param>
    /// <returns>A new PersistentObject with attribute values populated from the entity</returns>
    public static PersistentObject ToPersistentObject<T>(this T entity, Guid objectTypeId) where T : class
    {
        if (entity == null) throw new ArgumentNullException(nameof(entity));

        var entityType = typeof(T);
        var idProperty = entityType.GetProperty("Id", BindingFlags.Public | BindingFlags.Instance);

        var attributes = new List<PersistentObjectAttribute>();
        var properties = entityType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.Name != "Id" && p.CanRead);

        foreach (var property in properties)
        {
            var referenceAttr = property.GetCustomAttribute<ReferenceAttribute>();
            attributes.Add(new PersistentObjectAttribute
            {
                Name = property.Name,
                Value = property.GetValue(entity),
                DataType = referenceAttr != null ? "Reference" : GetDataType(property.PropertyType),
                Query = referenceAttr?.Query
            });
        }

        var displayName = GetEntityDisplayName(entity, entityType);

        return new PersistentObject
        {
            Id = idProperty?.GetValue(entity)?.ToString(),
            Name = displayName,
            Breadcrumb = displayName,
            ObjectTypeId = objectTypeId,
            Attributes = attributes.ToArray()
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

        // Check for array/collection of complex types
        var elementType = GetCollectionElementType(underlying);
        if (elementType != null && IsComplexType(elementType))
        {
            return "AsDetail";
        }

        return underlying switch
        {
            _ when underlying == typeof(string) => "string",
            _ when underlying == typeof(int) || underlying == typeof(long) => "number",
            _ when underlying == typeof(decimal) || underlying == typeof(double) || underlying == typeof(float) => "decimal",
            _ when underlying == typeof(bool) => "boolean",
            _ when underlying == typeof(DateTime) => "datetime",
            _ when underlying == typeof(DateOnly) => "date",
            _ when underlying == typeof(Guid) => "guid",
            _ when IsComplexType(underlying) => "AsDetail",
            _ => "string"
        };
    }

    private static Type? GetCollectionElementType(Type type)
    {
        if (type.IsArray)
            return type.GetElementType();

        if (type.IsGenericType)
        {
            var genericDef = type.GetGenericTypeDefinition();
            if (genericDef == typeof(List<>) ||
                genericDef == typeof(IList<>) ||
                genericDef == typeof(ICollection<>) ||
                genericDef == typeof(IEnumerable<>) ||
                genericDef == typeof(IReadOnlyList<>) ||
                genericDef == typeof(IReadOnlyCollection<>))
            {
                return type.GetGenericArguments()[0];
            }
        }

        foreach (var iface in type.GetInterfaces())
        {
            if (iface.IsGenericType && iface.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            {
                var elType = iface.GetGenericArguments()[0];
                if (elType != typeof(char))
                    return elType;
            }
        }

        return null;
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

    private static string GetEntityDisplayName(object entity, Type entityType)
    {
        // Try common display name properties
        var nameProperty = entityType.GetProperty("Name")
            ?? entityType.GetProperty("FullName")
            ?? entityType.GetProperty("Title");

        return nameProperty?.GetValue(entity)?.ToString() ?? entityType.Name;
    }
}
