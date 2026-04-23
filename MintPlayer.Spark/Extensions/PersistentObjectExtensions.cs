using MintPlayer.Spark.Abstractions;
using System.Reflection;

namespace MintPlayer.Spark.Extensions;

/// <summary>
/// Extension methods for the <see cref="PersistentObject"/> → entity direction
/// (populating a CLR entity from a PO's attribute values).
/// </summary>
/// <remarks>
/// The forward direction (entity → PO) is owned by <c>IEntityMapper</c>, which is
/// schema-aware (metadata from <c>EntityTypeDefinition</c>, enum/Color/AsDetail
/// conversions, Reference breadcrumb resolution). The previous extension-based
/// forward mappers (<c>ToPersistentObject&lt;T&gt;</c>, <c>PopulateAttributeValues&lt;T&gt;</c>)
/// were removed as part of the PersistentObject factory refactor — they bypassed
/// the schema and produced POs with only a handful of metadata fields set,
/// diverging from what <c>EntityMapper</c> produces. See
/// <c>docs/PRD-PersistentObjectFactory.md</c>.
/// </remarks>
public static class PersistentObjectExtensions
{
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
}
