using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Reflection;
using Raven.Client;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;
using System.Collections;
using System.Text.Json;

namespace MintPlayer.Spark.Services;

/// <summary>
/// Binds every reference id a save writes to the collection its <c>[Reference]</c> declares (#251).
/// </summary>
/// <remarks>
/// <para>
/// A reference value is a bare id string, taken from the caller. RavenDB's typed load does not check
/// the collection, so a <c>Person</c>-typed <c>Driver</c> holding <c>accounts/1</c> later read that
/// account <i>as a person</i>. The breadcrumb resolver drops such a target on read; this is the write
/// half, so the value cannot be stored at all.
/// </para>
/// <para>
/// <b>Rejected:</b> an id that names no document in the target collection — a foreign document and a
/// missing one alike, with one message that names neither the id nor what was found. Rejecting only
/// foreign documents would make the answer an existence oracle for any document id in the database
/// (owner, 2026-11-09).
/// </para>
/// <para>
/// <b>Judged:</b> only ids the save newly writes. An id the stored row already holds for the same
/// target type passes untouched, so a row with a legacy dangling reference stays editable. References
/// inside AsDetail rows are judged the same way; the error names the top-level attribute.
/// </para>
/// <para>
/// <b>Not judged:</b> a target with no documents of its own — a type with no C# class or a
/// <c>[ValueObject]</c> (reference add-new PRD D10), whose key lives only in the owner's value — and an
/// attribute the caller may not write (its kept value is the stored one).
/// </para>
/// <para>
/// The check reads each candidate's <c>@collection</c> from an <b>untyped</b> load in a side session.
/// A typed load would deserialize a foreign document into the target type (and can throw on a field of
/// another shape); an untyped one materializes it as itself, or as a JObject when its
/// <c>@Raven-Clr-Type</c> is stale, which still carries the metadata. A document in the target
/// collection passes whatever its CLR type metadata says; one in another collection passes only as a
/// genuine subtype of the target, stored in that subtype's own collection.
/// </para>
/// </remarks>
internal interface IReferenceTargetValidation
{
    /// <summary>Throws <see cref="SparkSaveValidationException"/> naming every attribute whose new reference id is not a valid target.</summary>
    Task EnsureValidAsync(
        PersistentObject shielded, EntityTypeDefinition definition, object? stored, IReadOnlySet<string> unwritable,
        CancellationToken cancellationToken = default);
}

[Register(typeof(IReferenceTargetValidation), ServiceLifetime.Scoped)]
internal sealed partial class ReferenceTargetValidation : IReferenceTargetValidation
{
    [Inject] private readonly IDocumentStore documentStore;
    [Inject] private readonly IAsyncDocumentSession requestSession;
    [Inject] private readonly IModelLoader modelLoader;
    [Inject] private readonly ISparkTypeResolver typeResolver;

    public async Task EnsureValidAsync(
        PersistentObject shielded, EntityTypeDefinition definition, object? stored, IReadOnlySet<string> unwritable,
        CancellationToken cancellationToken = default)
    {
        // (top-level attribute, target type, id) for every id the posted object holds.
        var posted = new List<(string Attribute, Type Target, string Id)>();
        foreach (var attribute in definition.Attributes)
        {
            if (attribute.IsReadOnly || unwritable.Contains(attribute.Name))
                continue;
            var value = shielded.Attributes.FirstOrDefault(a => a.Name == attribute.Name);
            if (value is not null)
                CollectPosted(attribute.Name, attribute, value, posted);
        }
        if (posted.Count == 0)
            return;

        // What the stored row already holds, per target type: those ids are not the caller's new claim.
        var kept = new HashSet<(Type, string)>(KeyComparer.Instance);
        if (stored is not null)
            CollectStored(stored, definition, kept);

        var candidates = posted.Where(p => !kept.Contains((p.Target, p.Id))).ToList();
        if (candidates.Count == 0)
            return;

        // A target the request session already tracks — loaded, or stored by this request and not yet
        // committed (an Actions class creating the person and then the car, a write batch) — is judged
        // from that session, without a server call. Everything else from a side session, so nothing
        // this check loads (a foreign document, a stale-typed JObject) ends up tracked in the request.
        var ids = candidates.Select(c => c.Id).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var found = new Dictionary<string, (object Doc, IAsyncDocumentSession Session)>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in ids.Where(requestSession.Advanced.IsLoaded))
            if (await requestSession.LoadAsync<object>(id, cancellationToken) is { } tracked)
                found[id] = (tracked, requestSession);

        using var sideSession = documentStore.OpenAsyncSession();
        var remaining = ids.Where(id => !found.ContainsKey(id)).ToList();
        if (remaining.Count > 0)
            foreach (var (id, doc) in await sideSession.LoadAsync<object>(remaining, cancellationToken))
                if (doc is not null)
                    found[id] = (doc, sideSession);

        var result = new ValidationResult();
        foreach (var (attributeName, target, id) in candidates)
        {
            if (found.TryGetValue(id, out var hit) && IsInTargetCollection(hit.Session, hit.Doc, target))
                continue;
            if (result.Errors.Any(e => e.AttributeName == attributeName))
                continue;
            result.Errors.Add(new ValidationError
            {
                AttributeName = attributeName,
                RuleType = "referenceTarget",
                ErrorMessage = TranslatedString.Create(
                    $"{attributeName} refers to a record that does not exist.",
                    $"{attributeName} fait référence à un enregistrement qui n'existe pas.",
                    $"{attributeName} verwijst naar een record dat niet bestaat."),
            });
        }

        if (!result.IsValid)
            throw new SparkSaveValidationException(result);
    }

    private bool IsInTargetCollection(IAsyncDocumentSession session, object doc, Type target)
    {
        var conventions = documentStore.Conventions;
        if (!session.Advanced.GetMetadataFor(doc).TryGetValue(Constants.Documents.Metadata.Collection, out var raw)
            || raw?.ToString() is not { Length: > 0 } actual)
            return false;

        if (string.Equals(actual, conventions.GetCollectionName(target), StringComparison.Ordinal))
            return true;

        // A genuine subtype behind a base-typed edge lives in its own collection.
        var docType = doc.GetType();
        return docType != target
            && target.IsAssignableFrom(docType)
            && string.Equals(actual, conventions.GetCollectionName(docType), StringComparison.Ordinal);
    }

    /// <summary>The CLR type a reference attribute's ids must be documents of; null when it has no documents to check.</summary>
    private Type? StoredTargetType(EntityAttributeDefinition attribute)
    {
        if (attribute.DataType != "Reference" || string.IsNullOrEmpty(attribute.ReferenceType))
            return null;
        var target = typeResolver.Resolve(attribute.ReferenceType);
        return target is null || Abstractions.Model.SparkValueObjects.IsKeyed(target) ? null : target;
    }

    private EntityTypeDefinition? AsDetailDefinition(EntityAttributeDefinition attribute)
        => attribute.DataType == "AsDetail" && !string.IsNullOrEmpty(attribute.AsDetailType)
            ? modelLoader.GetEntityTypeByClrType(attribute.AsDetailType)
            : null;

    private void CollectPosted(
        string topLevel, EntityAttributeDefinition attribute, PersistentObjectAttribute value,
        List<(string, Type, string)> into)
    {
        if (StoredTargetType(attribute) is { } target)
        {
            foreach (var id in ExtractIds(value.Value))
                into.Add((topLevel, target, id));
            return;
        }

        if (value is not PersistentObjectAttributeAsDetail asDetail || AsDetailDefinition(attribute) is not { } childDef)
            return;
        IEnumerable<PersistentObject> rows = asDetail.IsArray
            ? asDetail.Objects ?? []
            : asDetail.Object is { } single ? [single] : [];
        foreach (var row in rows)
            foreach (var childAttribute in childDef.Attributes)
            {
                if (childAttribute.IsReadOnly)
                    continue;
                if (row.Attributes.FirstOrDefault(a => a.Name == childAttribute.Name) is { } childValue)
                    CollectPosted(topLevel, childAttribute, childValue, into);
            }
    }

    private void CollectStored(object entity, EntityTypeDefinition definition, HashSet<(Type, string)> into)
    {
        foreach (var attribute in definition.Attributes)
        {
            var property = entity.GetType().GetCachedProperty(attribute.Name);
            if (property is null || !property.CanRead)
                continue;
            var value = AccessorCache.GetGetter(property)(entity);

            if (StoredTargetType(attribute) is { } target)
            {
                foreach (var id in ExtractIds(value))
                    into.Add((target, id));
                continue;
            }

            if (AsDetailDefinition(attribute) is not { } childDef)
                continue;
            switch (value)
            {
                case null or string:
                    break;
                case IEnumerable rows:
                    foreach (var row in rows)
                        if (row is not null) CollectStored(row, childDef, into);
                    break;
                default:
                    CollectStored(value, childDef, into);
                    break;
            }
        }
    }

    /// <summary>The ids a reference value holds: one string, a JSON string or array, or a collection.</summary>
    private static IEnumerable<string> ExtractIds(object? value)
    {
        switch (value)
        {
            case null:
                yield break;
            case string s:
                if (!string.IsNullOrEmpty(s)) yield return s;
                yield break;
            case JsonElement { ValueKind: JsonValueKind.String } je:
                if (je.GetString() is { Length: > 0 } single) yield return single;
                yield break;
            case JsonElement { ValueKind: JsonValueKind.Array } array:
                foreach (var item in array.EnumerateArray())
                    if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } id)
                        yield return id;
                yield break;
            case JsonElement:
                yield break;
            case IEnumerable items:
                foreach (var item in items)
                    if (item?.ToString() is { Length: > 0 } id)
                        yield return id;
                yield break;
            default:
                // A complex-typed reference property holds the referenced entity itself; its id is the value.
                var idProperty = value.GetType().GetCachedProperty("Id");
                if (idProperty is not null && AccessorCache.GetGetter(idProperty)(value)?.ToString() is { Length: > 0 } entityId)
                    yield return entityId;
                yield break;
        }
    }

    /// <summary>Document ids are case-insensitive; the type half compares by identity.</summary>
    private sealed class KeyComparer : IEqualityComparer<(Type, string)>
    {
        public static readonly KeyComparer Instance = new();
        public bool Equals((Type, string) x, (Type, string) y)
            => x.Item1 == y.Item1 && StringComparer.OrdinalIgnoreCase.Equals(x.Item2, y.Item2);
        public int GetHashCode((Type, string) obj)
            => HashCode.Combine(obj.Item1, StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Item2));
    }
}
