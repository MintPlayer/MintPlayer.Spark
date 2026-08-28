using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Reflection;
using MintPlayer.Spark.Exceptions;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;
using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;

namespace MintPlayer.Spark.Services;

/// <summary>
/// Processes incoming sync actions on the owner module.
/// Resolves the CLR entity type from the collection name and runs the operation
/// through the IPersistentObjectActions pipeline (preserving lifecycle hooks).
/// Constructs a PersistentObject from the incoming data dictionary with IsValueChanged
/// metadata so the actions pipeline has full change tracking context.
/// </summary>
[Register(typeof(ISyncActionHandler), ServiceLifetime.Scoped)]
internal partial class SyncActionHandler : ISyncActionHandler
{
    [Inject] private readonly IDocumentStore documentStore;
    [Inject] private readonly IActionsResolver actionsResolver;
    [Inject] private readonly IModelLoader modelLoader;
    [Inject] private readonly IEntityMapper entityMapper;
    [Inject] private readonly IDatabaseAccess databaseAccess;
    [Inject] private readonly ILogger<SyncActionHandler> logger;

    // Cache: collection name → CLR entity type
    private static readonly ConcurrentDictionary<string, Type?> _collectionTypeCache = new(StringComparer.OrdinalIgnoreCase);

    public async Task<string?> HandleSaveAsync(string collection, string? documentId, Dictionary<string, object?> data, string[]? properties)
    {
        var entityType = ResolveEntityType(collection)
            ?? throw new InvalidOperationException($"Cannot resolve entity type for collection '{collection}'.");

        // Build a PersistentObject from the sync action data
        var po = BuildPersistentObject(entityType, documentId, data, properties);
        EnsureAuthorizable(po, collection);

        // Through the same chokepoint as every other write (F4/M11). This path used to invoke the
        // actions pipeline directly, so an authenticated module could insert, update or delete any
        // document in any collection — the certificate proved *which* module was calling and
        // nothing consulted what that module was allowed to touch.
        var saved = await databaseAccess.SavePersistentObjectAsync(po);

        logger.LogInformation("Sync action: saved {Collection}/{DocumentId} ({PropertyMode})",
            collection, saved.Id ?? documentId,
            properties != null ? $"partial: {string.Join(", ", properties)}" : "full");
        return saved.Id;
    }

    public async Task HandleDeleteAsync(string collection, string documentId)
    {
        var entityType = ResolveEntityType(collection)
            ?? throw new InvalidOperationException($"Cannot resolve entity type for collection '{collection}'.");

        var entityTypeDef = FindEntityTypeDefinition(entityType)
            ?? throw new SparkSyncNotAuthorizableException(collection);

        await databaseAccess.DeletePersistentObjectAsync(entityTypeDef.Id, documentId);

        logger.LogInformation("Sync action: deleted {Collection}/{DocumentId}", collection, documentId);
    }

    /// <summary>
    /// Refuses a sync action against a type with no registered <see cref="EntityTypeDefinition"/>.
    /// <para>
    /// Such a type has no name for <c>security.json</c> to grant rights on, so no authorization
    /// decision can be made about it — and this path previously wrote it anyway, through the CLR
    /// reflection fallback. Writing what cannot be authorized is the failure this milestone exists
    /// to remove, so it fails closed: <b>unevaluable is not permitted</b>.
    /// </para>
    /// <para>
    /// The fix for an app that hits this is to register the entity (add it to the app's
    /// <c>SparkContext</c> and run <c>--spark-synchronize-model</c>), which is also what makes it
    /// governable.
    /// </para>
    /// </summary>
    private static void EnsureAuthorizable(PersistentObject po, string collection)
    {
        if (po.ObjectTypeId == Guid.Empty)
            throw new SparkSyncNotAuthorizableException(collection);
    }

    /// <summary>
    /// Builds a PersistentObject from the incoming sync action data dictionary.
    /// <para>
    /// When <paramref name="properties"/> is supplied the result carries <strong>only</strong>
    /// those attributes — a partial update, per the contract on <c>SyncAction.Properties</c>.
    /// This is load-bearing rather than an optimization: the write path writes every attribute it
    /// is handed and never consults <c>IsValueChanged</c>, so an unnamed attribute left on the PO
    /// would overwrite stored data with null. When it is null, every attribute is included and
    /// <c>IsValueChanged</c> reflects whether the data dictionary carried a value.
    /// </para>
    /// When an <see cref="EntityTypeDefinition"/> is registered for the entity type the
    /// schema path runs through <see cref="IEntityMapper.GetPersistentObject(Guid)"/>, so
    /// every attribute gets the canonical 14-field metadata; otherwise the CLR-reflection
    /// fallback inventories the entity's public read/write properties (no schema available).
    /// </summary>
    internal PersistentObject BuildPersistentObject(Type entityType, string? documentId, Dictionary<string, object?> data, string[]? properties)
    {
        var entityTypeDef = FindEntityTypeDefinition(entityType);
        var propertySet = properties != null
            ? new HashSet<string>(properties, StringComparer.OrdinalIgnoreCase)
            : null;

        if (entityTypeDef is null)
            return BuildFromClrReflection(entityType, documentId, data, propertySet);

        // Schema path — full metadata scaffolded by IEntityMapper, values overlaid from data dict.
        var po = entityMapper.GetPersistentObject(entityTypeDef.Id);
        po.Id = documentId;

        // A partial update must carry ONLY the attributes it updates. The scaffold starts with
        // every attribute in the model, and the write path (EntityMapper.PopulateObjectValues*)
        // writes every attribute it is given — it does not consult IsValueChanged — so leaving
        // the unnamed ones in place with a null value would blank out stored data the sender
        // never mentioned. See SyncAction.Properties: "only these properties are merged".
        if (propertySet is not null)
            po.RetainAttributes(a => propertySet.Contains(a.Name));

        foreach (var attribute in po.Attributes)
        {
            var hasValue = TryGetValue(data, attribute.Name, out var value);
            attribute.Value = hasValue ? NormalizeValue(value) : null;
            attribute.IsValueChanged = propertySet != null
                ? propertySet.Contains(attribute.Name)
                : hasValue;
        }

        return po;
    }

    /// <summary>
    /// CLR-reflection fallback for entity types that have no registered
    /// <see cref="EntityTypeDefinition"/>. No schema metadata is available, so attributes
    /// carry only <c>Name</c>, <c>Value</c>, and <c>IsValueChanged</c>.
    /// </summary>
    private static PersistentObject BuildFromClrReflection(Type entityType, string? documentId, Dictionary<string, object?> data, HashSet<string>? propertySet)
    {
        var po = new PersistentObject
        {
            Id = documentId,
            ObjectTypeId = Guid.Empty,
            Name = entityType.Name,
        };

        // This is the "no registered EntityTypeDefinition" fallback, so it cannot inherit the
        // synchronizer's exclusion — it has to consult the attribute itself.
        // Writable, not merely "in the model": this builds the object an incoming sync action is
        // applied to, so an attribute for a property with no setter could never be written anyway.
        foreach (var prop in entityType.GetSparkWritableProperties())
        {
            // Same partial-update rule as the schema path: an attribute the sender did not name
            // must not appear at all, or the write path blanks the stored value.
            if (propertySet is not null && !propertySet.Contains(prop.Name))
                continue;

            var hasValue = TryGetValue(data, prop.Name, out var value);
            var isChanged = propertySet != null
                ? propertySet.Contains(prop.Name)
                : hasValue;

            po.AddAttribute(new PersistentObjectAttribute
            {
                Name = prop.Name,
                Value = hasValue ? NormalizeValue(value) : null,
                IsValueChanged = isChanged,
            });
        }

        return po;
    }

    private EntityTypeDefinition? FindEntityTypeDefinition(Type entityType)
    {
        var clrTypeName = entityType.FullName ?? entityType.Name;
        return modelLoader.GetEntityTypeByClrType(clrTypeName);
    }

    /// <summary>
    /// Case-insensitive key lookup in the data dictionary.
    /// </summary>
    private static bool TryGetValue(Dictionary<string, object?> data, string key, out object? value)
    {
        if (data.TryGetValue(key, out value))
            return true;

        // Case-insensitive fallback
        foreach (var kvp in data)
        {
            if (string.Equals(kvp.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                value = kvp.Value;
                return true;
            }
        }

        value = null;
        return false;
    }

    /// <summary>
    /// Normalizes a dictionary value to a plain .NET type.
    /// Values coming from System.Text.Json deserialization (HTTP path) arrive as JsonElement;
    /// values coming from Newtonsoft.Json deserialization (RavenDB path) arrive as .NET primitives.
    /// </summary>
    private static object? NormalizeValue(object? value)
    {
        if (value is JsonElement element)
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

        // Already a .NET primitive (from Newtonsoft/RavenDB deserialization or direct assignment)
        return value;
    }

    private Type? ResolveEntityType(string collection)
    {
        // R2-H15: don't cache unresolved (null) entries — the cache is keyed on
        // request-controlled strings, so caching every miss lets an attacker
        // grow the static dictionary without bound (memory DoS). Resolved
        // (non-null) entries are bounded by the schema and safe to cache.
        if (_collectionTypeCache.TryGetValue(collection, out var cached))
            return cached;

        Type? resolved = null;
        foreach (var entityTypeDef in modelLoader.GetEntityTypes())
        {
            var clrType = SparkTypeResolver.ResolveClrType(entityTypeDef.ClrType);
            if (clrType == null) continue;

            var collectionName = documentStore.Conventions.FindCollectionName(clrType);
            if (string.Equals(collectionName, collection, StringComparison.OrdinalIgnoreCase))
            {
                resolved = clrType;
                break;
            }
        }

        if (resolved is not null)
            _collectionTypeCache[collection] = resolved;
        return resolved;
    }


    private async Task<object> SaveEntityViaActionsAsync(IAsyncDocumentSession session, Type entityType, PersistentObject obj)
    {
        var actions = actionsResolver.ResolveForType(entityType);
        var onSaveMethod = ReflectionCache.GetOrAdd<(string Op, Type Actions), MethodInfo>(
            ("SyncActionHandler.OnSaveAsync", actions.GetType()),
            static k => k.Actions.GetMethod("OnSaveAsync")
                ?? throw new InvalidOperationException(
                    $"Actions type '{k.Actions.FullName}' is missing required method 'OnSaveAsync'."));
        var task = (Task)onSaveMethod.Invoke(actions, [session, obj])!;
        await task;
        return task.GetCompletedTaskResult()!;
    }

    private async Task DeleteEntityViaActionsAsync(IAsyncDocumentSession session, Type entityType, string id)
    {
        var actions = actionsResolver.ResolveForType(entityType);
        var onDeleteMethod = ReflectionCache.GetOrAdd<(string Op, Type Actions), MethodInfo>(
            ("SyncActionHandler.OnDeleteAsync", actions.GetType()),
            static k => k.Actions.GetMethod("OnDeleteAsync")
                ?? throw new InvalidOperationException(
                    $"Actions type '{k.Actions.FullName}' is missing required method 'OnDeleteAsync'."));
        var task = (Task)onDeleteMethod.Invoke(actions, [session, id])!;
        await task;
    }
}
