using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Model;
using MintPlayer.Spark.Abstractions.Reflection;
using MintPlayer.Spark.Configuration;
using Raven.Client.Documents.Session;
using System.Collections;
using System.Reflection;
using System.Text;

namespace MintPlayer.Spark.Services.Breadcrumb;

/// <summary>
/// The fully-resolved breadcrumb string for every entity touched while resolving a page —
/// roots and all (transitively) referenced documents — keyed by RavenDB id.
/// </summary>
public sealed class BreadcrumbResult
{
    public IReadOnlyDictionary<string, string> BreadcrumbsById { get; }

    public BreadcrumbResult(IReadOnlyDictionary<string, string> breadcrumbsById)
        => BreadcrumbsById = breadcrumbsById;

    /// <summary>The breadcrumb for <paramref name="id"/>, or null if it was not resolved.</summary>
    public string? Get(string? id)
        => id is not null && BreadcrumbsById.TryGetValue(id, out var b) ? b : null;

    public static BreadcrumbResult Empty { get; } = new(new Dictionary<string, string>());

    /// <summary>
    /// Per entity type (by definition id), the attributes whose breadcrumb tokens render empty for
    /// this caller because a static attribute right refuses them (contributions M2c-2a). Carried so
    /// the in-place renderer for embedded rows (<see cref="EmbeddedBreadcrumbRenderer"/>) blanks the
    /// same tokens the resolver did. Null when nothing is refused.
    /// </summary>
    internal IReadOnlyDictionary<Guid, IReadOnlySet<string>>? DeniedTokensByType { get; init; }

    /// <summary>
    /// Per rendered document id, every token that rendered empty on it — static refusals and what the
    /// per-row hook protects on that row, dotted names (<c>Jobs.Salary</c>) included. Carried so an
    /// embedded AsDetail row's own breadcrumb blanks the column its owner's hook protects
    /// (contributions M2c-2b). Null when nothing is blanked.
    /// </summary>
    internal IReadOnlyDictionary<string, IReadOnlySet<string>>? BlankedTokensById { get; init; }

    /// <summary>The tokens blanked on document <paramref name="id"/>, or null.</summary>
    internal IReadOnlySet<string>? BlankedFor(string? id)
        => id is not null && BlankedTokensById is not null && BlankedTokensById.TryGetValue(id, out var blanked) ? blanked : null;

    /// <summary>The names under <c>{attribute}.</c>, relative to it (<c>Jobs.Salary</c> → <c>Salary</c>), or null.</summary>
    internal static IReadOnlySet<string>? Relative(IReadOnlySet<string>? names, string attribute)
    {
        if (names is not { Count: > 0 })
            return null;

        var lead = attribute + ".";
        HashSet<string>? result = null;
        foreach (var name in names)
        {
            if (name.Length > lead.Length && name.StartsWith(lead, StringComparison.OrdinalIgnoreCase))
                (result ??= new(StringComparer.OrdinalIgnoreCase)).Add(name[lead.Length..]);
        }
        return result;
    }

    /// <summary>Whether a <c>{<paramref name="attributeName"/>}</c> token of <paramref name="definition"/> renders empty.</summary>
    internal bool IsTokenDenied(EntityTypeDefinition? definition, string attributeName)
        => definition is not null
           && DeniedTokensByType is not null
           && DeniedTokensByType.TryGetValue(definition.Id, out var denied)
           && denied.Contains(attributeName);
}

/// <summary>
/// Resolves breadcrumbs recursively across references, identically for every read path.
/// Loads the referenced documents a whole page needs breadth-first — one batched load per
/// distinct declared reference target type per level — then renders each breadcrumb purely in
/// memory. Request cost is O(breadcrumb depth × types per level) per page, independent of row
/// count and fan-out.
/// </summary>
internal interface IBreadcrumbResolver
{
    /// <param name="roots">The page's entities (collection documents or projections).</param>
    /// <param name="rootDef">The <b>collection</b> entity-type definition whose breadcrumb template/edges apply to the roots.</param>
    /// <param name="action">
    /// The verb the roots are presented under — <c>Query</c> on a list, <c>Read</c> on a detail
    /// load. Decides which static attribute rights, and which answer of the per-row hook, blank the
    /// roots' own tokens. Referenced documents are always judged for <c>Read</c>.
    /// </param>
    Task<BreadcrumbResult> ResolveAsync(
        IAsyncDocumentSession session, IReadOnlyList<object> roots, EntityTypeDefinition? rootDef, CancellationToken ct = default,
        string action = "Read");
}

[Register(typeof(IBreadcrumbResolver), ServiceLifetime.Scoped)]
internal partial class BreadcrumbResolver : IBreadcrumbResolver
{
    [Inject] private readonly IModelLoader modelLoader;
    [Inject] private readonly IBreadcrumbClosure closure;
    [Inject] private readonly IRowSecurity rowSecurity;
    [Inject] private readonly SparkOptions options;
    // Optional so the test sites that construct the resolver by hand keep compiling; DI always
    // supplies it, and without it no token is blanked by a static attribute right.
    [Inject] private readonly IAttributeRightsEnforcement? attributeRights = null;

    /// <summary>A blanking-set entry meaning "every field token": an unverifiable row shows none.</summary>
    private const string AllTokens = "*";

    public async Task<BreadcrumbResult> ResolveAsync(
        IAsyncDocumentSession session, IReadOnlyList<object> roots, EntityTypeDefinition? rootDef, CancellationToken ct = default,
        string action = "Read")
    {
        if (roots.Count == 0)
            return BreadcrumbResult.Empty;

        // id → the entity to render that id's breadcrumb from; id → the def driving the render.
        var renderEntity = new Dictionary<string, object>(StringComparer.Ordinal);
        var defById = new Dictionary<string, EntityTypeDefinition?>(StringComparer.Ordinal);
        var denied = new HashSet<string>(StringComparer.Ordinal);

        var rootIds = new List<string>(roots.Count);
        foreach (var root in roots)
        {
            var id = GetId(root);
            if (string.IsNullOrEmpty(id) || renderEntity.ContainsKey(id)) continue;
            rootIds.Add(id);
            renderEntity[id] = root;
            defById[id] = rootDef;
        }

        // Level-0 fallback: a root projection that can't render its breadcrumb (a placeholder
        // field isn't on the projection) needs its collection document — one batched load,
        // under the root's DECLARED type (see LoadManyAsync).
        if (rootDef is { BreadcrumbProjectionSatisfiable: false } && rootIds.Count > 0)
        {
            var collectionRoots = await LoadManyAsync(
                session, SparkTypeResolver.ResolveClrType(rootDef.ClrType), rootIds, ct);
            foreach (var id in rootIds)
                if (collectionRoots.TryGetValue(id, out var doc) && doc is not null)
                    renderEntity[id] = doc;
        }

        // Breadth-first: each level batch-loads all not-yet-seen referenced collection documents.
        var frontier = rootIds.Where(renderEntity.ContainsKey).ToList();
        var depth = 1;
        while (frontier.Count > 0 && depth < options.Breadcrumb.MaxDepth)
        {
            // Referenced ids, grouped by the target type the MODEL declares for the edge that
            // reached them. Batched per declared type rather than in one untyped load — see
            // LoadManyAsync for why the type argument is load-bearing.
            var neededByType = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var declaredTypeById = new Dictionary<string, string>(StringComparer.Ordinal);
            var neededSet = new HashSet<string>(StringComparer.Ordinal);

            foreach (var id in frontier)
            {
                var def = defById[id];
                if (def is null) continue;
                var entity = renderEntity[id];

                // Roots (depth 1) follow EVERY reference attribute — each one needs a display label
                // on the returned PO — AND descend into embedded AsDetail children, whose reference
                // cells are materialized on the PO too and need the same label. Deeper levels follow
                // only the breadcrumb-template references, since a referenced entity is represented
                // solely by its breadcrumb string.
                var collected = new List<(string Id, string TargetClrType)>();
                if (depth == 1)
                    CollectRootReferenceIds(entity, def, collected);
                else
                    foreach (var reference in closure.GetReferences(def))
                        foreach (var refId in ExtractIds(entity, reference.AttributeName))
                            collected.Add((refId, reference.TargetClrType));

                foreach (var (refId, targetClrType) in collected)
                {
                    if (renderEntity.ContainsKey(refId) || denied.Contains(refId) || !neededSet.Add(refId))
                        continue;
                    declaredTypeById[refId] = targetClrType;
                    if (!neededByType.TryGetValue(targetClrType, out var bucket))
                        neededByType[targetClrType] = bucket = [];
                    bucket.Add(refId);
                }
            }

            if (neededByType.Count == 0) break;

            var loaded = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            foreach (var (targetClrType, ids) in neededByType) // one request per declared type
            {
                var typed = await LoadManyAsync(session, SparkTypeResolver.ResolveClrType(targetClrType), ids, ct);
                foreach (var (id, doc) in typed)
                    loaded[id] = doc;
            }

            var next = new List<string>();
            foreach (var refId in neededSet)
            {
                if (!loaded.TryGetValue(refId, out var doc) || doc is null) continue;

                // The document's own type when the model knows it, else the type the reference
                // declares. A subtype stored behind a base-typed reference keeps its own
                // breadcrumb; a document whose @Raven-Clr-Type is stale still gets the right one.
                var docType = doc.GetType();
                var runtimeDef = modelLoader.GetEntityTypeByClrType(docType.FullName ?? docType.Name);
                var declaredClrType = declaredTypeById.GetValueOrDefault(refId);
                var securityType = runtimeDef is not null
                    ? docType
                    : SparkTypeResolver.ResolveClrType(declaredClrType) ?? docType;

                if (!await rowSecurity.IsAllowedAsync(securityType, "Read", doc))
                {
                    denied.Add(refId); // surfaced as the redacted placeholder where it appears
                    continue;
                }
                renderEntity[refId] = doc;
                defById[refId] = runtimeDef
                    ?? (declaredClrType is null ? null : modelLoader.GetEntityTypeByClrType(declaredClrType));
                next.Add(refId);
            }
            frontier = next;
            depth++;
        }

        // Which tokens render empty (contributions M2c-2a): an attribute a static right refuses, and
        // one the per-row hook protects on that row. Decided before rendering because both answers
        // are asynchronous and rendering is not; the static half is one decision per (type, verb).
        var rootIdSet = new HashSet<string>(rootIds, StringComparer.Ordinal);
        var deniedByType = await ResolveDeniedTokensAsync(rootDef, action, defById.Values, ct);
        var blankedById = await ResolveBlankedTokensAsync(
            session, renderEntity, defById, rootIdSet, rootDef, action, deniedByType, ct);

        // Render every touched id purely in memory.
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var id in renderEntity.Keys)
            result[id] = Render(id, renderEntity, defById, denied, allowReferences: true, [], blankedById, deniedByType);
        foreach (var id in denied)
            result[id] = options.Breadcrumb.RedactedPlaceholder;

        return new BreadcrumbResult(result)
        {
            DeniedTokensByType = deniedByType.Count == 0 ? null : deniedByType,
            BlankedTokensById = blankedById.Count == 0 ? null : blankedById,
        };
    }

    /// <summary>
    /// The statically refused attributes per definition that can appear in this render: the roots'
    /// type under <paramref name="action"/>, every referenced type under <c>Read</c>, and the AsDetail
    /// row types reachable from either (a row inherits its owner's verb).
    /// </summary>
    private async Task<Dictionary<Guid, IReadOnlySet<string>>> ResolveDeniedTokensAsync(
        EntityTypeDefinition? rootDef, string action, IEnumerable<EntityTypeDefinition?> definitions, CancellationToken ct)
    {
        var result = new Dictionary<Guid, IReadOnlySet<string>>();
        if (attributeRights is null)
            return result;

        var visited = new HashSet<Guid>();
        if (rootDef is not null)
            await CollectDeniedAsync(rootDef, action, depth: 0);
        foreach (var definition in definitions)
        {
            if (definition is not null)
                await CollectDeniedAsync(definition, "Read", depth: 0);
        }
        return result;

        async Task CollectDeniedAsync(EntityTypeDefinition definition, string verb, int depth)
        {
            if (depth > options.Breadcrumb.MaxDepth || !visited.Add(definition.Id))
                return;

            var refused = await attributeRights.GetDeniedAsync(definition, verb, ct);
            if (refused.Count > 0)
                result[definition.Id] = refused;

            foreach (var attribute in definition.Attributes)
            {
                if (attribute.DataType == "AsDetail" && !string.IsNullOrEmpty(attribute.AsDetailType)
                    && modelLoader.GetEntityTypeByClrType(attribute.AsDetailType) is { } rowType)
                {
                    await CollectDeniedAsync(rowType, verb, depth + 1);
                }
            }
        }
    }

    /// <summary>
    /// Per rendered id, the tokens that render empty: its type's static refusals plus what the
    /// per-row hook protects on that very document. The hook is asked once per rendered document,
    /// and only for a type that overrides it.
    /// </summary>
    /// <remarks>
    /// The hook is typed on the entity, so a root that is an index projection is judged on its base
    /// document, batch-loaded once (a session-cache hit wherever row security already loaded it). A
    /// root whose base document cannot be found renders no field token at all — unverifiable is not
    /// shown, as in redaction.
    /// </remarks>
    private async Task<Dictionary<string, IReadOnlySet<string>>> ResolveBlankedTokensAsync(
        IAsyncDocumentSession session,
        Dictionary<string, object> renderEntity,
        Dictionary<string, EntityTypeDefinition?> defById,
        HashSet<string> rootIds,
        EntityTypeDefinition? rootDef,
        string action,
        Dictionary<Guid, IReadOnlySet<string>> deniedByType,
        CancellationToken ct)
    {
        var result = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal);

        // Projected roots whose hook must be asked of the stored document, grouped by entity type.
        var baseLoads = new Dictionary<Type, List<string>>();

        foreach (var (id, entity) in renderEntity)
        {
            var isRoot = rootIds.Contains(id);
            var def = isRoot ? rootDef : defById.GetValueOrDefault(id);
            if (def is null)
                continue;

            if (deniedByType.TryGetValue(def.Id, out var staticDenied))
                result[id] = staticDenied;

            var clrType = string.IsNullOrEmpty(def.ClrType) ? null : SparkTypeResolver.ResolveClrType(def.ClrType);
            if (clrType is null || !rowSecurity.HasProtectedAttributesHook(clrType))
                continue;

            if (clrType.IsInstanceOfType(entity))
            {
                var names = await rowSecurity.GetProtectedAttributesAsync(clrType, isRoot ? action : "Read", entity);
                Merge(id, names);
            }
            else if (isRoot)
            {
                if (!baseLoads.TryGetValue(clrType, out var ids))
                    baseLoads[clrType] = ids = [];
                ids.Add(id);
            }
        }

        foreach (var (clrType, ids) in baseLoads)
        {
            var loaded = await RowSecurity.LoadBaseDocumentsAsync(session, clrType, ids, ct);
            foreach (var id in ids)
            {
                if (!loaded.TryGetValue(id, out var document))
                {
                    Merge(id, [AllTokens]);
                    continue;
                }

                Merge(id, await rowSecurity.GetProtectedAttributesAsync(clrType, action, document));
            }
        }

        return result;

        void Merge(string id, IReadOnlyCollection<string>? names)
        {
            if (names is not { Count: > 0 })
                return;

            var merged = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
            if (result.TryGetValue(id, out var existing))
                merged.UnionWith(existing);
            result[id] = merged;
        }
    }

    private static bool IsBlanked(IReadOnlySet<string>? blanked, string attributeName)
        => blanked is not null && (blanked.Contains(AllTokens) || blanked.Contains(attributeName));

    /// <summary>
    /// The owner's blanking entries that reach into the embedded value under
    /// <paramref name="attributeName"/> (<c>Address.Street</c> → <c>Street</c>), relative to it.
    /// </summary>
    private static IReadOnlySet<string>? Descend(IReadOnlySet<string>? blanked, string attributeName)
    {
        if (blanked is null || blanked.Contains(AllTokens))
            return blanked;

        HashSet<string>? result = null;
        var prefix = attributeName + ".";
        foreach (var entry in blanked)
        {
            if (entry.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                (result ??= new(StringComparer.OrdinalIgnoreCase)).Add(entry[prefix.Length..]);
        }
        return result;
    }

    private static IReadOnlySet<string>? Union(IReadOnlySet<string>? a, IReadOnlySet<string>? b)
    {
        if (a is not { Count: > 0 }) return b;
        if (b is not { Count: > 0 }) return a;

        var merged = new HashSet<string>(a, StringComparer.OrdinalIgnoreCase);
        merged.UnionWith(b);
        return merged;
    }

    private string Render(
        string id,
        Dictionary<string, object> renderEntity,
        Dictionary<string, EntityTypeDefinition?> defById,
        HashSet<string> denied,
        bool allowReferences,
        HashSet<string> visited,
        Dictionary<string, IReadOnlySet<string>> blankedById,
        Dictionary<Guid, IReadOnlySet<string>> deniedByType)
    {
        if (denied.Contains(id))
            return options.Breadcrumb.RedactedPlaceholder;
        if (!renderEntity.TryGetValue(id, out var entity))
            return string.Empty; // not loaded (beyond MaxDepth or missing/denied document)

        var def = defById.GetValueOrDefault(id);
        if (def is null || string.IsNullOrEmpty(def.Breadcrumb))
            // No definition means we know nothing about the document but its id — which is at
            // least a true, stable label. Never the CLR type name: that rendered referenced
            // documents as "JObject" whenever an untyped load could not recover their type,
            // putting an internal implementation detail in front of the user.
            return def?.Name ?? id;

        // Re-entering an id already on the render path is a cycle: render this node's scalars
        // but suppress its reference expansion so we terminate.
        var openedScope = visited.Add(id);
        var expandReferences = allowReferences && openedScope;
        var blanked = blankedById.GetValueOrDefault(id);

        var sb = new StringBuilder();
        foreach (var token in BreadcrumbTemplate.Parse(def.Breadcrumb))
        {
            switch (token)
            {
                case LiteralToken literal:
                    sb.Append(literal.Text);
                    break;

                // A refused or protected attribute renders as nothing — the same output as an empty
                // value, so the breadcrumb says no more than the attribute itself would (M2c-2a).
                case FieldToken field when IsBlanked(blanked, field.AttributeName):
                    break;

                case FieldToken field:
                    var attr = def.Attributes.FirstOrDefault(a => a.Name == field.AttributeName);
                    if (attr is { DataType: "Reference" } && !string.IsNullOrEmpty(attr.ReferenceType))
                    {
                        if (!expandReferences) break;
                        var ids = ExtractIds(entity, field.AttributeName).ToList();
                        if (attr.IsArray)
                        {
                            var parts = ids
                                .Select(rid => Render(rid, renderEntity, defById, denied, true, visited, blankedById, deniedByType))
                                .Where(s => !string.IsNullOrEmpty(s));
                            sb.Append(string.Join(options.Breadcrumb.ReferenceSeparator, parts));
                        }
                        else
                        {
                            var rid = ids.FirstOrDefault();
                            if (!string.IsNullOrEmpty(rid))
                                sb.Append(Render(rid, renderEntity, defById, denied, true, visited, blankedById, deniedByType));
                        }
                    }
                    else if (attr is { DataType: "AsDetail", IsArray: false } && !string.IsNullOrEmpty(attr.AsDetailType))
                    {
                        // Embedded complex token: recurse into the embedded type's own breadcrumb.
                        // Before #273 this fell into the scalar arm and rendered ToString() — the
                        // CLR type name — silently.
                        var child = ReadValue(entity, field.AttributeName);
                        if (child is not null)
                            sb.Append(RenderEmbedded(child, 0, renderEntity, defById, denied, expandReferences, visited,
                                blankedById, deniedByType, Descend(blanked, field.AttributeName)));
                    }
                    else
                    {
                        sb.Append(FormatScalar(ReadValue(entity, field.AttributeName)));
                    }
                    break;
            }
        }

        if (openedScope)
            visited.Remove(id);
        return sb.ToString();
    }

    /// <summary>
    /// The breadcrumb of an embedded (id-less) value, rendered in place: its entity-type
    /// definition's template when one is registered, else the value's <c>[Breadcrumb]</c>-marked
    /// property. Embedded values form a finite document tree, so recursion terminates on the data;
    /// the depth cap only guards template-level pathologies.
    /// </summary>
    private string RenderEmbedded(
        object child,
        int depth,
        Dictionary<string, object> renderEntity,
        Dictionary<string, EntityTypeDefinition?> defById,
        HashSet<string> denied,
        bool expandReferences,
        HashSet<string> visited,
        Dictionary<string, IReadOnlySet<string>> blankedById,
        Dictionary<Guid, IReadOnlySet<string>> deniedByType,
        IReadOnlySet<string>? inherited)
    {
        if (depth >= options.Breadcrumb.MaxDepth)
            return string.Empty;

        var type = child.GetType();
        var def = modelLoader.GetEntityTypeByClrType(type.FullName ?? type.Name);

        if (def is not null && !string.IsNullOrEmpty(def.Breadcrumb))
        {
            // The owner's dotted protections for this value, plus the embedded type's own refusals.
            var blanked = Union(inherited, deniedByType.GetValueOrDefault(def.Id));

            var sb = new StringBuilder();
            foreach (var token in BreadcrumbTemplate.Parse(def.Breadcrumb))
            {
                switch (token)
                {
                    case LiteralToken literal:
                        sb.Append(literal.Text);
                        break;

                    case FieldToken field when IsBlanked(blanked, field.AttributeName):
                        break;

                    case FieldToken field:
                        var attr = def.Attributes.FirstOrDefault(a => a.Name == field.AttributeName);
                        if (attr is { DataType: "Reference" } && !string.IsNullOrEmpty(attr.ReferenceType))
                        {
                            if (!expandReferences) break;
                            // Reference targets nested inside embedded values are preloaded by
                            // CollectRootReferenceIds, so the by-id render finds them in memory.
                            var parts = ExtractIds(child, field.AttributeName)
                                .Select(rid => Render(rid, renderEntity, defById, denied, true, visited, blankedById, deniedByType))
                                .Where(s => !string.IsNullOrEmpty(s));
                            sb.Append(string.Join(options.Breadcrumb.ReferenceSeparator, parts));
                        }
                        else if (attr is { DataType: "AsDetail", IsArray: false } && !string.IsNullOrEmpty(attr.AsDetailType))
                        {
                            var nested = ReadValue(child, field.AttributeName);
                            if (nested is not null)
                                sb.Append(RenderEmbedded(nested, depth + 1, renderEntity, defById, denied, expandReferences, visited,
                                    blankedById, deniedByType, Descend(blanked, field.AttributeName)));
                        }
                        else
                        {
                            sb.Append(FormatScalar(ReadValue(child, field.AttributeName)));
                        }
                        break;
                }
            }
            return sb.ToString();
        }

        // Unregistered (or template-less) embedded type: the [Breadcrumb]-marked property is the
        // type's declared breadcrumb value.
        var marked = type.GetBreadcrumbProperty();
        if (marked is null)
            return def?.Name ?? type.Name;

        var markedBlanked = Union(inherited, def is null ? null : deniedByType.GetValueOrDefault(def.Id));
        if (IsBlanked(markedBlanked, marked.Name))
            return string.Empty;

        var value = AccessorCache.GetGetter(marked)(child);
        if (value is null)
            return string.Empty;

        return SparkModelShape.IsComplexType(value.GetType())
            ? RenderEmbedded(value, depth + 1, renderEntity, defById, denied, expandReferences, visited,
                blankedById, deniedByType, Descend(markedBlanked, marked.Name))
            : FormatScalar(value);
    }

    /// <summary>Every <c>[Reference]</c> attribute of a type — root attributes all need a display label.</summary>
    private static IReadOnlyList<BreadcrumbReference> GetAllReferences(EntityTypeDefinition def)
        => def.Attributes
            .Where(a => a.DataType == "Reference" && !string.IsNullOrEmpty(a.ReferenceType))
            .Select(a => new BreadcrumbReference(a.Name, a.ReferenceType!, a.IsArray))
            .ToList();

    /// <summary>
    /// Collects every referenced document id reachable from a root entity for display: its own
    /// <c>[Reference]</c> attributes plus, recursively, the references nested inside its embedded
    /// AsDetail children. Those embedded rows are materialized as PersistentObjects on the returned
    /// PO (<c>EntityMapper.PopulateAsDetail</c>), so each of their reference cells needs a resolved
    /// breadcrumb exactly like a top-level reference column. AsDetail children are embedded objects
    /// (a finite document tree, never cyclic), so the recursion is bounded by the document shape.
    /// </summary>
    private void CollectRootReferenceIds(
        object entity, EntityTypeDefinition def, List<(string Id, string TargetClrType)> into)
    {
        foreach (var reference in GetAllReferences(def))
            foreach (var refId in ExtractIds(entity, reference.AttributeName))
                into.Add((refId, reference.TargetClrType));

        foreach (var attr in def.Attributes)
        {
            if (attr.DataType != "AsDetail" || string.IsNullOrEmpty(attr.AsDetailType))
                continue;
            var childDef = modelLoader.GetEntityTypeByClrType(attr.AsDetailType);
            if (childDef is null)
                continue;
            foreach (var child in ReadChildren(entity, attr.Name))
                CollectRootReferenceIds(child, childDef, into);
        }
    }

    /// <summary>Yields the embedded AsDetail child object(s) of a property — the single value, or
    /// each non-null element of the collection (an AsDetail value is never a bare string).</summary>
    private static IEnumerable<object> ReadChildren(object entity, string propertyName)
    {
        var value = ReadValue(entity, propertyName);
        switch (value)
        {
            case null or string:
                yield break;
            case IEnumerable enumerable:
                foreach (var item in enumerable)
                    if (item is not null) yield return item;
                yield break;
            default:
                yield return value;
                yield break;
        }
    }

    /// <summary>
    /// Batch-loads documents as <paramref name="entityType"/>, falling back to an untyped load
    /// when the model names no resolvable CLR type (a JSON-only virtual type).
    /// <para>
    /// The type argument is load-bearing, for the same reason it is in <c>RowSecurity</c> (#281).
    /// RavenDB recovers a document's CLR type from <c>@Raven-Clr-Type</c> when it can and falls
    /// back to a <c>JObject</c> when that metadata is absent or names a type this process cannot
    /// resolve — a raw put, a bulk insert, an import, or an entity since moved between assemblies
    /// or renamed. Asking for <c>object</c> made two user-visible outcomes depend on stored
    /// metadata rather than on the model:
    /// </para>
    /// <list type="bullet">
    /// <item>the breadcrumb, because no definition matched <c>JObject</c>, so every referenced
    /// document rendered as the literal text "JObject" instead of its label; and</item>
    /// <item>row security, because <c>IsAllowedAsync(typeof(JObject), …)</c> finds no rule for
    /// that type and "no rule" means unrestricted — so a referenced document skipped the row rule
    /// its own type declares.</item>
    /// </list>
    /// <para>
    /// The reference edge already carries the target type the model declares, so nothing needs to
    /// be inferred from the document. Cost is one request per distinct declared type per level
    /// rather than one per level; a page's references span a handful of types, and the alternative
    /// is a result that is wrong whenever the metadata is.
    /// </para>
    /// </summary>
    private static async Task<Dictionary<string, object>> LoadManyAsync(
        IAsyncDocumentSession session, Type? entityType, IReadOnlyCollection<string> ids, CancellationToken ct)
    {
        // Document ids are case-insensitive, and an index-projected Id can differ in case from the
        // stored one — the same comparer RavenDB builds its own result with.
        var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        if (ids.Count == 0)
            return result;

        if (entityType is null)
        {
            foreach (var (id, doc) in await session.LoadAsync<object>(ids, ct))
                if (doc is not null) result[id] = doc;
            return result;
        }

        var loadMethod = ReflectionCache.GetOrAdd<(string Op, Type Entity), MethodInfo?>(
            ("BreadcrumbResolver.SessionLoadManyAsync", entityType),
            static k => typeof(IAsyncDocumentSession)
                .GetMethod(nameof(IAsyncDocumentSession.LoadAsync), [typeof(IEnumerable<string>), typeof(CancellationToken)])
                ?.MakeGenericMethod(k.Entity));

        // Reflection applies no default arguments, so the token is passed explicitly.
        if (loadMethod?.Invoke(session, [ids, ct]) is not Task task)
            return result;

        await task;

        // Task<Dictionary<string, TEntity>>, copied into the object-valued shape callers hold.
        if (task.GetCompletedTaskResult() is IDictionary loaded)
            foreach (DictionaryEntry entry in loaded)
                if (entry.Value is not null)
                    result[(string)entry.Key] = entry.Value;

        return result;
    }

    private static string GetId(object entity)
        => ReadValue(entity, "Id")?.ToString() ?? string.Empty;

    private static object? ReadValue(object entity, string propertyName)
    {
        var property = entity.GetType().GetCachedProperty(propertyName);
        return property is not null && property.CanRead ? AccessorCache.GetGetter(property)(entity) : null;
    }

    private static string FormatScalar(object? value) => value?.ToString() ?? string.Empty;

    /// <summary>A reference property is a single id (string) or an array of ids ([Reference] List&lt;string&gt;).</summary>
    private static IEnumerable<string> ExtractIds(object entity, string propertyName)
    {
        var value = ReadValue(entity, propertyName);
        switch (value)
        {
            case null:
                yield break;
            case string s:
                if (!string.IsNullOrEmpty(s)) yield return s;
                yield break;
            case IEnumerable enumerable:
                foreach (var item in enumerable)
                {
                    var id = item?.ToString();
                    if (!string.IsNullOrEmpty(id)) yield return id;
                }
                yield break;
        }
    }
}
