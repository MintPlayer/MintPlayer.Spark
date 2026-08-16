using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Reflection;
using System.Reflection;

namespace MintPlayer.Spark.Services;

/// <summary>
/// Discovers <see cref="ReferenceAttribute"/> properties and chains RavenDB <c>.Include()</c> so a
/// query's first-level references are primed into the session cache. Breadcrumb resolution itself
/// (recursive, batched) lives in <see cref="Breadcrumb.IBreadcrumbResolver"/>.
/// </summary>
internal interface IReferenceResolver
{
    List<(PropertyInfo Property, ReferenceAttribute Attribute)> GetReferenceProperties(Type entityType);

    /// <summary>
    /// Gets reference properties, falling back to a base entity type when the primary type
    /// (e.g., a projection like VPerson) lacks [Reference] attributes but has matching property names.
    /// Returns PropertyInfo from <paramref name="entityType"/> paired with ReferenceAttribute from <paramref name="fallbackType"/>.
    /// </summary>
    List<(PropertyInfo Property, ReferenceAttribute Attribute)> GetReferenceProperties(Type entityType, Type fallbackType);

    /// <summary>
    /// Chains RavenDB <c>.Include(path)</c> on a queryable of <paramref name="elementType"/> so the
    /// named referenced documents are loaded in the same round-trip. Returns the (possibly wrapped)
    /// queryable. Paths are dotted JSON paths into the document.
    /// </summary>
    object ApplyIncludes(object queryable, Type elementType, IReadOnlyCollection<string> paths);

    /// <summary>The paths a type's Actions class declares via <c>GetDefaultIncludes()</c>, or null.</summary>
    IReadOnlyCollection<string>? GetDefaultIncludes(Type entityType);

    /// <summary>
    /// The full include set for a query of <paramref name="entityType"/> read as
    /// <paramref name="queryType"/>: the <c>[Reference]</c> property names merged with the Actions
    /// class's <c>GetDefaultIncludes()</c> paths, deduped. Empty when nothing to include.
    /// </summary>
    IReadOnlyCollection<string> ResolveIncludePaths(Type queryType, Type entityType);
}

[Register(typeof(IReferenceResolver), ServiceLifetime.Scoped)]
internal partial class ReferenceResolver : IReferenceResolver
{
    [Inject] private readonly IActionsResolver actionsResolver;
    [Inject] private readonly Microsoft.Extensions.Logging.ILogger<ReferenceResolver>? logger;

    /// <summary>One-shot diagnostic keys for GetDefaultIncludes paths whose first segment is unknown.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(Type, string), bool> announced = new();

    public List<(PropertyInfo Property, ReferenceAttribute Attribute)> GetReferenceProperties(Type entityType)
    {
        // Return a copy of the cached array as a List so callers can mutate (the
        // overload below appends fallback entries). The underlying array itself
        // is shared via ReflectionCache and must not be mutated.
        var cached = ReflectionCache.GetOrAdd<Type, (PropertyInfo Property, ReferenceAttribute Attribute)[]>(
            entityType,
            static t => t.GetCachedProperties()
                // An ignored [Reference] must not be .Include()d: besides the pointless load, it
                // would pull the referenced document into the session for a field the client is
                // never allowed to see.
                .Where(p => !p.IsIgnoredForSparkModel())
                .Select(p => (Property: p, Attribute: p.GetCachedCustomAttribute<ReferenceAttribute>()))
                .Where(x => x.Attribute is not null)
                .Select(x => (x.Property, x.Attribute!))
                .ToArray());
        return new List<(PropertyInfo, ReferenceAttribute)>(cached);
    }

    public List<(PropertyInfo Property, ReferenceAttribute Attribute)> GetReferenceProperties(Type entityType, Type fallbackType)
    {
        // First try the primary type (e.g., projection type VPerson)
        var result = GetReferenceProperties(entityType);
        if (result.Count > 0 || entityType == fallbackType)
            return result;

        // Fallback: get [Reference] attributes from the base type (e.g., Person),
        // but pair them with PropertyInfo from the primary type so value reading works.
        var fallbackProps = GetReferenceProperties(fallbackType);
        foreach (var (fallbackProp, refAttr) in fallbackProps)
        {
            // The fallback list is already filtered, but the primary type may ignore a property
            // the base type does not.
            var matchingProp = entityType.GetCachedProperty(fallbackProp.Name);
            if (matchingProp != null && !matchingProp.IsIgnoredForSparkModel())
            {
                result.Add((matchingProp, refAttr));
            }
        }

        return result;
    }

    public object ApplyIncludes(object queryable, Type elementType, IReadOnlyCollection<string> paths)
    {
        if (paths.Count == 0)
            return queryable;

        // RavenDB's Include on a queryable is the STATIC extension
        // LinqExtensions.Include<TResult>(IQueryable<TResult>, string) — there is NO instance
        // Include on RavenQueryInspector<T> (the prior code reflected for one and silently no-oped,
        // so Spark applied no includes at all). Invoke the static generic extension reflectively,
        // the same shape RowSecurity.ComposeRowFilter uses for Queryable.Where. Cached per element
        // type.
        var includeMethod = ReflectionCache.GetOrAdd<(string Op, Type Element), MethodInfo>(
            ("ReferenceResolver.LinqInclude", elementType),
            static k => typeof(Raven.Client.Documents.LinqExtensions).GetMethods()
                .First(m => m.Name == "Include"
                    && m.IsGenericMethodDefinition
                    && m.GetGenericArguments().Length == 1
                    && m.GetParameters() is { Length: 2 } ps
                    && ps[0].ParameterType.IsGenericType
                    && ps[0].ParameterType.GetGenericTypeDefinition() == typeof(System.Linq.IQueryable<>)
                    && ps[1].ParameterType == typeof(string))
                .MakeGenericMethod(k.Element));

        foreach (var path in paths)
            queryable = includeMethod.Invoke(null, [queryable, path])!;

        return queryable;
    }

    public IReadOnlyCollection<string>? GetDefaultIncludes(Type entityType)
    {
        var actions = actionsResolver.ResolveForType(entityType);
        var method = ReflectionCache.GetOrAdd<(string Op, Type Actions), MethodInfo?>(
            ("ReferenceResolver.GetDefaultIncludes", actions.GetType()),
            static k => k.Actions.GetMethod("GetDefaultIncludes", Type.EmptyTypes));

        var paths = (IReadOnlyCollection<string>?)method?.Invoke(actions, []);
        if (paths is not { Count: > 0 })
            return null;

        // Stringly-typed safety net: a path whose first segment isn't a property of the type will
        // silently include nothing. Warn once per (type, unknown segment) so a typo is visible.
        foreach (var path in paths)
        {
            var firstSegment = path.Split('.', 2)[0];
            if (entityType.GetCachedProperty(firstSegment) is null
                && announced.TryAdd((entityType, firstSegment), true))
            {
                logger?.LogWarning(
                    "GetDefaultIncludes for {EntityType} names '{Path}', whose first segment "
                    + "'{Segment}' is not a property of the type — that include will do nothing.",
                    entityType.Name, path, firstSegment);
            }
        }

        return paths;
    }

    public IReadOnlyCollection<string> ResolveIncludePaths(Type queryType, Type entityType)
    {
        var referenceNames = GetReferenceProperties(queryType, entityType).Select(p => p.Property.Name);
        var defaults = GetDefaultIncludes(entityType) ?? [];
        return referenceNames.Concat(defaults).Distinct(StringComparer.Ordinal).ToArray();
    }
}
