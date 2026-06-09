using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Reflection;
using MintPlayer.Spark.Services.Breadcrumb;
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
    /// <paramref name="breadcrumbs"/> is supplied, the PO's Name/Breadcrumb and each Reference
    /// attribute's Breadcrumb(s) are filled by id lookup from the pre-resolved result (see
    /// <see cref="Breadcrumb.IBreadcrumbResolver"/>). Attributes whose name has no
    /// matching property on the entity are left with Value = null (Vidyano parity);
    /// attributes whose name contains '.' are skipped.
    /// </summary>
    void PopulateAttributeValues(PersistentObject po, object entity, BreadcrumbResult? breadcrumbs = null);

    /// <summary>
    /// Typed convenience overload of
    /// <see cref="PopulateAttributeValues(PersistentObject, object, BreadcrumbResult?)"/>.
    /// </summary>
    void PopulateAttributeValues<T>(PersistentObject po, T entity, BreadcrumbResult? breadcrumbs = null) where T : class;

    /// <summary>
    /// Convenience wrapper: <see cref="GetPersistentObject(Guid)"/> + <see cref="PopulateAttributeValues"/>.
    /// Existing call sites (DatabaseAccess, QueryExecutor, StreamingQueryExecutor) keep this signature.
    /// </summary>
    PersistentObject ToPersistentObject(object entity, Guid objectTypeId, BreadcrumbResult? breadcrumbs = null);

    /// <summary>
    /// Typed convenience overload that derives the ObjectTypeId from
    /// <c>typeof(T).FullName</c> via <see cref="IModelLoader.GetEntityTypeByClrType"/>.
    /// Throws <see cref="KeyNotFoundException"/> when no entity type is registered
    /// for the CLR type. Callers in framework internals that already have a Guid
    /// (DatabaseAccess, QueryExecutor) continue to use the non-generic overload.
    /// </summary>
    PersistentObject ToPersistentObject<T>(T entity, BreadcrumbResult? breadcrumbs = null) where T : class;

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
    // Optional so the many test sites that construct `new EntityMapper(modelLoader)` keep compiling
    // (the source generator gives a nullable [Inject] field a `= null` default). Only used to read
    // the configured breadcrumb reference separator when rendering an embedded row's own breadcrumb.
    [Inject] private readonly Configuration.SparkOptions? options;

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

    public PersistentObject ToPersistentObject<T>(T entity, BreadcrumbResult? breadcrumbs = null) where T : class
        => ToPersistentObject(entity, ResolveDefByClrType(typeof(T)).Id, breadcrumbs);

    public void PopulateAttributeValues<T>(PersistentObject po, T entity, BreadcrumbResult? breadcrumbs = null) where T : class
        => PopulateAttributeValues(po, (object)entity, breadcrumbs);

    private EntityTypeDefinition ResolveDefByClrType(Type clrType)
    {
        var name = clrType.FullName ?? clrType.Name;
        return modelLoader.GetEntityTypeByClrType(name)
            ?? throw new KeyNotFoundException($"No entity type registered for CLR type '{name}'.");
    }

    public PersistentObject ToPersistentObject(object entity, Guid objectTypeId, BreadcrumbResult? breadcrumbs = null)
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

        PopulateAttributeValues(po, entity, breadcrumbs);
        return po;
    }

    public void PopulateAttributeValues(PersistentObject po, object entity, BreadcrumbResult? breadcrumbs = null)
    {
        var entityType = entity.GetType();
        var idProperty = entityType.GetCachedProperty("Id");
        po.Id = idProperty is not null ? AccessorCache.GetGetter(idProperty)(entity)?.ToString() : null;

        // Name/Breadcrumb come from the pre-resolved breadcrumb result (recursive, server-side).
        // Embedded AsDetail objects have no id and aren't keyed in the result → render their own
        // [Breadcrumb] template in place, substituting the resolved breadcrumb for each reference
        // token (the resolver descended into AsDetail children, so those targets are resolved).
        var breadcrumb = breadcrumbs?.Get(po.Id);
        if (string.IsNullOrWhiteSpace(breadcrumb) && breadcrumbs is not null && string.IsNullOrEmpty(po.Id))
        {
            var def = modelLoader.GetEntityTypeByClrType(entityType.FullName ?? entityType.Name);
            if (def is not null)
                breadcrumb = EmbeddedBreadcrumbRenderer.Render(
                    entity, def, breadcrumbs, options?.Breadcrumb.ReferenceSeparator ?? ", ");
        }
        if (string.IsNullOrWhiteSpace(breadcrumb))
            breadcrumb = entityType.Name;
        po.Name = breadcrumb;
        po.Breadcrumb = breadcrumb;

        foreach (var attribute in po.Attributes)
        {
            // Dot-notation names (nested paths) are not populated here — reserved for
            // cross-field path access where PersistentObjectAttributeAsDetail isn't the
            // right fit.
            if (attribute.Name.Contains('.'))
                continue;

            var property = entityType.GetCachedProperty(attribute.Name);
            if (property is null || !property.CanRead)
                continue; // silent skip — attribute may be projection-only

            var raw = AccessorCache.GetGetter(property)(entity);

            // AsDetail attributes carry full nested PersistentObject(s) instead of a scalar
            // Value; delegate to the recursive populator.
            if (attribute is PersistentObjectAttributeAsDetail asDetail)
            {
                PopulateAsDetail(asDetail, raw, property.PropertyType, breadcrumbs);
                continue;
            }

            attribute.Value = ConvertValueForWire(raw, property.PropertyType, attribute);

            if (breadcrumbs is null)
                continue;

            // Reference attributes: copy the referenced entity's pre-resolved breadcrumb (by id)
            // so the client can render it without a second round-trip. The breadcrumb is fully
            // recursive — the resolver already expanded any nested references.
            if (attribute.DataType == "Reference" && !attribute.IsArray
                && attribute.Value is string refId && !string.IsNullOrEmpty(refId))
            {
                attribute.Breadcrumb = breadcrumbs.Get(refId);
            }
            // Reference ARRAY: a breadcrumb per id (id → breadcrumb) so the client can render one
            // chip per selected reference.
            else if (attribute.DataType == "Reference" && attribute.IsArray
                && raw is System.Collections.IEnumerable refIds && raw is not string)
            {
                Dictionary<string, string?>? perId = null;
                foreach (var idObj in refIds)
                {
                    var id = idObj?.ToString();
                    if (string.IsNullOrEmpty(id)) continue;
                    (perId ??= [])[id] = breadcrumbs.Get(id);
                }
                attribute.Breadcrumbs = perId;
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
        BreadcrumbResult? breadcrumbs)
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
                        PopulateAttributeValues(child, item, breadcrumbs);
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
        PopulateAttributeValues(nested, raw, breadcrumbs);
        attr.Object = nested;
    }

    /// <summary>
    /// Returns the element type for an array (<c>T[]</c>), a generic <see cref="IEnumerable{T}"/>,
    /// or a <c>List&lt;T&gt;</c>. Returns <c>null</c> for non-collection types. Cached per
    /// <see cref="Type"/> via <see cref="ReflectionCache"/>; called per-AsDetail-property
    /// per-row in the read path, so the cache hit matters.
    /// </summary>
    private static Type? GetCollectionElementType(Type propertyType)
        => ReflectionCache.GetOrAdd<(string Op, Type Type), Type?>(
            ("EntityMapper.CollectionElement", propertyType),
            static k => ResolveCollectionElementType(k.Type));

    private static Type? ResolveCollectionElementType(Type propertyType)
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
        var schemaAttributes = GetSchemaAttributeMap(entityType);

        TryWriteId(entityType, entity, po.Id);

        foreach (var attribute in po.Attributes)
        {
            if (attribute.Name.Contains('.'))
                continue;

            if (!IsWritableBySchema(attribute, schemaAttributes))
                continue;

            var property = entityType.GetCachedProperty(attribute.Name);
            if (property is null || !property.CanWrite)
                continue;

            await WritePropertyAsync(property, entity, attribute, session, includedDocuments: null, cancellationToken);
        }
    }

    public void PopulateObjectValues(PersistentObject po, object entity,
        Dictionary<string, object>? includedDocuments = null)
    {
        var entityType = entity.GetType();
        var schemaAttributes = GetSchemaAttributeMap(entityType);

        TryWriteId(entityType, entity, po.Id);

        foreach (var attribute in po.Attributes)
        {
            if (attribute.Name.Contains('.'))
                continue;

            if (!IsWritableBySchema(attribute, schemaAttributes))
                continue;

            var property = entityType.GetCachedProperty(attribute.Name);
            if (property is null || !property.CanWrite)
                continue;

            // Sync path: delegate to the async core with a null session. Complex-typed
            // References that can't resolve from includedDocuments throw synchronously
            // before any await point, so GetAwaiter().GetResult() won't deadlock.
            WritePropertyAsync(property, entity, attribute, session: null, includedDocuments, CancellationToken.None)
                .GetAwaiter().GetResult();
        }
    }

    /// <summary>
    /// R2-H8 schema-side write gate. The client-supplied attribute carries its
    /// own IsReadOnly / IsVisible flags, but those are advisory — the source of
    /// truth is the entity's schema definition. We look up the schema for the
    /// entity's CLR type and refuse any write to an attribute the schema marks
    /// as IsReadOnly=true or IsVisible=false. Attributes whose name has no
    /// schema entry are also refused (defense-in-depth against client-introduced
    /// fields that happen to match a CLR property name not declared on the
    /// model).
    /// <para>
    /// When the entity type has no schema registration at all (e.g. ad-hoc
    /// internal mapping), the map is empty and every attribute is allowed —
    /// callers in that path have already bypassed the framework's contract.
    /// </para>
    /// </summary>
    private Dictionary<string, EntityAttributeDefinition>? GetSchemaAttributeMap(Type entityType)
    {
        var clrTypeName = entityType.FullName ?? entityType.Name;
        var entityTypeDef = modelLoader.GetEntityTypeByClrType(clrTypeName);
        if (entityTypeDef?.Attributes is null)
            return null;
        var map = new Dictionary<string, EntityAttributeDefinition>(StringComparer.Ordinal);
        foreach (var def in entityTypeDef.Attributes)
            map[def.Name] = def;
        return map;
    }

    private static bool IsWritableBySchema(PersistentObjectAttribute attribute,
        Dictionary<string, EntityAttributeDefinition>? schemaAttributes)
    {
        if (schemaAttributes is null) return true;
        if (!schemaAttributes.TryGetValue(attribute.Name, out var def))
            return false;
        if (def.IsReadOnly) return false;
        if (!def.IsVisible) return false;
        return true;
    }

    private void TryWriteId(Type entityType, object entity, string? id)
    {
        if (string.IsNullOrEmpty(id)) return;
        var idProperty = entityType.GetCachedProperty("Id");
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

        // Reference ARRAYS carry an array of ids in Value, not a single refId — route
        // them to the collection coercion path (SetPropertyValue) rather than the
        // single-reference resolver below, which would try to load one document per the
        // whole serialized array.
        if (attribute.IsArray)
        {
            SetPropertyValue(property, entity, attribute.Value);
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
                AccessorCache.GetSetter(property)(entity, null);
                return;
            }

            if (includedDocuments is not null && includedDocuments.TryGetValue(refId, out var preloaded))
            {
                AccessorCache.GetSetter(property)(entity, preloaded);
                return;
            }

            if (session is not null)
            {
                var loaded = await LoadReferenceAsync(session, targetType, refId, cancellationToken);
                AccessorCache.GetSetter(property)(entity, loaded);
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
            AccessorCache.GetSetter(property)(entity, BuildCollection(items, propertyType, elementType));
            return;
        }

        if (attr.Object is null)
        {
            AccessorCache.GetSetter(property)(entity, null);
            return;
        }

        var targetType = Nullable.GetUnderlyingType(propertyType) ?? propertyType;
        var child = Activator.CreateInstance(targetType)
            ?? throw new InvalidOperationException(
                $"PopulateObjectValues: could not instantiate AsDetail type '{targetType.FullName}' for attribute '{attr.Name}'.");
        await PopulateObjectValuesAsync(attr.Object, child, session, cancellationToken);
        AccessorCache.GetSetter(property)(entity, child);
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
            AccessorCache.GetSetter(property)(entity, null);
            return;
        }

        var existing = AccessorCache.GetGetter(property)(entity) as TranslatedString;
        if (existing is null)
        {
            AccessorCache.GetSetter(property)(entity, incoming);
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
        var generic = ReflectionCache.GetOrAdd<(string Op, Type Type), MethodInfo>(
            ("EntityMapper.SessionLoadAsync", targetType),
            static k =>
            {
                var method = typeof(IAsyncDocumentSession).GetMethod(
                    nameof(IAsyncDocumentSession.LoadAsync),
                    [typeof(string), typeof(CancellationToken)])
                    ?? throw new InvalidOperationException("Could not resolve IAsyncDocumentSession.LoadAsync<T>(string, CancellationToken).");
                return method.MakeGenericMethod(k.Type);
            });
        var task = (Task)generic.Invoke(session, [refId, cancellationToken])!;
        await task.ConfigureAwait(false);
        var resultProp = task.GetType().GetCachedProperty("Result");
        return resultProp is not null ? AccessorCache.GetGetter(resultProp)(task) : null;
    }

    private void SetPropertyValue(PropertyInfo property, object entity, object? value)
    {
        var targetType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
        var setter = AccessorCache.GetSetter(property);

        // Handle JsonElement - either extract simple value or deserialize complex types
        if (value is JsonElement je)
        {
            if (je.ValueKind == JsonValueKind.Array)
            {
                // Deserialize array/collection of complex objects (e.g., CarreerJob[], List<CarreerJob>)
                try
                {
                    var deserializedValue = je.Deserialize(targetType, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    setter(entity, deserializedValue);
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
                    setter(entity, deserializedValue);
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

        // Collection target handed a non-JsonElement enumerable directly (e.g. a
        // List<string> of reference ids assembled in-process rather than off the wire):
        // round-trip through JSON so element coercion uses the same path as the
        // JsonElement-array branch above. AsDetail arrays never reach here — they are
        // handled by WriteAsDetailAsync before SetPropertyValue.
        if (value is not null && value is System.Collections.IEnumerable && value is not string
            && GetCollectionElementType(targetType) is not null)
        {
            try
            {
                var json = JsonSerializer.Serialize(value);
                using var tmpDoc = JsonDocument.Parse(json);
                setter(entity, tmpDoc.RootElement.Deserialize(targetType, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }));
            }
            catch
            {
                // Skip values that can't be coerced into the collection type.
            }
            return;
        }

        if (value == null)
        {
            if (Nullable.GetUnderlyingType(property.PropertyType) != null || !property.PropertyType.IsValueType)
            {
                setter(entity, null);
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

            setter(entity, convertedValue);
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
        return type.GetCachedProperties().Length > 0;
    }

    private Type? ResolveType(string clrType)
    {
        // Cache positive and negative resolutions: assembly walks are expensive and the
        // same CLR type names are looked up repeatedly across requests. ReflectionCache
        // memoizes null results too — so unresolvable names don't re-walk every time.
        return ReflectionCache.GetOrAdd<Type?>(
            $"resolveType|{clrType}",
            () =>
            {
                var type = Type.GetType(clrType);
                if (type != null) return type;

                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    type = assembly.GetType(clrType);
                    if (type != null) return type;
                }

                return null;
            });
    }
}
