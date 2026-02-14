using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Services;

/// <summary>
/// Processes incoming sync actions on the owner module.
/// Resolves the CLR entity type from the collection name and runs the operation
/// through the IPersistentObjectActions pipeline (preserving lifecycle hooks).
/// When Properties is set, performs a partial merge instead of full replacement.
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

    public async Task<string?> HandleSaveAsync(string collection, string? documentId, JsonElement data, string[]? properties)
    {
        var entityType = ResolveEntityType(collection)
            ?? throw new InvalidOperationException($"Cannot resolve entity type for collection '{collection}'.");

        object entity;

        if (documentId != null && properties != null && properties.Length > 0)
        {
            // Partial update: load existing entity, merge only the specified properties
            entity = await LoadAndMergeAsync(entityType, documentId, data, properties);
        }
        else
        {
            // Full save (insert or full replacement)
            entity = JsonSerializer.Deserialize(data.GetRawText(), entityType, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            }) ?? throw new InvalidOperationException($"Failed to deserialize data for collection '{collection}'.");

            // Set the document ID if updating
            if (documentId != null)
            {
                var idProperty = entityType.GetProperty("Id", BindingFlags.Public | BindingFlags.Instance);
                idProperty?.SetValue(entity, documentId);
            }
        }

        // Run through the actions pipeline
        using var session = documentStore.OpenAsyncSession();
        var savedEntity = await SaveEntityViaActionsAsync(session, entityType, entity);

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
    /// Loads the existing entity from the database and merges only the specified properties
    /// from the incoming JSON data. This preserves owner-only properties that aren't replicated.
    /// </summary>
    private async Task<object> LoadAndMergeAsync(Type entityType, string documentId, JsonElement data, string[] properties)
    {
        using var session = documentStore.OpenAsyncSession();
        var existing = await LoadEntityAsync(session, entityType, documentId)
            ?? throw new InvalidOperationException($"Document '{documentId}' not found for partial update.");

        // Deserialize incoming data to a temporary object of the same type
        var incoming = JsonSerializer.Deserialize(data.GetRawText(), entityType, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? throw new InvalidOperationException($"Failed to deserialize incoming data for partial update.");

        // Copy only the specified properties from incoming to existing
        var propertySet = new HashSet<string>(properties, StringComparer.OrdinalIgnoreCase);
        foreach (var prop in entityType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!propertySet.Contains(prop.Name)) continue;
            if (!prop.CanRead || !prop.CanWrite) continue;

            var value = prop.GetValue(incoming);
            prop.SetValue(existing, value);
        }

        // Detach from session — the actions pipeline will use its own session
        session.Advanced.Evict(existing);
        return existing;
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

    private static async Task<object?> LoadEntityAsync(IAsyncDocumentSession session, Type entityType, string id)
    {
        var method = typeof(IAsyncDocumentSession).GetMethod(nameof(IAsyncDocumentSession.LoadAsync), [typeof(string), typeof(CancellationToken)]);
        var genericMethod = method?.MakeGenericMethod(entityType);
        var task = genericMethod?.Invoke(session, [id, CancellationToken.None]) as Task;

        if (task == null) return null;

        await task;

        var resultProperty = task.GetType().GetProperty("Result");
        return resultProperty?.GetValue(task);
    }

    private async Task<object> SaveEntityViaActionsAsync(IAsyncDocumentSession session, Type entityType, object entity)
    {
        var actions = actionsResolver.ResolveForType(entityType);
        var onSaveMethod = actions.GetType().GetMethod("OnSaveAsync")!;
        var task = (Task)onSaveMethod.Invoke(actions, [session, entity])!;
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
