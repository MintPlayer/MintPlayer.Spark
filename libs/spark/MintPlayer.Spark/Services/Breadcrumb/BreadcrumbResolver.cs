using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Reflection;
using MintPlayer.Spark.Configuration;
using Raven.Client.Documents.Session;
using System.Collections;
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
/// Loads the referenced documents a whole page needs breadth-first — one batched
/// <c>LoadAsync&lt;object&gt;(ids)</c> per reference level — then renders each breadcrumb purely
/// in memory. Request cost is O(breadcrumb depth) per page, independent of row count and fan-out.
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
        // field isn't on the projection) needs its collection document — one batched load.
        if (rootDef is { BreadcrumbProjectionSatisfiable: false } && rootIds.Count > 0)
        {
            var collectionRoots = await session.LoadAsync<object>(rootIds, ct);
            foreach (var id in rootIds)
                if (collectionRoots.TryGetValue(id, out var doc) && doc is not null)
                    renderEntity[id] = doc;
        }

        // Breadth-first: each level batch-loads all not-yet-seen referenced collection documents.
        var frontier = rootIds.Where(renderEntity.ContainsKey).ToList();
        var depth = 1;
        while (frontier.Count > 0 && depth < options.Breadcrumb.MaxDepth)
        {
            var needed = new List<string>();
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
                var collected = new List<string>();
                if (depth == 1)
                    CollectRootReferenceIds(entity, def, collected);
                else
                    foreach (var reference in closure.GetReferences(def))
                        collected.AddRange(ExtractIds(entity, reference.AttributeName));

                foreach (var refId in collected)
                    if (!renderEntity.ContainsKey(refId) && !denied.Contains(refId) && neededSet.Add(refId))
                        needed.Add(refId);
            }

            if (needed.Count == 0) break;

            var loaded = await session.LoadAsync<object>(needed, ct); // one request for the whole level
            var next = new List<string>();
            foreach (var refId in needed)
            {
                if (!loaded.TryGetValue(refId, out var doc) || doc is null) continue;
                var docType = doc.GetType();
                if (!await rowSecurity.IsAllowedAsync(docType, "Read", doc))
                {
                    denied.Add(refId); // surfaced as the redacted placeholder where it appears
                    continue;
                }
                renderEntity[refId] = doc;
                defById[refId] = modelLoader.GetEntityTypeByClrType(docType.FullName ?? docType.Name);
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
            return def?.Name ?? entity.GetType().Name;

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
    private void CollectRootReferenceIds(object entity, EntityTypeDefinition def, List<string> into)
    {
        foreach (var reference in GetAllReferences(def))
            into.AddRange(ExtractIds(entity, reference.AttributeName));

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
