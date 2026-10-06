using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Abstractions.Reflection;

namespace MintPlayer.Spark.Services;

/// <summary>
/// The write half of attribute-level rights (contributions M2c-2b, PRD §5 Q14, leaks 6 and 7): takes
/// out of a posted persistent object every attribute the caller may not write, before anything maps,
/// hooks or intercepts it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Dropped, not restored.</b> The mapper only writes attributes that are present
/// (<see cref="IEntityMapper.PopulateObjectValuesAsync"/>), so an attribute that is not there keeps
/// whatever the entity holds: the stored value on an edit, the CLR default or field initializer on a
/// create or on a new AsDetail row, and the stored row's own value on an edited row (the mapper
/// populates the stored instance it matched by key). That makes one rule cover every attribute kind
/// — scalars, references, reference arrays, TranslatedStrings, AsDetail collections, embedded objects
/// — without converting a stored value back into its wire shape. The old shield restored the stored
/// value into the posted object, which only worked for top-level scalars and was what the Update
/// response then echoed (leak 2).
/// </para>
/// <para>
/// Applied here rather than as a skip inside the mapper so that nothing downstream ever sees the
/// tampered value: <c>MapAsync</c> and every before-save interceptor (their
/// <see cref="Abstractions.Interceptors.SaveContext.PersistentObject"/> is this object) read the
/// shielded object.
/// </para>
/// <para>
/// <b>What is not writable:</b>
/// <list type="bullet">
///   <item>an attribute a static right refuses for <c>Edit</c> — <c>New</c> on a create and on an
///   AsDetail row no stored row matches — on the object's own type, AsDetail rows by their row type;</item>
///   <item>an attribute the per-row <c>GetProtectedAttributesAsync("Edit", stored)</c> hook names
///   (dotted names reach into AsDetail rows: <c>Jobs.Salary</c>);</item>
///   <item>an attribute the hook protects for <c>Read</c> on that row, unless the client marked it
///   changed: the caller was shown a blank, so posting the blank back must not wipe the value, while
///   a deliberate new value for a write-only field still lands.</item>
/// </list>
/// <see cref="Abstractions.PersistentObjectAttribute.IsValueChanged"/> is otherwise ignored: a
/// refused attribute is dropped however it is flagged.
/// </para>
/// <para>
/// Never refuses the save: refusing would tell the caller which attributes exist and which they may
/// not write (PRD leak 7). System context (sync, replication) is unrestricted — the denied sets are
/// empty there and the per-row hook does not apply.
/// </para>
/// </remarks>
internal interface IAttributeWriteShield
{
    /// <summary>
    /// Drops from <paramref name="posted"/> every attribute the caller may not write.
    /// <paramref name="stored"/> is the document as it is stored, or null for a create.
    /// </summary>
    /// <remarks>
    /// Idempotent: applying it again to an already shielded object with the same <paramref name="stored"/>
    /// drops nothing more. A natural-id create is shielded before its collision probe (contributions
    /// M2d), with no stored row, so a refused value never chooses the derived id.
    /// </remarks>
    Task<AttributeWriteShieldResult> ApplyAsync(
        PersistentObject posted, EntityTypeDefinition definition, Type entityType, object? stored,
        CancellationToken cancellationToken = default);
}

/// <summary>What <see cref="IAttributeWriteShield.ApplyAsync"/> kept out of a save.</summary>
/// <param name="Refused">
/// The attributes a <b>static</b> right kept out, as paths (<c>Title</c>, <c>Lines.Text</c>), distinct.
/// Per-row protection is deliberately not reported: whether a row protects an attribute is itself
/// what the hook hides. This is what <c>SaveContext.UnwritableAttributes</c> carries.
/// </param>
/// <param name="Unwritable">
/// The object's own (top-level) attributes whose value the save does not take from the caller — a
/// static refusal, the per-row hook's <c>Edit</c> protection, its <c>Read</c> protection on an
/// attribute the client did not change, or a static <c>Read</c> deny on one it did not post — whether or not the client posted them. Internal only: save
/// validation skips them (contributions M2d), because the value that is kept is not the caller's.
/// </param>
internal sealed record AttributeWriteShieldResult(IReadOnlyList<string> Refused, IReadOnlySet<string> Unwritable);

[Register(typeof(IAttributeWriteShield), ServiceLifetime.Scoped)]
internal sealed partial class AttributeWriteShield : IAttributeWriteShield
{
    [Inject] private readonly IAttributeRightsEnforcement attributeRights;
    [Inject] private readonly IRowSecurity rowSecurity;
    [Inject] private readonly IModelLoader modelLoader;

    private static readonly IReadOnlySet<string> None = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>AsDetail nests; a cycle of row types is stopped by the depth, as in the mapper.</summary>
    private const int MaxDepth = 8;

    public async Task<AttributeWriteShieldResult> ApplyAsync(
        PersistentObject posted, EntityTypeDefinition definition, Type entityType, object? stored,
        CancellationToken cancellationToken = default)
    {
        var (always, unlessChanged) = await PerRowAsync(entityType, stored);
        var verb = stored is null ? SparkCoreActions.New : SparkCoreActions.Edit;

        var refused = new List<string>();
        await ShieldAsync(
            posted, definition, stored, verb,
            always, unlessChanged, prefix: string.Empty, refused, depth: 0, cancellationToken);

        // After the drop: a Read-protected attribute the client changed is still there, and is the
        // caller's to write; one it did not change (or never posted) keeps the stored value.
        var unwritable = new HashSet<string>(await attributeRights.GetDeniedAsync(definition, verb, cancellationToken), StringComparer.OrdinalIgnoreCase);
        unwritable.UnionWith(always.Where(n => !n.Contains('.')));
        unwritable.UnionWith(unlessChanged.Where(n => !n.Contains('.') && posted.Attributes.All(a => !string.Equals(a.Name, n, StringComparison.OrdinalIgnoreCase))));

        // A static Read deny is the per-row Read protection's twin (composition M9, D13a constraint 2):
        // the caller was never shown the attribute, so one it did not post keeps the stored value (the
        // CLR default, or what the server fills in, on a create) and is not validated — a "required"
        // error would name an attribute the caller cannot know exists. One it did post is a deliberate
        // write to a write-only field (AttributeVerbMatrixTests); the caller supplied that name.
        var readDenied = await attributeRights.GetDeniedAsync(definition, SparkCoreActions.Read, cancellationToken);
        unwritable.UnionWith(readDenied.Where(n => posted.Attributes.All(a => !string.Equals(a.Name, n, StringComparison.OrdinalIgnoreCase))));

        return new([.. refused.Distinct(StringComparer.OrdinalIgnoreCase)], unwritable);
    }

    /// <summary>
    /// What the per-row hook protects on the stored document: for <c>Edit</c> (always dropped) and
    /// for <c>Read</c> only (dropped unless changed). Nothing on a create — there is no row yet.
    /// </summary>
    private async Task<(IReadOnlySet<string> Always, IReadOnlySet<string> UnlessChanged)> PerRowAsync(Type entityType, object? stored)
    {
        if (stored is null || !rowSecurity.HasProtectedAttributesHook(entityType))
            return (None, None);

        var edit = await rowSecurity.GetProtectedAttributesAsync(entityType, SparkCoreActions.Edit, stored);
        var read = await rowSecurity.GetProtectedAttributesAsync(entityType, SparkCoreActions.Read, stored);

        var always = edit is { Count: > 0 } ? new HashSet<string>(edit, StringComparer.OrdinalIgnoreCase) : None;
        var unlessChanged = read is { Count: > 0 }
            ? new HashSet<string>(read.Where(n => !always.Contains(n)), StringComparer.OrdinalIgnoreCase)
            : None;
        return (always, unlessChanged);
    }

    private async Task ShieldAsync(
        PersistentObject po, EntityTypeDefinition? definition, object? stored, string verb,
        IReadOnlySet<string> always, IReadOnlySet<string> unlessChanged, string prefix,
        List<string> refused, int depth, CancellationToken cancellationToken)
    {
        if (depth > MaxDepth)
            return;

        var denied = definition is null ? None : await attributeRights.GetDeniedAsync(definition, verb, cancellationToken);
        HashSet<string>? drop = null;

        foreach (var attribute in po.Attributes)
        {
            if (denied.Contains(attribute.Name))
            {
                (drop ??= new(StringComparer.OrdinalIgnoreCase)).Add(attribute.Name);
                refused.Add(prefix + attribute.Name);
                continue;
            }

            if (always.Contains(attribute.Name) || (unlessChanged.Contains(attribute.Name) && !attribute.IsValueChanged))
            {
                (drop ??= new(StringComparer.OrdinalIgnoreCase)).Add(attribute.Name);
                continue;
            }

            if (attribute is PersistentObjectAttributeAsDetail asDetail)
                await ShieldRowsAsync(asDetail, definition, stored, always, unlessChanged, prefix, refused, depth, cancellationToken);
        }

        if (drop is not null)
            po.RetainAttributes(a => !drop.Contains(a.Name));
    }

    /// <summary>
    /// AsDetail rows, each by its row type's rights: <c>Edit</c> for a row that claims a stored row by
    /// its key (judged against that stored row), <c>New</c> for any other. Adding and removing rows
    /// stays the mapper's row-type <c>New</c>/<c>Delete</c> check.
    /// </summary>
    private async Task ShieldRowsAsync(
        PersistentObjectAttributeAsDetail asDetail, EntityTypeDefinition? definition, object? stored,
        IReadOnlySet<string> always, IReadOnlySet<string> unlessChanged, string prefix,
        List<string> refused, int depth, CancellationToken cancellationToken)
    {
        // The row type comes from the model, never from the posted attribute's own AsDetailType.
        var rowTypeName = definition?.Attributes.FirstOrDefault(a => a.Name == asDetail.Name)?.AsDetailType;
        var rowDefinition = string.IsNullOrEmpty(rowTypeName) ? null : modelLoader.GetEntityTypeByClrType(rowTypeName);

        var rowAlways = Relative(always, asDetail.Name);
        var rowUnlessChanged = Relative(unlessChanged, asDetail.Name);
        var rowPrefix = prefix + asDetail.Name + ".";
        var storedValue = stored is null ? null : ReadProperty(stored, asDetail.Name);

        if (asDetail.Objects is { Count: > 0 } rows)
        {
            var storedRows = KeyRows(storedValue);
            foreach (var row in rows)
            {
                if (row is null)
                    continue;

                var match = !string.IsNullOrEmpty(row.Id) && storedRows.TryGetValue(row.Id!, out var storedRow) ? storedRow : null;
                await ShieldAsync(
                    row, rowDefinition, match, match is null ? SparkCoreActions.New : SparkCoreActions.Edit,
                    rowAlways, rowUnlessChanged, rowPrefix, refused, depth + 1, cancellationToken);
            }
        }

        if (asDetail.Object is { } nested)
        {
            // A single embedded object is matched by its property, so the stored one is the edit's subject.
            await ShieldAsync(
                nested, rowDefinition, storedValue, storedValue is null ? SparkCoreActions.New : SparkCoreActions.Edit,
                rowAlways, rowUnlessChanged, rowPrefix, refused, depth + 1, cancellationToken);
        }
    }

    /// <summary>The names under <c>{attribute}.</c>, relative to it (<c>Jobs.Salary</c> → <c>Salary</c>).</summary>
    private static IReadOnlySet<string> Relative(IReadOnlySet<string> names, string attribute)
    {
        if (names.Count == 0)
            return None;

        var lead = attribute + ".";
        HashSet<string>? result = null;
        foreach (var name in names)
        {
            if (name.Length > lead.Length && name.StartsWith(lead, StringComparison.OrdinalIgnoreCase))
                (result ??= new(StringComparer.OrdinalIgnoreCase)).Add(name[lead.Length..]);
        }
        return result ?? None;
    }

    private static object? ReadProperty(object entity, string name)
    {
        var property = entity.GetType().GetCachedProperty(name);
        return property is not null && property.CanRead ? AccessorCache.GetGetter(property)(entity) : null;
    }

    /// <summary>The stored rows by key — the same matching the mapper applies.</summary>
    private static Dictionary<string, object> KeyRows(object? value)
    {
        var byKey = new Dictionary<string, object>(StringComparer.Ordinal);
        if (value is not System.Collections.IEnumerable rows || value is string)
            return byKey;

        foreach (var row in rows)
        {
            if (row is not null && Abstractions.Model.SparkValueObjects.GetKey(row) is { Length: > 0 } key)
                byKey[key] = row;
        }
        return byKey;
    }
}
