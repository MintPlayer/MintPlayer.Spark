using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using System.Drawing;
using System.Reflection;
using System.Text.Json;

namespace MintPlayer.Spark.Services;

public interface IEntityMapper
{
    object ToEntity(PersistentObject persistentObject);
    T ToEntity<T>(PersistentObject persistentObject) where T : class;

    /// <summary>
    /// Scaffolds a blank PersistentObject from the schema named <paramref name="name"/>:
    /// every declared attribute is created with full metadata (DataType, Label, Rules,
    /// Renderer, ShowedOn, Order, Group, IsRequired/Visible/ReadOnly/Array, Query),
    /// Value = null, Parent set. Throws <see cref="KeyNotFoundException"/> on unknown
    /// or ambiguous name.
    /// </summary>
    PersistentObject NewPersistentObject(string name);

    /// <summary>
    /// Scaffolds a blank PersistentObject by ObjectTypeId. Preferred over the name
    /// overload for apps that declare entities across multiple database schemas,
    /// since IDs are unambiguous by construction.
    /// </summary>
    PersistentObject NewPersistentObject(Guid id);

    /// <summary>
    /// Scaffolds a blank PersistentObject for <typeparamref name="T"/>, resolving the
    /// ObjectTypeId via <see cref="IModelLoader.GetEntityTypeByClrType"/>. Throws
    /// <see cref="KeyNotFoundException"/> when no entity type is registered under
    /// <c>typeof(T).FullName</c>.
    /// </summary>
    PersistentObject NewPersistentObject<T>() where T : class;

    /// <summary>
    /// Fills <paramref name="po"/> with values reflected from <paramref name="entity"/>:
    /// Id / Name / Breadcrumb on the PO itself, and Value on every attribute that has
    /// a matching public readable property. Applies enum→string, <see cref="System.Drawing.Color"/>→
    /// <c>#RRGGBB</c> hex, and AsDetail→dictionary conversions. When
    /// <paramref name="includedDocuments"/> is supplied, Reference attributes gain a
    /// Breadcrumb resolved from the target document. Attributes whose name has no
    /// matching property on the entity are left with Value = null (Vidyano parity);
    /// attributes whose name contains '.' are skipped.
    /// </summary>
    void PopulateAttributeValues(PersistentObject po, object entity, Dictionary<string, object>? includedDocuments = null);

    /// <summary>
    /// Typed convenience overload of
    /// <see cref="PopulateAttributeValues(PersistentObject, object, Dictionary{string, object}?)"/>.
    /// </summary>
    void PopulateAttributeValues<T>(PersistentObject po, T entity, Dictionary<string, object>? includedDocuments = null) where T : class;

    /// <summary>
    /// Convenience wrapper: <see cref="NewPersistentObject(Guid)"/> + <see cref="PopulateAttributeValues"/>.
    /// Existing call sites (DatabaseAccess, QueryExecutor, StreamingQueryExecutor) keep this signature.
    /// </summary>
    PersistentObject ToPersistentObject(object entity, Guid objectTypeId, Dictionary<string, object>? includedDocuments = null);

    /// <summary>
    /// Typed convenience overload that derives the ObjectTypeId from
    /// <c>typeof(T).FullName</c> via <see cref="IModelLoader.GetEntityTypeByClrType"/>.
    /// Throws <see cref="KeyNotFoundException"/> when no entity type is registered
    /// for the CLR type. Callers in framework internals that already have a Guid
    /// (DatabaseAccess, QueryExecutor) continue to use the non-generic overload.
    /// </summary>
    PersistentObject ToPersistentObject<T>(T entity, Dictionary<string, object>? includedDocuments = null) where T : class;
}

[Register(typeof(IEntityMapper), ServiceLifetime.Scoped)]
internal partial class EntityMapper : IEntityMapper
{
    [Inject] private readonly IModelLoader modelLoader;

    public T ToEntity<T>(PersistentObject persistentObject) where T : class
        => (T)ToEntity(persistentObject);

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

    public PersistentObject NewPersistentObject(string name)
    {
        var def = modelLoader.GetEntityTypeByName(name)
            ?? throw new KeyNotFoundException($"No entity type with Name '{name}' is registered.");
        return ScaffoldFrom(def);
    }

    public PersistentObject NewPersistentObject(Guid id)
    {
        var def = modelLoader.GetEntityType(id)
            ?? throw new KeyNotFoundException($"No entity type with ObjectTypeId '{id}' is registered.");
        return ScaffoldFrom(def);
    }

    public PersistentObject NewPersistentObject<T>() where T : class
        => ScaffoldFrom(ResolveDefByClrType(typeof(T)));

    public PersistentObject ToPersistentObject<T>(T entity, Dictionary<string, object>? includedDocuments = null) where T : class
        => ToPersistentObject(entity, ResolveDefByClrType(typeof(T)).Id, includedDocuments);

    public void PopulateAttributeValues<T>(PersistentObject po, T entity, Dictionary<string, object>? includedDocuments = null) where T : class
        => PopulateAttributeValues(po, (object)entity, includedDocuments);

    private EntityTypeDefinition ResolveDefByClrType(Type clrType)
    {
        var name = clrType.FullName ?? clrType.Name;
        return modelLoader.GetEntityTypeByClrType(name)
            ?? throw new KeyNotFoundException($"No entity type registered for CLR type '{name}'.");
    }

    public PersistentObject ToPersistentObject(object entity, Guid objectTypeId, Dictionary<string, object>? includedDocuments = null)
    {
        // `GetEntityType` may legitimately return null for projection / anonymous types
        // that don't have a declared EntityTypeDefinition. In that case we produce an
        // empty PO shell and let PopulateAttributeValues fill Id/Name.
        var def = modelLoader.GetEntityType(objectTypeId);
        var po = def is not null
            ? ScaffoldFrom(def)
            : new PersistentObject
            {
                Id = null,
                Name = entity.GetType().Name,
                ObjectTypeId = objectTypeId,
            };

        PopulateAttributeValues(po, entity, includedDocuments);
        return po;
    }

    public void PopulateAttributeValues(PersistentObject po, object entity, Dictionary<string, object>? includedDocuments = null)
    {
        var entityType = entity.GetType();
        var idProperty = entityType.GetProperty("Id", BindingFlags.Public | BindingFlags.Instance);
        po.Id = idProperty?.GetValue(entity)?.ToString();

        var entityTypeDef = modelLoader.GetEntityType(po.ObjectTypeId);
        var displayName = GetEntityDisplayName(entity, entityType, entityTypeDef);
        po.Name = displayName;
        po.Breadcrumb = displayName;

        foreach (var attribute in po.Attributes)
        {
            // Vidyano parity: dot-notation names (nested paths) are not populated
            // here — reserved for future PersistentObjectAttributeAsDetail support.
            if (attribute.Name.Contains('.'))
                continue;

            var property = entityType.GetProperty(attribute.Name, BindingFlags.Public | BindingFlags.Instance);
            if (property is null || !property.CanRead)
                continue; // silent skip — attribute may be projection-only

            var raw = property.GetValue(entity);
            attribute.Value = ConvertValueForWire(raw, property.PropertyType, attribute);

            // Reference attributes: resolve the display name of the referenced entity
            // from the preloaded includedDocuments dict so the client can render a
            // breadcrumb without a second round-trip.
            if (attribute.DataType == "Reference"
                && attribute.Value is string refId && !string.IsNullOrEmpty(refId)
                && includedDocuments is not null
                && includedDocuments.TryGetValue(refId, out var referencedEntity)
                && referencedEntity is not null)
            {
                var referencedEntityType = referencedEntity.GetType();
                var referencedEntityTypeDef = modelLoader.GetEntityTypeByClrType(
                    referencedEntityType.FullName ?? referencedEntityType.Name);
                attribute.Breadcrumb = GetEntityDisplayName(referencedEntity, referencedEntityType, referencedEntityTypeDef);
            }
        }
    }

    /// <summary>
    /// Builds a scaffold PO (metadata only, values null) from an entity type definition.
    /// Shared by <see cref="NewPersistentObject(string)"/>, <see cref="NewPersistentObject(Guid)"/>,
    /// and <see cref="ToPersistentObject(object, Guid, Dictionary{string, object}?)"/>.
    /// </summary>
    private static PersistentObject ScaffoldFrom(EntityTypeDefinition def)
    {
        var po = new PersistentObject
        {
            Id = null,
            Name = def.Name,
            ObjectTypeId = def.Id,
        };

        if (def.Attributes is not null)
        {
            foreach (var attrDef in def.Attributes)
                po.AddAttribute(FromDefinition(attrDef));
        }

        return po;
    }

    /// <summary>
    /// Pure function: 14-field metadata copy from an <see cref="EntityAttributeDefinition"/>
    /// into a blank <see cref="PersistentObjectAttribute"/>. Single canonical owner of
    /// "which fields travel from schema to wire". Value stays null; populate step fills it.
    /// </summary>
    private static PersistentObjectAttribute FromDefinition(EntityAttributeDefinition def)
        => new()
        {
            Name = def.Name,
            Label = def.Label,
            DataType = def.DataType,
            IsArray = def.IsArray,
            IsRequired = def.IsRequired,
            IsVisible = def.IsVisible,
            IsReadOnly = def.IsReadOnly,
            Order = def.Order,
            ShowedOn = def.ShowedOn,
            Rules = def.Rules ?? [],
            Group = def.Group,
            Renderer = def.Renderer,
            RendererOptions = def.RendererOptions,
            Query = def.DataType == "Reference" ? def.Query : null,
            Value = null,
        };

    /// <summary>
    /// Converts a raw property value to its wire representation:
    /// <list type="bullet">
    ///   <item>enums → string name</item>
    ///   <item><see cref="Color"/> → <c>#RRGGBB</c> (or null if Empty)</item>
    ///   <item>AsDetail single → dictionary</item>
    ///   <item>AsDetail array → list of dictionaries</item>
    ///   <item>everything else → passthrough</item>
    /// </list>
    /// </summary>
    private static object? ConvertValueForWire(object? raw, Type propertyType, PersistentObjectAttribute attribute)
    {
        if (raw is null) return null;

        var underlying = Nullable.GetUnderlyingType(propertyType) ?? propertyType;

        if (underlying.IsEnum)
            return raw.ToString();

        if (underlying == typeof(Color) && raw is Color colorValue)
            return colorValue.IsEmpty ? null : $"#{colorValue.R:x2}{colorValue.G:x2}{colorValue.B:x2}";

        if (attribute.DataType == "AsDetail")
        {
            if (attribute.IsArray && raw is System.Collections.IEnumerable enumerable && raw is not string)
            {
                var list = new List<Dictionary<string, object?>>();
                foreach (var item in enumerable)
                    list.Add(ConvertToSerializableDictionary(item));
                return list;
            }
            if (!attribute.IsArray)
                return ConvertToSerializableDictionary(raw);
        }

        return raw;
    }

    private void SetPropertyValue(PropertyInfo property, object entity, object? value)
    {
        var targetType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;

        // Handle JsonElement - either extract simple value or deserialize complex types
        if (value is JsonElement je)
        {
            if (je.ValueKind == JsonValueKind.Array)
            {
                // Deserialize array/collection of complex objects (e.g., CarreerJob[], List<CarreerJob>)
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
            else if (targetType == typeof(Color))
            {
                convertedValue = ColorTranslator.FromHtml(value.ToString()!);
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
            _ when underlying == typeof(Color) => "color",
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
