using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;

namespace MintPlayer.Spark.Services;

/// <summary>
/// Applies the caller's static attribute rights (PRD §5 Q14) to what the read paths put on the wire:
/// an attribute the caller may not <c>Read</c> is <b>absent</b> from a persistent object and from the
/// caller's entity-type definition, one they may not <c>Query</c> is absent from the query surface,
/// and one they may not <c>Edit</c> (or <c>New</c>) is read-only.
/// </summary>
/// <remarks>
/// <para>
/// <b>Absent, not blanked.</b> A static right does not vary per row, so removing the attribute
/// discloses nothing a row could not: every row of the type is missing the same attribute for the
/// same caller. That is the difference from the per-row <c>GetProtectedAttributesAsync</c> hook,
/// which keeps the attribute and empties its value, because a per-row absence would itself be the
/// signal.
/// </para>
/// <para>
/// One decision per (type, verb) per request — <see cref="IAttributeRights"/> memoises it, and the
/// denied sets are memoised here — then applied to every row. Nothing here asks per row.
/// </para>
/// <para>
/// Only attributes an attribute-level right <em>mentions</em> are ever removed. The type-level right
/// was decided before any of this runs (a caller without <c>Read/T</c> never reaches a presentation
/// of a <c>T</c>), and an AsDetail row type usually holds no type right of its own — the parent's
/// governs — so treating "type not granted" as "every attribute denied" would empty every embedded
/// row.
/// </para>
/// </remarks>
internal interface IAttributeRightsEnforcement
{
    /// <summary>
    /// The attributes of <paramref name="definition"/> the caller is refused for
    /// <paramref name="verb"/>, in the model's spelling, compared case-insensitively. Empty in
    /// system context and when no attribute right mentions the type.
    /// </summary>
    Task<IReadOnlySet<string>> GetDeniedAsync(EntityTypeDefinition definition, string verb, CancellationToken cancellationToken = default);

    /// <summary>
    /// The caller's query surface: <paramref name="definition"/> without its <c>Query</c>-denied
    /// attributes, so columns, search fields, sort, filter and distincts treat them as if they did
    /// not exist. The same reference when nothing is denied; otherwise a copy — never a mutation of
    /// the loader's shared definition.
    /// </summary>
    Task<EntityTypeDefinition> ForQueryAsync(EntityTypeDefinition definition, CancellationToken cancellationToken = default);

    /// <summary>
    /// The caller's form definition (<c>EntityTypes/Get|List</c>): <c>Read</c>-denied attributes
    /// removed and <c>Edit</c>-denied ones read-only, on the type and on every embedded detail type
    /// by that type's own rights. The same reference when nothing changes.
    /// </summary>
    Task<EntityTypeDefinition> ForFormAsync(EntityTypeDefinition definition, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes the attributes denied for <paramref name="removeVerb"/> from each object and marks
    /// those denied for <paramref name="readOnlyVerb"/> read-only, recursing into AsDetail rows (each
    /// judged by its own row type's rights).
    /// </summary>
    Task PresentAsync(
        IEnumerable<PersistentObject> objects, string removeVerb, string? readOnlyVerb = null,
        CancellationToken cancellationToken = default);
}

[Register(typeof(IAttributeRightsEnforcement), ServiceLifetime.Scoped)]
internal sealed partial class AttributeRightsEnforcement : IAttributeRightsEnforcement
{
    [Inject] private readonly IAttributeRights attributeRights;
    [Inject] private readonly IModelLoader modelLoader;

    private static readonly IReadOnlySet<string> None = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>AsDetail nests; a cycle of row types is stopped by the depth, as in the mapper.</summary>
    private const int MaxDepth = 8;

    private readonly Dictionary<(Guid Type, string Verb), IReadOnlySet<string>> denied = [];

    public async Task<IReadOnlySet<string>> GetDeniedAsync(
        EntityTypeDefinition definition, string verb, CancellationToken cancellationToken = default)
    {
        if (denied.TryGetValue((definition.Id, verb), out var cached))
            return cached;

        var rights = await attributeRights.GetEffectiveAsync(definition, verb, cancellationToken);
        var set = rights.IsUnrestricted
            ? None
            : rights.DeniedAttributes.ToHashSet(StringComparer.OrdinalIgnoreCase);
        IReadOnlySet<string> result = set.Count == 0 ? None : set;

        denied[(definition.Id, verb)] = result;
        return result;
    }

    public async Task<EntityTypeDefinition> ForQueryAsync(
        EntityTypeDefinition definition, CancellationToken cancellationToken = default)
    {
        var refused = await GetDeniedAsync(definition, SparkCoreActions.Query, cancellationToken);
        if (refused.Count == 0)
            return definition;

        var copy = definition.ShallowCopy();
        copy.Attributes = [.. definition.Attributes.Where(a => !refused.Contains(a.Name))];
        return copy;
    }

    public async Task<EntityTypeDefinition> ForFormAsync(
        EntityTypeDefinition definition, CancellationToken cancellationToken = default)
    {
        var result = await PruneForFormAsync(definition, cancellationToken);

        if (definition.DetailTypes is { Length: > 0 } detailTypes)
        {
            var pruned = new EntityTypeDefinition[detailTypes.Length];
            var changed = false;
            for (var i = 0; i < detailTypes.Length; i++)
            {
                pruned[i] = await PruneForFormAsync(detailTypes[i], cancellationToken);
                changed |= !ReferenceEquals(pruned[i], detailTypes[i]);
            }

            if (changed)
            {
                if (ReferenceEquals(result, definition))
                    result = definition.ShallowCopy();
                result.DetailTypes = pruned;
            }
        }

        return result;
    }

    private async Task<EntityTypeDefinition> PruneForFormAsync(
        EntityTypeDefinition definition, CancellationToken cancellationToken)
    {
        var unreadable = await GetDeniedAsync(definition, SparkCoreActions.Read, cancellationToken);
        var uneditable = await GetDeniedAsync(definition, SparkCoreActions.Edit, cancellationToken);
        if (unreadable.Count == 0 && uneditable.Count == 0)
            return definition;

        var attributes = new List<EntityAttributeDefinition>(definition.Attributes.Length);
        foreach (var attribute in definition.Attributes)
        {
            if (unreadable.Contains(attribute.Name))
                continue;

            if (uneditable.Contains(attribute.Name) && !attribute.IsReadOnly)
            {
                var readOnly = attribute.ShallowCopy();
                readOnly.IsReadOnly = true;
                attributes.Add(readOnly);
            }
            else
            {
                attributes.Add(attribute);
            }
        }

        var copy = definition.ShallowCopy();
        copy.Attributes = [.. attributes];
        return copy;
    }

    public async Task PresentAsync(
        IEnumerable<PersistentObject> objects, string removeVerb, string? readOnlyVerb = null,
        CancellationToken cancellationToken = default)
    {
        foreach (var po in objects)
            await PresentOneAsync(po, removeVerb, readOnlyVerb, depth: 0, cancellationToken);
    }

    private async Task PresentOneAsync(
        PersistentObject po, string removeVerb, string? readOnlyVerb, int depth, CancellationToken cancellationToken)
    {
        if (depth > MaxDepth)
            return;

        if (modelLoader.GetEntityType(po.ObjectTypeId) is { } definition)
        {
            var refused = await GetDeniedAsync(definition, removeVerb, cancellationToken);
            if (refused.Count > 0)
                po.RetainAttributes(a => !refused.Contains(a.Name));

            if (readOnlyVerb is not null)
            {
                var readOnly = await GetDeniedAsync(definition, readOnlyVerb, cancellationToken);
                if (readOnly.Count > 0)
                {
                    foreach (var attribute in po.Attributes)
                    {
                        if (readOnly.Contains(attribute.Name))
                            attribute.IsReadOnly = true;
                    }
                }
            }
        }

        foreach (var attribute in po.Attributes)
        {
            if (attribute is not PersistentObjectAttributeAsDetail asDetail)
                continue;

            if (asDetail.Object is not null)
                await PresentOneAsync(asDetail.Object, removeVerb, readOnlyVerb, depth + 1, cancellationToken);
            foreach (var row in asDetail.Objects ?? [])
                await PresentOneAsync(row, removeVerb, readOnlyVerb, depth + 1, cancellationToken);
        }
    }
}
