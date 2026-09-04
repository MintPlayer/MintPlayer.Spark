using Microsoft.Extensions.Options;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Messaging.Abstractions;
using MintPlayer.Spark.Replication.Messages;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Reflection;
using MintPlayer.Spark.Replication.Abstractions;
using MintPlayer.Spark.Replication.Abstractions.Configuration;
using MintPlayer.Spark.Replication.Abstractions.Models;
using Raven.Client.Documents;
using System.Text.Json;

namespace MintPlayer.Spark.Replication.Services;

/// <summary>
/// Intercepts writes to replicated entities and broadcasts each one to the module that owns the
/// collection, on the <c>spark-sync</c> lane, ordered per document.
/// </summary>
internal partial class SyncActionInterceptor : ISyncActionInterceptor
{
    [Inject] private readonly IMessageBus messageBus;
    [Inject] private readonly IOptions<SparkReplicationOptions> optionsAccessor;
    [Inject] private readonly ILogger<SyncActionInterceptor> logger;

    private SparkReplicationOptions Options => optionsAccessor.Value;

    public bool IsReplicated(Type entityType)
    {
        return GetReplicatedAttribute(entityType) != null;
    }

    public async Task HandleSaveAsync(Type entityType, PersistentObject obj)
    {
        var attr = GetReplicatedAttribute(entityType)
            ?? throw new InvalidOperationException($"Type {entityType.Name} is not a replicated entity.");

        var collection = attr.SourceCollection ?? InferCollectionName(attr.OriginalType ?? entityType);
        var actionType = obj.Id == null ? SyncActionType.Insert : SyncActionType.Update;

        // These attributes come from the client, so an [IgnoreProperty] name could be posted
        // even though it is not part of the model. Drop it here rather than trusting the input:
        // it would otherwise be transmitted AND listed as writable on the owner module.
        var ignoredNames = entityType.GetCachedProperties()
            .Where(p => p.IsIgnoredForSparkModel())
            .Select(p => p.Name)
            .ToHashSet(StringComparer.Ordinal);

        // Use IsValueChanged from PO attributes to determine which properties changed
        var changedProperties = obj.Attributes
            .Where(a => a.IsValueChanged && !ignoredNames.Contains(a.Name))
            .Select(a => a.Name)
            .ToArray();

        // If no attributes are marked as changed, fall back to all replicated properties
        if (changedProperties.Length == 0)
        {
            changedProperties = GetPropertyNames(entityType);
        }

        // Build data from PO attributes, normalizing any JsonElement values to plain .NET types
        var data = new Dictionary<string, object?>();
        foreach (var attribute in obj.Attributes)
        {
            if (ignoredNames.Contains(attribute.Name)) continue;
            data[attribute.Name] = NormalizeValue(attribute.Value);
        }
        if (obj.Id != null)
        {
            data["Id"] = obj.Id;
        }

        var syncAction = new SyncAction
        {
            ActionType = actionType,
            Collection = collection,
            DocumentId = obj.Id,
            Data = data,
            Properties = changedProperties,
        };

        await DispatchAsync(attr.SourceModule, collection, syncAction);

        logger.LogInformation(
            "Dispatched {ActionType} sync action for {Collection} (ID: {DocumentId}, {PropertyCount} changed properties) to owner module '{OwnerModule}'",
            actionType, collection, obj.Id ?? "(new)", changedProperties.Length, attr.SourceModule);
    }

    public async Task HandleSaveAsync(object entity, string? documentId)
    {
        var entityType = entity.GetType();
        var attr = GetReplicatedAttribute(entityType)
            ?? throw new InvalidOperationException($"Type {entityType.Name} is not a replicated entity.");

        var collection = attr.SourceCollection ?? InferCollectionName(attr.OriginalType ?? entityType);
        var actionType = documentId == null ? SyncActionType.Insert : SyncActionType.Update;

        // Auto-populate Properties from the replicated entity type (all properties, no change tracking)
        var properties = GetPropertyNames(entityType);

        var data = new Dictionary<string, object?>();
        foreach (var prop in entityType.GetCachedProperties())
        {
            // Ignored properties are not part of the model, so they are not transmitted
            // cross-module either.
            if (prop.CanRead && !prop.IsIgnoredForSparkModel())
                data[prop.Name] = NormalizeValue(AccessorCache.GetGetter(prop)(entity));
        }

        var syncAction = new SyncAction
        {
            ActionType = actionType,
            Collection = collection,
            DocumentId = documentId,
            Data = data,
            Properties = properties,
        };

        await DispatchAsync(attr.SourceModule, collection, syncAction);

        logger.LogInformation(
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

        logger.LogInformation(
            "Dispatched Delete sync action for {Collection}/{DocumentId} to owner module '{OwnerModule}'",
            collection, documentId, attr.SourceModule);
    }

    private Task DispatchAsync(string ownerModuleName, string collection, SyncAction action)
        // Was a bespoke SparkSyncAction document drained by its own subscription, its own sweeper and
        // its own retry engine. It is a message, so it is now sent as one: retry, backoff,
        // dead-lettering and retention come from messaging, and replication keeps only the part that
        // was ever its own — where to POST it and which HTTP results deserve another attempt.
        => messageBus.BroadcastAsync(new SyncActionMessage
        {
            OwnerModuleName = ownerModuleName,
            RequestingModule = Options.ModuleName,
            Collection = collection,
            // The ordering domain. Writes to one document reach the owner in the order they were
            // made; writes to different documents never wait for each other.
            DocumentId = action.DocumentId ?? string.Empty,
            Actions = [action],
        });

    /// <summary>
    /// Converts JsonElement values (from Spark's JSON deserialization) to plain .NET types
    /// so they can be safely serialized by both Newtonsoft.Json (RavenDB) and System.Text.Json (HTTP).
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
                _ => element.ToString(),
            };
        }

        return value;
    }

    private static ReplicatedAttribute? GetReplicatedAttribute(Type type)
        => type.GetCachedCustomAttribute<ReplicatedAttribute>();

    /// <summary>
    /// Gets the property names from the replicated entity type, excluding "Id" and anything
    /// marked <c>[IgnoreProperty]</c>. These are the only properties that should be synced back
    /// to the owner, since the replicated type only contains the subset of fields from the ETL
    /// script. This list is the owner module's write authorization, so an excluded property must
    /// not appear in it.
    /// </summary>
    private static string[] GetPropertyNames(Type entityType)
    {
        return ReflectionCache.GetOrAdd<(string Op, Type Type), string[]>(
            ("SyncActionInterceptor.ReplicatedPropNames", entityType),
            // Writable, not merely "in the model": this list is a write authorization, so a
            // get-only computed property must never reach it. Those appear in the model as
            // read-only attributes and are not writable by anyone, least of all a peer module.
            static k => k.Type.GetSparkWritableProperties()
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
