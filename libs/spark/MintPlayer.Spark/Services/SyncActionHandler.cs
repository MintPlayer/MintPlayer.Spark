using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Abstractions.Interceptors;
using MintPlayer.Spark.Abstractions.Reflection;
using MintPlayer.Spark.Exceptions;
using Raven.Client.Documents;
using Sparrow.Json;
using System.Text.Json;


namespace MintPlayer.Spark.Services;

/// <summary>
/// Applies write-back edits on the owner module (warehouse §4.2.5). An edit is applied through
/// <see cref="ISparkReplacedSaves"/> — the path an approved change takes — as the requesting module's principal:
/// its type right, its attribute rights (F17; a module is evaluated like a person there), validation and the
/// interceptors, in one commit with the caller's own writes.
/// <para>
/// <b>Per-property compare-and-set</b> (owner decision 2026-10-20, replacing "the version stored now", which let a
/// stale replica edit overwrite newer owner changes): each listed property's stored value must still equal the base
/// the person started from. The comparison is on JSON in the store's own serialization, so both sides convert the
/// same way (Spark's converters included); collections and AsDetail values compare as whole values. The save then
/// carries the change vector the comparison read as its etag, so a write landing in between is compared again.
/// </para>
/// </summary>
[Register(typeof(ISyncActionHandler), ServiceLifetime.Scoped)]
internal partial class SyncActionHandler : ISyncActionHandler
{
    /// <summary>How often a compare is repeated when a write lands between it and the commit.</summary>
    internal const int MaxCompareRounds = 3;

    [Inject] private readonly IDocumentStore documentStore;
    [Inject] private readonly IModelLoader modelLoader;
    [Inject] private readonly IEntityMapper entityMapper;
    [Inject] private readonly ISparkReplacedSaves replacedSaves;
    [Inject] private readonly IDatabaseAccess databaseAccess;
    [Inject] private readonly ILogger<SyncActionHandler> logger;
    [Inject] private readonly Abstractions.Authentication.ISparkSyncInitiator? syncInitiator = null;

    public async Task<SyncEditOutcome> ApplyEditAsync(SyncEdit edit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(edit);
        if (edit.Values.Count == 0)
            return new SyncEditOutcome(SyncEditOutcomeKind.Refused, "The edit changes nothing.");

        var blind = edit.Values.Keys.Where(k => !edit.BaseValues.ContainsKey(k)).ToList();
        if (blind.Count > 0)
            return new SyncEditOutcome(SyncEditOutcomeKind.Refused, $"No base value for {string.Join(", ", blind)}.") { Attributes = blind };

        var entityTypeDef = FindEntityTypeDefinition(edit.EntityType);
        if (entityTypeDef is null)
            return new SyncEditOutcome(SyncEditOutcomeKind.Refused, new SparkSyncNotAuthorizableException(edit.EntityType.Name).Message);

        var data = edit.Values.ToDictionary(kv => kv.Key, kv => (object?)kv.Value, StringComparer.OrdinalIgnoreCase);

        for (var round = 1; ; round++)
        {
            // The base check: the row as stored now, compared per listed property with what the person started from.
            var (baseEtag, conflict) = await CompareAsync(edit.EntityType, edit.DocumentId, edit.BaseValues, edit.Values.Keys, cancellationToken);
            if (conflict is not null)
                return conflict;

            var po = BuildPersistentObject(edit.EntityType, edit.DocumentId, data, [.. edit.Values.Keys]);
            EnsureAuthorizable(po, edit.EntityType.Name);

            // A name the model does not know would be dropped silently by the partial build: refuse instead, so the
            // requester learns the edit was not what it asked for.
            var unknown = edit.Values.Keys.Where(k => !po.Attributes.Any(a => string.Equals(a.Name, k, StringComparison.OrdinalIgnoreCase))).ToList();
            if (unknown.Count > 0)
                return new SyncEditOutcome(SyncEditOutcomeKind.Refused, $"Unknown attribute(s) on {entityTypeDef.Name}: {string.Join(", ", unknown)}.") { Attributes = unknown };

            po.Etag = baseEtag;

            var outcome = await SaveAsync(edit, entityTypeDef, po, baseEtag, cancellationToken);

            // Changed between the compare and the commit: compare again against the new version, a bounded number of times.
            if (outcome is { Kind: SyncEditOutcomeKind.Conflict, Reason: SparkConflictException.Changed, Attributes.Count: 0 } && round < MaxCompareRounds)
                continue;

            logger.LogInformation("Write-back on {Type}/{DocumentId} ({Attributes}): {Outcome}",
                entityTypeDef.Name, edit.DocumentId, string.Join(", ", edit.Values.Keys), outcome.Kind);
            return outcome;
        }
    }

    public async Task<SyncEditOutcome> ApplyDeleteAsync(SyncDelete delete, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(delete);
        var entityTypeDef = FindEntityTypeDefinition(delete.EntityType);
        if (entityTypeDef is null)
            return new SyncEditOutcome(SyncEditOutcomeKind.Refused, new SparkSyncNotAuthorizableException(delete.EntityType.Name).Message);

        var (baseEtag, conflict) = await CompareAsync(delete.EntityType, delete.DocumentId, delete.BaseValues, delete.BaseValues.Keys, cancellationToken);
        if (conflict is not null)
            return conflict;

        try
        {
            using var initiator = (syncInitiator as SparkSyncInitiator)?.Begin(delete.InitiatorId);
            await databaseAccess.DeletePersistentObjectAsync(entityTypeDef.Id, delete.DocumentId, PersistentObjectOperation.Delete, baseEtag);
            logger.LogInformation("Write-back delete of {Type}/{DocumentId}: Applied", entityTypeDef.Name, delete.DocumentId);
            return new SyncEditOutcome(SyncEditOutcomeKind.Applied);
        }
        catch (Exception ex) when (Refusal(ex) is { } refusal)
        {
            return refusal;
        }
    }

    /// <summary>
    /// Loads the stored row and compares each of <paramref name="keys"/> with its base. Returns the change vector the
    /// comparison read (the etag the save must find), or the conflict.
    /// </summary>
    private async Task<(string? Etag, SyncEditOutcome? Conflict)> CompareAsync(
        Type entityType, string documentId, IReadOnlyDictionary<string, JsonElement> baseValues, IEnumerable<string> keys, CancellationToken cancellationToken)
    {
        using var session = documentStore.OpenAsyncSession();
        var stored = await session.LoadAsync<object>(documentId, cancellationToken);
        // A row deleted here is a conflict, never recreated from the replica's copy.
        if (stored is null)
            return (null, new SyncEditOutcome(SyncEditOutcomeKind.Conflict, SparkConflictException.Deleted));
        if (!entityType.IsInstanceOfType(stored))
            return (null, new SyncEditOutcome(SyncEditOutcomeKind.Refused, $"'{documentId}' is not a {entityType.Name}."));

        using var current = StoredJson(stored);
        var conflicting = new List<string>();
        foreach (var key in keys)
        {
            var now = TryGetProperty(current.RootElement, key, out var value) ? value : default;
            if (!SameValue(now, baseValues[key]))
                conflicting.Add(key);
        }

        return conflicting.Count > 0
            ? (null, new SyncEditOutcome(SyncEditOutcomeKind.Conflict, SparkConflictException.Changed) { Attributes = conflicting })
            : (session.Advanced.GetChangeVectorFor(stored), null);
    }

    private async Task<SyncEditOutcome> SaveAsync(SyncEdit edit, EntityTypeDefinition entityTypeDef, PersistentObject po, string? baseEtag, CancellationToken cancellationToken)
    {
        try
        {
            // The consumer's user, for the owner's stamping interceptors (#271, F2): the caller here is a
            // module certificate, so ISparkCurrentUser has no id to give them.
            using var initiator = (syncInitiator as SparkSyncInitiator)?.Begin(edit.InitiatorId);
            var outcome = await replacedSaves.ApplyAsync(new ReplacedSave
            {
                ObjectTypeId = entityTypeDef.Id,
                PersistentObjectJson = SparkReplacedSaves.Serialize(po),
                BaseEtag = baseEtag,
                OnBehalfOf = edit.InitiatorId,
                Alongside = edit.Alongside,
            }, cancellationToken);

            return outcome.Kind switch
            {
                ReplacedSaveOutcomeKind.Applied => new SyncEditOutcome(SyncEditOutcomeKind.Applied),
                ReplacedSaveOutcomeKind.Conflict => new SyncEditOutcome(SyncEditOutcomeKind.Conflict, outcome.ConflictReason ?? SparkConflictException.Changed),
                ReplacedSaveOutcomeKind.Unwritable => new SyncEditOutcome(SyncEditOutcomeKind.Refused,
                    $"The module may not write {string.Join(", ", outcome.Attributes)}.") { Attributes = outcome.Attributes, Refusal = SyncEditRefusal.Forbidden },
                ReplacedSaveOutcomeKind.Invalid => new SyncEditOutcome(SyncEditOutcomeKind.Refused,
                    "Invalid: " + string.Join(", ", outcome.Errors.Select(e => $"{e.AttributeName} ({e.RuleType})")))
                    { Attributes = [.. outcome.Errors.Select(e => e.AttributeName)], Refusal = SyncEditRefusal.Invalid },
                ReplacedSaveOutcomeKind.Cancelled => new SyncEditOutcome(SyncEditOutcomeKind.Refused, "cancelled by the owner"),
                _ => new SyncEditOutcome(SyncEditOutcomeKind.Refused, outcome.Kind.ToString()),
            };
        }
        catch (Exception ex) when (Refusal(ex) is { } refusal)
        {
            return refusal;
        }
    }

    /// <summary>The outcome a refusal the save path throws maps to, or null for a fault (rethrown).</summary>
    private static SyncEditOutcome? Refusal(Exception exception) => exception switch
    {
        SparkConflictException conflict => new SyncEditOutcome(SyncEditOutcomeKind.Conflict, conflict.Reason) { Attributes = conflict.Attributes },
        // Uniform with the HTTP path: a row the module may not edit is "not found" to it.
        SparkRowLevelAccessDeniedException => new SyncEditOutcome(SyncEditOutcomeKind.Refused, "not found") { Refusal = SyncEditRefusal.Forbidden },
        SparkAccessDeniedException denied => new SyncEditOutcome(SyncEditOutcomeKind.Refused, $"Forbidden: {denied.Resource}") { Refusal = SyncEditRefusal.Forbidden },
        SparkActionDisabledException disabled => new SyncEditOutcome(SyncEditOutcomeKind.Refused, disabled.Message) { Refusal = SyncEditRefusal.Forbidden },
        SparkValidationException invalid => new SyncEditOutcome(SyncEditOutcomeKind.Refused, invalid.Message)
        {
            Attributes = invalid.AttributeName is { Length: > 0 } name ? [name] : [],
            Refusal = SyncEditRefusal.Invalid,
        },
        SparkCancelException => new SyncEditOutcome(SyncEditOutcomeKind.Refused, "cancelled by the owner"),
        _ => null,
    };

    /// <summary><paramref name="entity"/> as the store would write it (its conventions and converters), without metadata.</summary>
    private JsonDocument StoredJson(object entity)
    {
        using var context = JsonOperationContext.ShortTermSingleUse();
        using var blittable = documentStore.Conventions.Serialization.DefaultConverter.ToBlittable(entity, context);
        return JsonDocument.Parse(blittable.ToString());
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.TryGetProperty(name, out value))
            return true;
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }
        value = default;
        return false;
    }

    /// <summary>JSON equality where an absent value (<c>default</c>/<c>Undefined</c>) equals <c>null</c>.</summary>
    internal static bool SameValue(JsonElement stored, JsonElement baseValue)
    {
        static bool IsNull(JsonElement e) => e.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null;
        if (IsNull(stored) || IsNull(baseValue))
            return IsNull(stored) && IsNull(baseValue);
        return JsonElement.DeepEquals(stored, baseValue);
    }


    /// <summary>
    /// Refuses a sync action against a type with no registered <see cref="EntityTypeDefinition"/>.
    /// <para>
    /// Such a type has no name for <c>security.json</c> to grant rights on, so no authorization
    /// decision can be made about it. It fails closed: <b>unevaluable is not permitted</b>.
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
    /// schema path runs through <see cref="ISystemManager.GetPersistentObject(Guid)"/> (system construction), so
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
        // A system construction (D13a, S5): replication writes every synced field, and pruning for a
        // viewer would drop the ones that viewer may not read.
        var po = entityMapper.AsSystem().GetPersistentObject(entityTypeDef.Id);
        po.Id = documentId;

        // A partial update must carry ONLY the attributes it updates. The scaffold starts with
        // every attribute in the model, and the write path (EntityMapper.PopulateObjectValues*)
        // writes every attribute it is given — it does not consult IsValueChanged — so leaving
        // the unnamed ones in place with a null value would blank out stored data the sender
        // never mentioned. See SyncAction.Properties: "only these properties are merged".
        if (propertySet is not null)
            po.RetainAttributes(a => propertySet.Contains(a.Name));

        // Satellite attributes (contributions F2) are never posted by a sync, full or partial: their
        // values live outside the replicated document, so the payload cannot carry them, and a full
        // sync would otherwise turn the missing value into an empty collection — withdrawing every row.
        var satellites = entityType.GetSparkSatellitePropertyNames();
        if (satellites.Count > 0)
            po.RetainAttributes(a => !satellites.Contains(a.Name));

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

            // Nor a satellite (contributions F2): its value is not the replicated document's.
            if (prop.IsSparkSatelliteProperty())
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
                // Objects and arrays keep their JSON structure (F18): the save path binds them as it binds a posted object.
                _ => element.Clone()
            };
        }

        // Already a .NET primitive (from Newtonsoft/RavenDB deserialization or direct assignment)
        return value;
    }
}
