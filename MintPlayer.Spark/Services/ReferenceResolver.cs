using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Reflection;
using Raven.Client.Documents.Session;
using System.Reflection;

namespace MintPlayer.Spark.Services;

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
    /// Chains .Include(propertyName) on a RavenDB IRavenQueryable so that referenced documents
    /// are loaded in the same round-trip. Returns the (possibly wrapped) queryable.
    /// </summary>
    object ApplyIncludes(object queryable, List<(PropertyInfo Property, ReferenceAttribute Attribute)> referenceProperties);

    Task<Dictionary<string, object>> ResolveReferencedDocumentsAsync(
        IAsyncDocumentSession session,
        IEnumerable<object> entities,
        List<(PropertyInfo Property, ReferenceAttribute Attribute)> referenceProperties);
}

[Register(typeof(IReferenceResolver), ServiceLifetime.Scoped)]
internal partial class ReferenceResolver : IReferenceResolver
{
    public List<(PropertyInfo Property, ReferenceAttribute Attribute)> GetReferenceProperties(Type entityType)
    {
        // Return a copy of the cached array as a List so callers can mutate (the
        // overload below appends fallback entries). The underlying array itself
        // is shared via ReflectionCache and must not be mutated.
        var cached = ReflectionCache.GetOrAdd<(PropertyInfo Property, ReferenceAttribute Attribute)[]>(
            entityType,
            static t => t.GetCachedProperties()
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
            var matchingProp = entityType.GetCachedProperty(fallbackProp.Name);
            if (matchingProp != null)
            {
                result.Add((matchingProp, refAttr));
            }
        }

        return result;
    }

    public object ApplyIncludes(object queryable, List<(PropertyInfo Property, ReferenceAttribute Attribute)> referenceProperties)
    {
        foreach (var (property, _) in referenceProperties)
        {
            var queryType = queryable.GetType();

            // Cached per (queryType, "Include(string)"): the .Include(string) MethodInfo
            // doesn't change for a given queryable type and is otherwise a fresh
            // GetMethods() scan per reference property.
            var includeMethod = ReflectionCache.GetOrAdd<MethodInfo?>(
                $"includeMethod|{queryType.FullName}",
                () => queryType.GetMethods()
                    .FirstOrDefault(m => m.Name == "Include"
                        && m.GetParameters().Length == 1
                        && m.GetParameters()[0].ParameterType == typeof(string)));

            if (includeMethod != null)
            {
                queryable = includeMethod.Invoke(queryable, [property.Name])!;
            }
        }

        return queryable;
    }

    public async Task<Dictionary<string, object>> ResolveReferencedDocumentsAsync(
        IAsyncDocumentSession session,
        IEnumerable<object> entities,
        List<(PropertyInfo Property, ReferenceAttribute Attribute)> referenceProperties)
    {
        var includedDocuments = new Dictionary<string, object>();

        if (referenceProperties.Count == 0)
            return includedDocuments;

        // Collect all unique reference IDs grouped by target type
        var refIdsByType = new Dictionary<Type, HashSet<string>>();

        foreach (var entity in entities)
        {
            foreach (var (property, refAttr) in referenceProperties)
            {
                var refId = AccessorCache.GetGetter(property)(entity) as string;
                if (string.IsNullOrEmpty(refId)) continue;

                var targetType = refAttr.TargetType;
                if (!refIdsByType.ContainsKey(targetType))
                {
                    refIdsByType[targetType] = [];
                }
                refIdsByType[targetType].Add(refId);
            }
        }

        // Load referenced documents (from session cache if .Include() was used, otherwise from database)
        foreach (var (targetType, refIds) in refIdsByType)
        {
            foreach (var refId in refIds)
            {
                var referencedEntity = await LoadEntityAsync(session, targetType, refId);
                if (referencedEntity != null)
                {
                    includedDocuments[refId] = referencedEntity;
                }
            }
        }

        return includedDocuments;
    }

    private static async Task<object?> LoadEntityAsync(IAsyncDocumentSession session, Type entityType, string id)
    {
        var genericMethod = ReflectionCache.GetOrAdd<MethodInfo?>(
            $"sessionLoadAsync|{entityType.FullName ?? entityType.Name}",
            () =>
            {
                var method = typeof(IAsyncDocumentSession).GetMethod(
                    nameof(IAsyncDocumentSession.LoadAsync),
                    [typeof(string), typeof(CancellationToken)]);
                return method?.MakeGenericMethod(entityType);
            });
        var task = genericMethod?.Invoke(session, [id, CancellationToken.None]) as Task;

        if (task == null) return null;

        await task;

        var resultProperty = task.GetType().GetCachedProperty("Result");
        return resultProperty is not null ? AccessorCache.GetGetter(resultProperty)(task) : null;
    }
}
