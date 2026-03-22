using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
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
    [Inject] private readonly ILogger<SyncActionHandler> logger;

    // Cache: collection name → CLR entity type
    private static readonly ConcurrentDictionary<string, Type?> _collectionTypeCache = new(StringComparer.OrdinalIgnoreCase);

    public async Task<string?> HandleSaveAsync(string collection, string? documentId, Dictionary<string, object?> data, string[]? properties)
    {
        var entityType = ResolveEntityType(collection)
            ?? throw new InvalidOperationException($"Cannot resolve entity type for collection '{collection}'.");

        // Build a PersistentObject from the sync action data
        var po = BuildPersistentObject(entityType, documentId, data, properties);

        // Run through the actions pipeline (which now receives PO and does entity mapping inside)
        using var session = documentStore.OpenAsyncSession();
        var savedEntity = await SaveEntityViaActionsAsync(session, entityType, po);

        // Extract the generated/existing ID
        var resultIdProperty = entityType.GetProperty("Id", BindingFlags.Public | BindingFlags.Instance);
        var resultId = resultIdProperty?.GetValue(savedEntity)?.ToString();

        logger.LogInformation("Sync action: saved {Collection}/{DocumentId} ({PropertyMode})",
            collection, resultId ?? documentId,
            properties != null ? $"partial: {string.Join(", ", properties)}" : "full");
        return resultId;
    }

    public async Task HandleDeleteAsync(string collection, string documentId)
    {
        var entityType = ResolveEntityType(collection)
            ?? throw new InvalidOperationException($"Cannot resolve entity type for collection '{collection}'.");

        using var session = documentStore.OpenAsyncSession();
        await DeleteEntityViaActionsAsync(session, entityType, documentId);

        logger.LogInformation("Sync action: deleted {Collection}/{DocumentId}", collection, documentId);
    }

    /// <summary>
    /// Builds a PersistentObject from the incoming sync action data dictionary.
    /// Marks attributes as IsValueChanged based on the Properties array.
    /// Uses the EntityTypeDefinition to construct proper attribute metadata.
    /// </summary>
    private PersistentObject BuildPersistentObject(Type entityType, string? documentId, Dictionary<string, object?> data, string[]? properties)
    {
        // Find the entity type definition for attribute metadata
        var entityTypeDef = FindEntityTypeDefinition(entityType);
        var propertySet = properties != null
            ? new HashSet<string>(properties, StringComparer.OrdinalIgnoreCase)
            : null;

        var attributes = new List<PersistentObjectAttribute>();

        if (entityTypeDef?.Attributes != null)
        {
            foreach (var attrDef in entityTypeDef.Attributes)
            {
                var hasValue = TryGetValue(data, attrDef.Name, out var value);

                var isChanged = propertySet != null
                    ? propertySet.Contains(attrDef.Name)
                    : hasValue;

                attributes.Add(new PersistentObjectAttribute
                {
                    Name = attrDef.Name,
                    Label = attrDef.Label,
                    Value = hasValue ? NormalizeValue(value) : null,
                    IsValueChanged = isChanged,
                    DataType = attrDef.DataType,
                    IsRequired = attrDef.IsRequired,
                    IsVisible = attrDef.IsVisible,
                    IsReadOnly = attrDef.IsReadOnly,
                    Order = attrDef.Order,
                    ShowedOn = attrDef.ShowedOn,
                    Rules = attrDef.Rules ?? [],
                });
            }
        }
        else
        {
            // Fallback: build attributes from entity type's CLR properties
            foreach (var prop in entityType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (string.Equals(prop.Name, "Id", StringComparison.Ordinal)) continue;
                if (!prop.CanRead || !prop.CanWrite) continue;

                var hasValue = TryGetValue(data, prop.Name, out var value);

                var isChanged = propertySet != null
                    ? propertySet.Contains(prop.Name)
                    : hasValue;

                attributes.Add(new PersistentObjectAttribute
                {
                    Name = prop.Name,
                    Value = hasValue ? NormalizeValue(value) : null,
                    IsValueChanged = isChanged,
                });
            }
        }

        return new PersistentObject
        {
            Id = documentId,
            ObjectTypeId = entityTypeDef?.Id ?? Guid.Empty,
            Name = entityTypeDef?.Name ?? entityType.Name,
            Attributes = attributes.ToArray(),
        };
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
        return _collectionTypeCache.GetOrAdd(collection, col =>
        {
            // Iterate all entity type definitions, resolve each CLR type,
            // and check if the RavenDB collection name matches
            foreach (var entityTypeDef in modelLoader.GetEntityTypes())
            {
                var clrType = ResolveType(entityTypeDef.ClrType);
                if (clrType == null) continue;

                var collectionName = documentStore.Conventions.FindCollectionName(clrType);
                if (string.Equals(collectionName, col, StringComparison.OrdinalIgnoreCase))
                    return clrType;
            }

            return null;
        });
    }

    private static Type? ResolveType(string clrType)
    {
        var type = Type.GetType(clrType);
        if (type != null) return type;

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            type = assembly.GetType(clrType);
            if (type != null) return type;
        }

        return null;
    }

    private async Task<object> SaveEntityViaActionsAsync(IAsyncDocumentSession session, Type entityType, PersistentObject obj)
    {
        var actions = actionsResolver.ResolveForType(entityType);
        var onSaveMethod = actions.GetType().GetMethod("OnSaveAsync")!;
        var task = (Task)onSaveMethod.Invoke(actions, [session, obj])!;
        await task;
        return task.GetType().GetProperty("Result")!.GetValue(task)!;
    }

    private async Task DeleteEntityViaActionsAsync(IAsyncDocumentSession session, Type entityType, string id)
    {
        var actions = actionsResolver.ResolveForType(entityType);
        var onDeleteMethod = actions.GetType().GetMethod("OnDeleteAsync")!;
        var task = (Task)onDeleteMethod.Invoke(actions, [session, id])!;
        await task;
    }
}
