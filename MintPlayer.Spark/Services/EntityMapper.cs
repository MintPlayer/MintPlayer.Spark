using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using Raven.Client.Documents.Session;
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
    PersistentObject GetPersistentObject(string name);

    /// <summary>
    /// Scaffolds a blank PersistentObject by ObjectTypeId. Preferred over the name
    /// overload for apps that declare entities across multiple database schemas,
    /// since IDs are unambiguous by construction.
    /// </summary>
    PersistentObject GetPersistentObject(Guid id);

    /// <summary>
    /// Scaffolds a blank PersistentObject for <typeparamref name="T"/>, resolving the
    /// ObjectTypeId via <see cref="IModelLoader.GetEntityTypeByClrType"/>. Throws
    /// <see cref="KeyNotFoundException"/> when no entity type is registered under
    /// <c>typeof(T).FullName</c>.
    /// </summary>
    PersistentObject GetPersistentObject<T>() where T : class;

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
    /// Convenience wrapper: <see cref="GetPersistentObject(Guid)"/> + <see cref="PopulateAttributeValues"/>.
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

    /// <summary>
    /// Populates <paramref name="entity"/> in-place from <paramref name="po"/>'s attribute
    /// values. Entity properties whose name is absent from the PO's attributes are left
    /// untouched — which is what enables PATCH-style updates (load-existing then merge
    /// incoming values; fields not on the PO survive).
    /// <list type="bullet">
    ///   <item>Dot-notation attribute names are skipped (reserved for nested AsDetail).</item>
    ///   <item><see cref="TranslatedString"/> properties merge per-language — languages on
    ///   the existing entity but absent from the incoming dict survive.</item>
    ///   <item>Reference attributes whose CLR property targets a complex entity type are
    ///   resolved via <paramref name="session"/>.LoadAsync. String-typed reference properties
    ///   get the refId written through directly (no resolution required).</item>
    ///   <item>Guid / DateTime / DateOnly / Color / enum coercion via the canonical path.</item>
    /// </list>
    /// </summary>
    Task PopulateObjectValuesAsync(PersistentObject po, object entity,
        IAsyncDocumentSession? session = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sync counterpart of <see cref="PopulateObjectValuesAsync"/>. Uses
    /// <paramref name="includedDocuments"/> (same pre-load shape as the forward path) to
    /// resolve complex-typed Reference attributes. Throws <see cref="InvalidOperationException"/>
    /// when a Reference attribute targets a complex-typed CLR property and the referenced
    /// document is not present in the dict — failing loud avoids silent data loss.
    /// String-typed reference properties need no resolution and always work here.
    /// </summary>
    void PopulateObjectValues(PersistentObject po, object entity,
        Dictionary<string, object>? includedDocuments = null);
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

        // Route through PopulateObjectValues so AsDetail attributes recurse into nested
        // CLR instances and scalar attributes go through the canonical coercion path.
        // PopulateObjectValues with includedDocuments=null throws on complex-typed
        // Reference attributes — callers in that shape need the async overload.
        PopulateObjectValues(persistentObject, entity);
        return entity;
    }

    public PersistentObject GetPersistentObject(string name)
    {
        var def = modelLoader.GetEntityTypeByName(name)
            ?? throw new KeyNotFoundException($"No entity type with Name '{name}' is registered.");
        return ScaffoldFrom(def);
    }

    public PersistentObject GetPersistentObject(Guid id)
    {
        var def = modelLoader.GetEntityType(id)
            ?? throw new KeyNotFoundException($"No entity type with ObjectTypeId '{id}' is registered.");
        return ScaffoldFrom(def);
    }

    public PersistentObject GetPersistentObject<T>() where T : class
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
            // Dot-notation names (nested paths) are not populated here — reserved for
            // cross-field path access where PersistentObjectAttributeAsDetail isn't the
            // right fit.
            if (attribute.Name.Contains('.'))
                continue;

            var property = entityType.GetProperty(attribute.Name, BindingFlags.Public | BindingFlags.Instance);
            if (property is null || !property.CanRead)
                continue; // silent skip — attribute may be projection-only

            var raw = property.GetValue(entity);

            // AsDetail attributes carry full nested PersistentObject(s) instead of a scalar
            // Value; delegate to the recursive populator.
            if (attribute is PersistentObjectAttributeAsDetail asDetail)
            {
                PopulateAsDetail(asDetail, raw, property.PropertyType, includedDocuments);
                continue;
            }

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
    /// Forward-path recursion for AsDetail attributes: scaffold a nested PO per element of
    /// the entity's collection (array case) or one nested PO for the single field, and
    /// <see cref="PopulateAttributeValues"/> the child entity into it. The pre-scaffolded
    /// <see cref="PersistentObjectAttributeAsDetail.Object"/> from <see cref="ScaffoldFrom"/>
    /// is discarded when a populated one is available — keeping stale values from the empty
    /// scaffold would poison the wire value.
    /// </summary>
    private void PopulateAsDetail(PersistentObjectAttributeAsDetail attr, object? raw, Type propertyType,
        Dictionary<string, object>? includedDocuments)
    {
        attr.Value = null; // AsDetail no longer carries a flat Value.

        if (attr.IsArray)
        {
            var elementType = GetCollectionElementType(propertyType);
            if (elementType is null)
            {
                attr.Objects = [];
                return;
            }
            var elementDef = modelLoader.GetEntityTypeByClrType(elementType.FullName ?? elementType.Name);
            if (elementDef is null)
            {
                attr.Objects = [];
                return;
            }

            var children = new List<PersistentObject>();
            if (raw is System.Collections.IEnumerable enumerable && raw is not string)
            {
                foreach (var item in enumerable)
                {
                    var child = ScaffoldFrom(elementDef);
                    if (item is not null)
                        PopulateAttributeValues(child, item, includedDocuments);
                    children.Add(child);
                }
            }
            attr.Objects = children;
            return;
        }

        if (raw is null)
        {
            attr.Object = null;
            return;
        }

        var targetType = Nullable.GetUnderlyingType(propertyType) ?? propertyType;
        var childDefSingle = modelLoader.GetEntityTypeByClrType(targetType.FullName ?? targetType.Name);
        if (childDefSingle is null)
        {
            attr.Object = null;
            return;
        }

        var nested = ScaffoldFrom(childDefSingle);
        PopulateAttributeValues(nested, raw, includedDocuments);
        attr.Object = nested;
    }

    /// <summary>
    /// Returns the element type for an array (<c>T[]</c>), a generic <see cref="IEnumerable{T}"/>,
    /// or a <c>List&lt;T&gt;</c>. Returns <c>null</c> for non-collection types.
    /// </summary>
    private static Type? GetCollectionElementType(Type propertyType)
    {
        if (propertyType.IsArray)
            return propertyType.GetElementType();

        if (propertyType.IsGenericType)
        {
            var genericArgs = propertyType.GetGenericArguments();
            if (genericArgs.Length == 1) return genericArgs[0];
        }

        foreach (var itf in propertyType.GetInterfaces())
        {
            if (itf.IsGenericType && itf.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                return itf.GetGenericArguments()[0];
        }

        return null;
    }

    /// <summary>
    /// Builds a scaffold PO (metadata only, values null) from an entity type definition.
    /// Shared by <see cref="GetPersistentObject(string)"/>, <see cref="GetPersistentObject(Guid)"/>,
    /// and <see cref="ToPersistentObject(object, Guid, Dictionary{string, object}?)"/>.
    /// Recurses through AsDetail attributes: for single AsDetail, the nested child PO is
    /// pre-scaffolded so UIs render an empty-but-structured form; for array AsDetail,
    /// <see cref="PersistentObjectAttributeAsDetail.Objects"/> starts as an empty list.
    /// </summary>
    private PersistentObject ScaffoldFrom(EntityTypeDefinition def)
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
    /// 14-field metadata copy from an <see cref="EntityAttributeDefinition"/> into a blank
    /// <see cref="PersistentObjectAttribute"/>. Single canonical owner of "which fields
    /// travel from schema to wire". Value stays null; populate step fills it.
    /// When <c>def.DataType == "AsDetail"</c>, returns a
    /// <see cref="PersistentObjectAttributeAsDetail"/> with the nested child PO pre-scaffolded
    /// (single) or an empty list (array).
    /// </summary>
    private PersistentObjectAttribute FromDefinition(EntityAttributeDefinition def)
    {
        if (def.DataType == "AsDetail")
        {
            var asDetail = new PersistentObjectAttributeAsDetail
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
                Query = null,
                Value = null,
                AsDetailType = def.AsDetailType,
            };

            if (!def.IsArray && def.AsDetailType is not null)
            {
                var childDef = modelLoader.GetEntityTypeByClrType(def.AsDetailType);
                if (childDef is not null)
                    asDetail.Object = ScaffoldFrom(childDef);
            }
            else if (def.IsArray)
            {
                asDetail.Objects = [];
            }

            return asDetail;
        }

        return new PersistentObjectAttribute
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
    }

    /// <summary>
    /// Converts a raw scalar property value to its wire representation: enums → string name,
    /// <see cref="Color"/> → <c>#RRGGBB</c>, everything else → passthrough. AsDetail values
    /// are no longer routed through here — <see cref="PopulateAsDetail"/> owns that path,
    /// producing a nested <see cref="PersistentObject"/> instead of a flat dictionary.
    /// </summary>
    private static object? ConvertValueForWire(object? raw, Type propertyType, PersistentObjectAttribute attribute)
    {
        if (raw is null) return null;

        var underlying = Nullable.GetUnderlyingType(propertyType) ?? propertyType;

        if (underlying.IsEnum)
            return raw.ToString();

        if (underlying == typeof(Color) && raw is Color colorValue)
            return colorValue.IsEmpty ? null : $"#{colorValue.R:x2}{colorValue.G:x2}{colorValue.B:x2}";

        return raw;
    }

    public async Task PopulateObjectValuesAsync(PersistentObject po, object entity,
        IAsyncDocumentSession? session = null, CancellationToken cancellationToken = default)
    {
        var entityType = entity.GetType();

        TryWriteId(entityType, entity, po.Id);

        foreach (var attribute in po.Attributes)
        {
            if (attribute.Name.Contains('.'))
                continue;

            var property = entityType.GetProperty(attribute.Name, BindingFlags.Public | BindingFlags.Instance);
            if (property is null || !property.CanWrite)
                continue;

            await WritePropertyAsync(property, entity, attribute, session, includedDocuments: null, cancellationToken);
        }
    }

    public void PopulateObjectValues(PersistentObject po, object entity,
        Dictionary<string, object>? includedDocuments = null)
    {
        var entityType = entity.GetType();

        TryWriteId(entityType, entity, po.Id);

        foreach (var attribute in po.Attributes)
        {
            if (attribute.Name.Contains('.'))
                continue;

            var property = entityType.GetProperty(attribute.Name, BindingFlags.Public | BindingFlags.Instance);
            if (property is null || !property.CanWrite)
                continue;

            // Sync path: delegate to the async core with a null session. Complex-typed
            // References that can't resolve from includedDocuments throw synchronously
            // before any await point, so GetAwaiter().GetResult() won't deadlock.
            WritePropertyAsync(property, entity, attribute, session: null, includedDocuments, CancellationToken.None)
                .GetAwaiter().GetResult();
        }
    }

    private void TryWriteId(Type entityType, object entity, string? id)
    {
        if (string.IsNullOrEmpty(id)) return;
        var idProperty = entityType.GetProperty("Id", BindingFlags.Public | BindingFlags.Instance);
        if (idProperty is null || !idProperty.CanWrite) return;
        SetPropertyValue(idProperty, entity, id);
    }

    private async Task WritePropertyAsync(PropertyInfo property, object entity,
        PersistentObjectAttribute attribute, IAsyncDocumentSession? session,
        Dictionary<string, object>? includedDocuments, CancellationToken cancellationToken)
    {
        var targetType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;

        // AsDetail: instantiate the nested CLR entity type and recurse. Single vs array
        // dispatch on the attribute (schema) rather than the property shape — an AsDetail
        // attribute should always find a matching collection/scalar on the entity.
        if (attribute is PersistentObjectAttributeAsDetail asDetail)
        {
            await WriteAsDetailAsync(asDetail, property, entity, session, includedDocuments, cancellationToken);
            return;
        }

        // TranslatedString merge — per-language overlay so partial updates don't drop
        // translations the client didn't send.
        if (targetType == typeof(TranslatedString))
        {
            MergeTranslatedString(property, entity, attribute.Value);
            return;
        }

        // Reference attributes pointing at a complex entity type need resolution to
        // an actual entity instance. String-typed reference properties fall through
        // to the generic coercion path below — the refId is written as-is.
        if (attribute.DataType == "Reference"
            && targetType != typeof(string)
            && IsComplexType(targetType))
        {
            var refId = ExtractReferenceId(attribute.Value);
            if (string.IsNullOrEmpty(refId))
            {
                property.SetValue(entity, null);
                return;
            }

            if (includedDocuments is not null && includedDocuments.TryGetValue(refId, out var preloaded))
            {
                property.SetValue(entity, preloaded);
                return;
            }

            if (session is not null)
            {
                var loaded = await LoadReferenceAsync(session, targetType, refId, cancellationToken);
                property.SetValue(entity, loaded);
                return;
            }

            throw new InvalidOperationException(
                $"PopulateObjectValues: reference attribute '{attribute.Name}' targets complex CLR type " +
                $"'{targetType.FullName}' and refId '{refId}' could not be resolved. " +
                "Pass a session to PopulateObjectValuesAsync, or pre-load the referenced document and " +
                "hand it in via includedDocuments.");
        }

        SetPropertyValue(property, entity, attribute.Value);
    }

    /// <summary>
    /// Inverse recursion for AsDetail attributes: instantiate the AsDetailType once per
    /// incoming nested PO and populate it via <see cref="PopulateObjectValuesAsync"/>. For
    /// <see cref="PersistentObjectAttributeAsDetail.IsArray"/>, assembles the resulting
    /// entities into the property's concrete collection shape (<c>T[]</c> vs <c>List&lt;T&gt;</c>
    /// vs <see cref="IEnumerable{T}"/>). Null / empty incoming clears the property.
    /// </summary>
    private async Task WriteAsDetailAsync(PersistentObjectAttributeAsDetail attr, PropertyInfo property,
        object entity, IAsyncDocumentSession? session, Dictionary<string, object>? includedDocuments,
        CancellationToken cancellationToken)
    {
        var propertyType = property.PropertyType;

        if (attr.IsArray)
        {
            var elementType = GetCollectionElementType(propertyType)
                ?? throw new InvalidOperationException(
                    $"PopulateObjectValues: AsDetail array attribute '{attr.Name}' targets non-collection property '{property.Name}' of type '{propertyType.FullName}'.");

            var incoming = attr.Objects ?? [];
            var items = new List<object?>(incoming.Count);
            foreach (var childPo in incoming)
            {
                if (childPo is null)
                {
                    items.Add(null);
                    continue;
                }
                var childEntity = Activator.CreateInstance(elementType)
                    ?? throw new InvalidOperationException(
                        $"PopulateObjectValues: could not instantiate AsDetail element type '{elementType.FullName}'.");
                await PopulateObjectValuesAsync(childPo, childEntity, session, cancellationToken);
                items.Add(childEntity);
            }
            property.SetValue(entity, BuildCollection(items, propertyType, elementType));
            return;
        }

        if (attr.Object is null)
        {
            property.SetValue(entity, null);
            return;
        }

        var targetType = Nullable.GetUnderlyingType(propertyType) ?? propertyType;
        var child = Activator.CreateInstance(targetType)
            ?? throw new InvalidOperationException(
                $"PopulateObjectValues: could not instantiate AsDetail type '{targetType.FullName}' for attribute '{attr.Name}'.");
        await PopulateObjectValuesAsync(attr.Object, child, session, cancellationToken);
        property.SetValue(entity, child);
    }

    /// <summary>
    /// Assembles <paramref name="items"/> into the collection shape declared by
    /// <paramref name="propertyType"/>: a typed array when the property is <c>T[]</c>,
    /// a <c>List&lt;T&gt;</c> when the property is List/IList/ICollection/IEnumerable,
    /// otherwise falls through to a typed array. Preserves element order.
    /// </summary>
    private static object BuildCollection(List<object?> items, Type propertyType, Type elementType)
    {
        if (propertyType.IsArray)
        {
            var array = Array.CreateInstance(elementType, items.Count);
            for (var i = 0; i < items.Count; i++)
                array.SetValue(items[i], i);
            return array;
        }

        var listType = typeof(List<>).MakeGenericType(elementType);
        var list = (System.Collections.IList)Activator.CreateInstance(listType)!;
        foreach (var item in items)
            list.Add(item);
        return list;
    }

    private static void MergeTranslatedString(PropertyInfo property, object entity, object? raw)
    {
        var incoming = ParseTranslatedString(raw);

        if (incoming is null)
        {
            // Explicit null / empty incoming clears the property — matches "write null to erase".
            property.SetValue(entity, null);
            return;
        }

        var existing = property.GetValue(entity) as TranslatedString;
        if (existing is null)
        {
            property.SetValue(entity, incoming);
            return;
        }

        // Merge: incoming languages overwrite; languages absent from the incoming dict
        // survive on the existing entity.
        foreach (var (lang, value) in incoming.Translations)
            existing.Translations[lang] = value;
    }

    private static TranslatedString? ParseTranslatedString(object? raw)
    {
        if (raw is null) return null;
        if (raw is TranslatedString ts) return ts;

        if (raw is JsonElement je)
        {
            if (je.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return null;
            if (je.ValueKind == JsonValueKind.Object)
            {
                var parsed = new TranslatedString();
                foreach (var prop in je.EnumerateObject())
                {
                    var text = prop.Value.ValueKind == JsonValueKind.String
                        ? prop.Value.GetString()
                        : prop.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
                            ? null
                            : prop.Value.ToString();
                    if (text is not null) parsed.Translations[prop.Name] = text;
                }
                return parsed.Translations.Count == 0 ? null : parsed;
            }
            return null;
        }

        if (raw is IDictionary<string, string> stringDict)
        {
            if (stringDict.Count == 0) return null;
            var parsed = new TranslatedString();
            foreach (var (k, v) in stringDict) parsed.Translations[k] = v;
            return parsed;
        }

        if (raw is IDictionary<string, object?> objDict)
        {
            var parsed = new TranslatedString();
            foreach (var (k, v) in objDict)
            {
                if (v is null) continue;
                parsed.Translations[k] = v is string s ? s : v.ToString()!;
            }
            return parsed.Translations.Count == 0 ? null : parsed;
        }

        if (raw is string json && !string.IsNullOrWhiteSpace(json))
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                return ParseTranslatedString(doc.RootElement.Clone());
            }
            catch (JsonException)
            {
                return null;
            }
        }

        return null;
    }

    private static string? ExtractReferenceId(object? value)
    {
        if (value is null) return null;
        if (value is string s) return string.IsNullOrEmpty(s) ? null : s;
        if (value is JsonElement je)
        {
            return je.ValueKind switch
            {
                JsonValueKind.String => je.GetString(),
                JsonValueKind.Null or JsonValueKind.Undefined => null,
                _ => je.ToString(),
            };
        }
        return value.ToString();
    }

    private static async Task<object?> LoadReferenceAsync(IAsyncDocumentSession session,
        Type targetType, string refId, CancellationToken cancellationToken)
    {
        var method = typeof(IAsyncDocumentSession).GetMethod(
            nameof(IAsyncDocumentSession.LoadAsync),
            [typeof(string), typeof(CancellationToken)])
            ?? throw new InvalidOperationException("Could not resolve IAsyncDocumentSession.LoadAsync<T>(string, CancellationToken).");
        var generic = method.MakeGenericMethod(targetType);
        var task = (Task)generic.Invoke(session, [refId, cancellationToken])!;
        await task.ConfigureAwait(false);
        return task.GetType().GetProperty("Result")?.GetValue(task);
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
