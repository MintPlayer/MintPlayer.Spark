using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Messaging.Abstractions;
using MintPlayer.Spark.Replication.Abstractions;
using MintPlayer.Spark.Replication.Abstractions.Configuration;
using MintPlayer.Spark.Replication.Abstractions.Models;
using MintPlayer.Spark.Replication.Messages;

namespace MintPlayer.Spark.Replication.Services;

/// <summary>
/// Intercepts write operations on replicated entities and dispatches them
/// to the owner module via the durable message bus.
/// </summary>
internal class SyncActionInterceptor : ISyncActionInterceptor
{
    private readonly IMessageBus _messageBus;
    private readonly SparkReplicationOptions _options;
    private readonly ILogger<SyncActionInterceptor> _logger;

    // Cache: CLR type → ReplicatedAttribute (null means not replicated)
    private static readonly ConcurrentDictionary<Type, ReplicatedAttribute?> _replicatedCache = new();

    // Cache: CLR type → property names (excluding Id) for partial updates
    private static readonly ConcurrentDictionary<Type, string[]> _propertyNamesCache = new();

    public SyncActionInterceptor(
        IMessageBus messageBus,
        IOptions<SparkReplicationOptions> options,
        ILogger<SyncActionInterceptor> logger)
    {
        _messageBus = messageBus;
        _options = options.Value;
        _logger = logger;
    }

    public bool IsReplicated(Type entityType)
    {
        return GetReplicatedAttribute(entityType) != null;
    }

    public async Task HandleSaveAsync(object entity, string? documentId)
    {
        var entityType = entity.GetType();
        var attr = GetReplicatedAttribute(entityType)
            ?? throw new InvalidOperationException($"Type {entityType.Name} is not a replicated entity.");

        var collection = attr.SourceCollection ?? InferCollectionName(attr.OriginalType ?? entityType);
        var actionType = documentId == null ? SyncActionType.Insert : SyncActionType.Update;

        // Auto-populate Properties from the replicated entity type.
        // The replicated type only has a subset of properties (the ones in the ETL script),
        // so we only send those back — preventing overwrite of properties the owner has
        // but the non-owner doesn't replicate.
        var properties = GetPropertyNames(entityType);

        var syncAction = new SyncAction
        {
            ActionType = actionType,
            Collection = collection,
            DocumentId = documentId,
            Data = JsonSerializer.SerializeToElement(entity),
            Properties = properties,
        };

        await DispatchAsync(attr.SourceModule, collection, syncAction);

        _logger.LogInformation(
            "Dispatched {ActionType} sync action for {Collection} (ID: {DocumentId}, {PropertyCount} properties) to owner module '{OwnerModule}'",
            actionType, collection, documentId ?? "(new)", properties.Length, attr.SourceModule);
    }

    public async Task HandleDeleteAsync(Type entityType, string documentId)
    {
        var attr = GetReplicatedAttribute(entityType)
            ?? throw new InvalidOperationException($"Type {entityType.Name} is not a replicated entity.");

        var collection = attr.SourceCollection ?? InferCollectionName(attr.OriginalType ?? entityType);

        var syncAction = new SyncAction
        {
            ActionType = SyncActionType.Delete,
            Collection = collection,
            DocumentId = documentId,
        };

        await DispatchAsync(attr.SourceModule, collection, syncAction);

        _logger.LogInformation(
            "Dispatched Delete sync action for {Collection}/{DocumentId} to owner module '{OwnerModule}'",
            collection, documentId, attr.SourceModule);
    }

    private async Task DispatchAsync(string ownerModuleName, string collection, SyncAction action)
    {
        var request = new SyncActionRequest
        {
            RequestingModule = _options.ModuleName,
            Actions = [action],
        };

        var message = new SyncActionDeploymentMessage
        {
            OwnerModuleName = ownerModuleName,
            Request = request,
        };

        // Use per-collection queue for isolation
        var queueName = $"spark-sync-{collection}";
        await _messageBus.BroadcastAsync(message, queueName);
    }

    private static ReplicatedAttribute? GetReplicatedAttribute(Type type)
    {
        return _replicatedCache.GetOrAdd(type, t => t.GetCustomAttribute<ReplicatedAttribute>());
    }

    /// <summary>
    /// Gets the property names from the replicated entity type, excluding "Id".
    /// These are the only properties that should be synced back to the owner,
    /// since the replicated type only contains the subset of fields from the ETL script.
    /// </summary>
    private static string[] GetPropertyNames(Type entityType)
    {
        return _propertyNamesCache.GetOrAdd(entityType, t =>
            t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.CanRead && p.CanWrite && !string.Equals(p.Name, "Id", StringComparison.Ordinal))
                .Select(p => p.Name)
                .ToArray());
    }

    /// <summary>
    /// Infers RavenDB collection name from a CLR type using the default pluralization convention.
    /// Matches the logic in EtlScriptCollector.
    /// </summary>
    private static string InferCollectionName(Type type)
    {
        var name = type.Name;

        if (name.EndsWith("y", StringComparison.Ordinal)
            && !name.EndsWith("ey", StringComparison.Ordinal)
            && !name.EndsWith("ay", StringComparison.Ordinal)
            && !name.EndsWith("oy", StringComparison.Ordinal))
            return name[..^1] + "ies";

        if (name.EndsWith("s", StringComparison.Ordinal)
            || name.EndsWith("x", StringComparison.Ordinal)
            || name.EndsWith("sh", StringComparison.Ordinal)
            || name.EndsWith("ch", StringComparison.Ordinal))
            return name + "es";

        return name + "s";
    }
}
