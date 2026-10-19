using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Abstractions.Interceptors;
using MintPlayer.Spark.Abstractions.Reflection;
using MintPlayer.Spark.Exceptions;
using Raven.Client.Documents;
using System.Text.Json;


namespace MintPlayer.Spark.Services;

/// <summary>
/// Applies write-back edits on the owner module (warehouse WX3). An edit is applied through
/// <see cref="ISparkReplacedSaves"/> — the path an approved change takes — as the requesting module's principal:
/// its type right, its attribute rights (F17; a module is evaluated like a person there), validation and the
/// interceptors, against the version stored now, in one commit with the caller's own writes.
/// </summary>
[Register(typeof(ISyncActionHandler), ServiceLifetime.Scoped)]
internal partial class SyncActionHandler : ISyncActionHandler
{
    [Inject] private readonly IDocumentStore documentStore;
    [Inject] private readonly IModelLoader modelLoader;
    [Inject] private readonly IEntityMapper entityMapper;
    [Inject] private readonly ISparkReplacedSaves replacedSaves;
    [Inject] private readonly ILogger<SyncActionHandler> logger;
    [Inject] private readonly Abstractions.Authentication.ISparkSyncInitiator? syncInitiator = null;

    public async Task<SyncEditOutcome> ApplyEditAsync(SyncEdit edit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(edit);
        if (edit.Values.Count == 0)
            return new SyncEditOutcome(SyncEditOutcomeKind.Refused, "The edit changes nothing.");

        var entityTypeDef = FindEntityTypeDefinition(edit.EntityType);
        if (entityTypeDef is null)
            return new SyncEditOutcome(SyncEditOutcomeKind.Refused, new SparkSyncNotAuthorizableException(edit.EntityType.Name).Message);

        // The version stored now is the base: the replica's own version means nothing to this module (#467, D15).
        // A row deleted here is refused, never recreated from the replica's copy.
        string? baseEtag;
        using (var checkSession = documentStore.OpenAsyncSession())
        {
            var stored = await checkSession.LoadAsync<object>(edit.DocumentId, cancellationToken);
            if (stored is null)
                return new SyncEditOutcome(SyncEditOutcomeKind.Refused, "deleted");
            baseEtag = checkSession.Advanced.GetChangeVectorFor(stored);
        }

        var data = new Dictionary<string, object?>(edit.Values, StringComparer.OrdinalIgnoreCase);
        var po = BuildPersistentObject(edit.EntityType, edit.DocumentId, data, [.. edit.Values.Keys]);
        EnsureAuthorizable(po, edit.EntityType.Name);

        // A name the model does not know would be dropped silently by the partial build: refuse instead, so the
        // requester learns the edit was not what it asked for.
        var unknown = edit.Values.Keys.Where(k => !po.Attributes.Any(a => string.Equals(a.Name, k, StringComparison.OrdinalIgnoreCase))).ToList();
        if (unknown.Count > 0)
            return new SyncEditOutcome(SyncEditOutcomeKind.Refused, $"Unknown attribute(s) on {entityTypeDef.Name}: {string.Join(", ", unknown)}.") { Attributes = unknown };

        po.Etag = baseEtag;

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

            logger.LogInformation("Write-back on {Type}/{DocumentId} ({Attributes}): {Outcome}",
                entityTypeDef.Name, edit.DocumentId, string.Join(", ", edit.Values.Keys), outcome.Kind);

            return outcome.Kind switch
            {
                ReplacedSaveOutcomeKind.Applied => new SyncEditOutcome(SyncEditOutcomeKind.Applied),
                ReplacedSaveOutcomeKind.Conflict when outcome.ConflictReason == "deleted"
                    => new SyncEditOutcome(SyncEditOutcomeKind.Refused, "deleted"),
                ReplacedSaveOutcomeKind.Conflict => new SyncEditOutcome(SyncEditOutcomeKind.Conflict, outcome.ConflictReason ?? "changed"),
                ReplacedSaveOutcomeKind.Unwritable => new SyncEditOutcome(SyncEditOutcomeKind.Refused,
                    $"The module may not write {string.Join(", ", outcome.Attributes)}.") { Attributes = outcome.Attributes },
                ReplacedSaveOutcomeKind.Invalid => new SyncEditOutcome(SyncEditOutcomeKind.Refused,
                    "Invalid: " + string.Join(", ", outcome.Errors.Select(e => $"{e.AttributeName} ({e.RuleType})"))) { Attributes = [.. outcome.Errors.Select(e => e.AttributeName)] },
                ReplacedSaveOutcomeKind.Cancelled => new SyncEditOutcome(SyncEditOutcomeKind.Refused, "cancelled by the owner"),
                _ => new SyncEditOutcome(SyncEditOutcomeKind.Refused, outcome.Kind.ToString()),
            };
        }
        catch (SparkConcurrencyException ex)
        {
            return ex.Reason == "deleted"
                ? new SyncEditOutcome(SyncEditOutcomeKind.Refused, "deleted")
                : new SyncEditOutcome(SyncEditOutcomeKind.Conflict, ex.Reason);
        }
        catch (SparkRowLevelAccessDeniedException)
        {
            // Uniform with the HTTP path: a row the module may not edit is "not found" to it.
            return new SyncEditOutcome(SyncEditOutcomeKind.Refused, "not found");
        }
        catch (SparkAccessDeniedException ex)
        {
            return new SyncEditOutcome(SyncEditOutcomeKind.Refused, $"Forbidden: {ex.Resource}");
        }
        catch (SparkActionDisabledException ex)
        {
            return new SyncEditOutcome(SyncEditOutcomeKind.Refused, ex.Message);
        }
        catch (SparkValidationException ex)
        {
            return new SyncEditOutcome(SyncEditOutcomeKind.Refused, ex.Message);
        }
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
