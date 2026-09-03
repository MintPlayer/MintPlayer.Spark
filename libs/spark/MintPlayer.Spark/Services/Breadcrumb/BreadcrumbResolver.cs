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
    Task<BreadcrumbResult> ResolveAsync(
        IAsyncDocumentSession session, IReadOnlyList<object> roots, EntityTypeDefinition? rootDef, CancellationToken ct = default);
}

[Register(typeof(IBreadcrumbResolver), ServiceLifetime.Scoped)]
internal partial class BreadcrumbResolver : IBreadcrumbResolver
{
    [Inject] private readonly IModelLoader modelLoader;
    [Inject] private readonly IBreadcrumbClosure closure;
    [Inject] private readonly IRowSecurity rowSecurity;
    [Inject] private readonly SparkOptions options;

    public async Task<BreadcrumbResult> ResolveAsync(
        IAsyncDocumentSession session, IReadOnlyList<object> roots, EntityTypeDefinition? rootDef, CancellationToken ct = default)
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

        // Render every touched id purely in memory.
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var id in renderEntity.Keys)
            result[id] = Render(id, renderEntity, defById, denied, allowReferences: true, []);
        foreach (var id in denied)
            result[id] = options.Breadcrumb.RedactedPlaceholder;

        return new BreadcrumbResult(result);
    }

    private string Render(
        string id,
        Dictionary<string, object> renderEntity,
        Dictionary<string, EntityTypeDefinition?> defById,
        HashSet<string> denied,
        bool allowReferences,
        HashSet<string> visited)
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

        var sb = new StringBuilder();
        foreach (var token in BreadcrumbTemplate.Parse(def.Breadcrumb))
        {
            switch (token)
            {
                case LiteralToken literal:
                    sb.Append(literal.Text);
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
                                .Select(rid => Render(rid, renderEntity, defById, denied, true, visited))
                                .Where(s => !string.IsNullOrEmpty(s));
                            sb.Append(string.Join(options.Breadcrumb.ReferenceSeparator, parts));
                        }
                        else
                        {
                            var rid = ids.FirstOrDefault();
                            if (!string.IsNullOrEmpty(rid))
                                sb.Append(Render(rid, renderEntity, defById, denied, true, visited));
                        }
                    }
                    else if (attr is { DataType: "AsDetail", IsArray: false } && !string.IsNullOrEmpty(attr.AsDetailType))
                    {
                        // Embedded complex token: recurse into the embedded type's own breadcrumb.
                        // Before #273 this fell into the scalar arm and rendered ToString() — the
                        // CLR type name — silently.
                        var child = ReadValue(entity, field.AttributeName);
                        if (child is not null)
                            sb.Append(RenderEmbedded(child, 0, renderEntity, defById, denied, expandReferences, visited));
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
        HashSet<string> visited)
    {
        if (depth >= options.Breadcrumb.MaxDepth)
            return string.Empty;

        var type = child.GetType();
        var def = modelLoader.GetEntityTypeByClrType(type.FullName ?? type.Name);

        if (def is not null && !string.IsNullOrEmpty(def.Breadcrumb))
        {
            var sb = new StringBuilder();
            foreach (var token in BreadcrumbTemplate.Parse(def.Breadcrumb))
            {
                switch (token)
                {
                    case LiteralToken literal:
                        sb.Append(literal.Text);
                        break;

                    case FieldToken field:
                        var attr = def.Attributes.FirstOrDefault(a => a.Name == field.AttributeName);
                        if (attr is { DataType: "Reference" } && !string.IsNullOrEmpty(attr.ReferenceType))
                        {
                            if (!expandReferences) break;
                            // Reference targets nested inside embedded values are preloaded by
                            // CollectRootReferenceIds, so the by-id render finds them in memory.
                            var parts = ExtractIds(child, field.AttributeName)
                                .Select(rid => Render(rid, renderEntity, defById, denied, true, visited))
                                .Where(s => !string.IsNullOrEmpty(s));
                            sb.Append(string.Join(options.Breadcrumb.ReferenceSeparator, parts));
                        }
                        else if (attr is { DataType: "AsDetail", IsArray: false } && !string.IsNullOrEmpty(attr.AsDetailType))
                        {
                            var nested = ReadValue(child, field.AttributeName);
                            if (nested is not null)
                                sb.Append(RenderEmbedded(nested, depth + 1, renderEntity, defById, denied, expandReferences, visited));
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

        var value = AccessorCache.GetGetter(marked)(child);
        if (value is null)
            return string.Empty;

        return SparkModelShape.IsComplexType(value.GetType())
            ? RenderEmbedded(value, depth + 1, renderEntity, defById, denied, expandReferences, visited)
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
